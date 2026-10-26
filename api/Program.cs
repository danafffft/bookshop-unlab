using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Mail;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;


var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? ("http://+:" + (Environment.GetEnvironmentVariable("PORT") ?? "5080")));
var connection = Environment.GetEnvironmentVariable("DATABASE_URL") ?? throw new Exception("DATABASE_URL is required");
builder.Services.AddSingleton(PgDataSource.Create(connection));
builder.Services.AddRateLimiter(o => {
 o.RejectionStatusCode=429;
 o.AddPolicy("auth",c=>RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString()??"local",_=>new FixedWindowRateLimiterOptions{PermitLimit=30,Window=TimeSpan.FromMinutes(10),QueueLimit=0}));
});
var app=builder.Build();
var db=app.Services.GetRequiredService<PgDataSource>();
await using(var init=await db.OpenConnectionAsync()){
 await Store.Execute(init,await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"schema.sql")));
 var books=JsonSerializer.Deserialize<List<BookInput>>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"books.json")),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
 foreach(var b in books) await Store.SaveBook(init,b,true);
 var email=Environment.GetEnvironmentVariable("ADMIN_EMAIL");var password=Environment.GetEnvironmentVariable("ADMIN_PASSWORD");
 if(!string.IsNullOrWhiteSpace(email)&&!string.IsNullOrWhiteSpace(password)){
  if(password.Length<12)throw new Exception("ADMIN_PASSWORD must have at least 12 characters");
  await Store.Execute(init,"INSERT INTO users(id,email,name,password_hash,role) VALUES(@id,@email,'Administrator',@hash,'admin') ON CONFLICT(email) DO NOTHING",("id",Guid.NewGuid()),("email",email.Trim().ToLowerInvariant()),("hash",Security.HashPassword(password)));
 }
}
app.Use(async(c,next)=>{
 try{
  c.Response.Headers.CacheControl="no-store";
  if(c.Request.ContentLength>65536){c.Response.StatusCode=413;return;}
  if(c.Request.Method is not ("GET" or "HEAD" or "OPTIONS")){
   var origin=c.Request.Headers.Origin.ToString();
   var allowed=Environment.GetEnvironmentVariable("PUBLIC_ORIGIN")??"http://localhost:8080";
   if(origin!=allowed){await Api.Error(c,403,"invalid_origin");return;}
  }
  await next();
 }catch(ApiException e){await Api.Error(c,e.Status,e.Message);}
 catch(PgException e) when(e.SqlState=="23505"){await Api.Error(c,409,"already_exists");}
 catch(Exception e){app.Logger.LogError(e,"Request failed");await Api.Error(c,500,"server_error");}
});
app.UseRateLimiter();
app.MapGet("/api/health",async()=>{await using var c=await db.OpenConnectionAsync();await Store.Query(c,"SELECT 1 AS ok");return Results.Ok(new{status="ok"});});
app.MapGet("/api/books",async()=>{await using var c=await db.OpenConnectionAsync();return Results.Ok(await Store.Query(c,"SELECT * FROM books WHERE active=true ORDER BY featured DESC,title_en"));});
app.MapGet("/api/auth/me",async(HttpContext ctx)=>{await using var c=await db.OpenConnectionAsync();return Results.Ok(new{user=await Security.User(ctx,c,false)});});
app.MapPost("/api/auth/register",async(HttpContext ctx,RegisterInput input)=>{
 var email=input.Email.Trim().ToLowerInvariant();
 if(!MailAddress.TryCreate(email,out var mail)||mail.Address!=email||email.Length>254||input.Name.Trim().Length is <2 or >100||input.Password.Length is <12 or >128)throw new ApiException(400,"invalid_registration");
 await using var c=await db.OpenConnectionAsync();var id=Guid.NewGuid();
 await Store.Execute(c,"INSERT INTO users(id,email,name,password_hash) VALUES(@id,@email,@name,@hash)",("id",id),("email",email),("name",input.Name.Trim()),("hash",Security.HashPassword(input.Password)));
 await Security.Login(ctx,c,id);return Results.Ok(new{user=new{id,email,name=input.Name.Trim(),role="customer"}});
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/login",async(HttpContext ctx,LoginInput input)=>{
 await using var c=await db.OpenConnectionAsync();
 var rows=await Store.Query(c,"SELECT id,email,name,role,password_hash FROM users WHERE email=@email",("email",input.Email.Trim().ToLowerInvariant()));
 var hash=rows.Count==0?Security.DummyHash:(string)rows[0]["passwordHash"]!;
 if(!Security.Verify(input.Password,hash)||rows.Count==0)throw new ApiException(401,"invalid_credentials");
 var u=rows[0];u.Remove("passwordHash");await Security.Login(ctx,c,(Guid)u["id"]!);return Results.Ok(new{user=u});
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/logout",async(HttpContext ctx)=>{await using var c=await db.OpenConnectionAsync();if(ctx.Request.Cookies.TryGetValue("cherry_session",out var token))await Store.Execute(c,"DELETE FROM sessions WHERE token_hash=@hash",("hash",Security.TokenHash(token)));ctx.Response.Cookies.Delete("cherry_session");return Results.Ok(new{ok=true});});
app.MapGet("/api/orders",async(HttpContext ctx)=>{await using var c=await db.OpenConnectionAsync();var u=await Security.User(ctx,c);return Results.Ok(await Store.Orders(c,(Guid)u!["id"]!));});
app.MapPost("/api/orders",async(HttpContext ctx,CheckoutInput input)=>{
 if(input.Items is null||input.Items.Length is <1 or >40||input.Items.Any(i=>i.Quantity is <1 or >20)||input.Items.Select(i=>i.BookId).Distinct().Count()!=input.Items.Length||!Api.Text(input.Name,2,100)||!Api.Text(input.Phone,7,25)||!Api.Text(input.City,2,100)||!Api.Text(input.Address,3,250))throw new ApiException(400,"invalid_checkout");
 await using var c=await db.OpenConnectionAsync();var u=await Security.User(ctx,c);
 await using var tx=await c.BeginTransactionAsync();var id=Guid.NewGuid();var lines=new List<(Dictionary<string,object?> book,int qty)>();int total=0;
 // Stable lock order avoids deadlocks between multi-book orders.
 foreach(var item in input.Items.OrderBy(x=>x.BookId)){
  var rows=await Store.Query(c,"SELECT * FROM books WHERE id=@id AND active=true FOR UPDATE",("id",item.BookId));
  if(rows.Count==0||(int)rows[0]["stock"]!<item.Quantity)throw new ApiException(409,"out_of_stock");
  total=checked(total+(int)rows[0]["price"]!*item.Quantity);lines.Add((rows[0],item.Quantity));
 }
 await Store.Execute(c,"INSERT INTO orders(id,user_id,name,phone,city,address,total) VALUES(@id,@uid,@name,@phone,@city,@address,@total)",("id",id),("uid",u!["id"]!),("name",input.Name.Trim()),("phone",input.Phone.Trim()),("city",input.City.Trim()),("address",input.Address.Trim()),("total",total));
 foreach(var (b,q) in lines){
  await Store.Execute(c,"INSERT INTO order_items(order_id,book_id,title_uk,title_en,price,quantity) VALUES(@oid,@bid,@uk,@en,@price,@q)",("oid",id),("bid",b["id"]!),("uk",b["titleUk"]!),("en",b["titleEn"]!),("price",b["price"]!),("q",q));
  await Store.Execute(c,"UPDATE books SET stock=stock-@q WHERE id=@id",("q",q),("id",b["id"]!));
 }
 await tx.CommitAsync();return Results.Ok(new{id,total,status="pending"});
});
app.MapGet("/api/admin/books",async(HttpContext ctx)=>{await using var c=await db.OpenConnectionAsync();await Security.Admin(ctx,c);return Results.Ok(await Store.Query(c,"SELECT * FROM books ORDER BY title_en"));});
app.MapPut("/api/admin/books/{id:guid}",async(HttpContext ctx,Guid id,BookInput b)=>{
 await using var c=await db.OpenConnectionAsync();await Security.Admin(ctx,c);
 if(b.Id!=id||!b.Valid())throw new ApiException(400,"invalid_book");await Store.SaveBook(c,b);return Results.Ok(new{ok=true});
});
app.MapPost("/api/admin/books",async(HttpContext ctx,BookInput b)=>{await using var c=await db.OpenConnectionAsync();await Security.Admin(ctx,c);b=b with{Id=Guid.NewGuid()};if(!b.Valid())throw new ApiException(400,"invalid_book");await Store.SaveBook(c,b);return Results.Ok(new{id=b.Id});});
app.MapGet("/api/admin/orders",async(HttpContext ctx)=>{await using var c=await db.OpenConnectionAsync();await Security.Admin(ctx,c);return Results.Ok(await Store.Orders(c,null));});
app.MapPatch("/api/admin/orders/{id:guid}",async(HttpContext ctx,Guid id,StatusInput input)=>{
 await using var c=await db.OpenConnectionAsync();await Security.Admin(ctx,c);await using var tx=await c.BeginTransactionAsync();
 var rows=await Store.Query(c,"SELECT status FROM orders WHERE id=@id FOR UPDATE",("id",id));if(rows.Count==0)throw new ApiException(404,"not_found");var old=(string)rows[0]["status"]!;
 var valid=old switch{"pending"=>input.Status is "confirmed" or "cancelled","confirmed"=>input.Status is "shipped" or "cancelled","shipped"=>input.Status=="completed",_=>false};
 if(!valid)throw new ApiException(409,"invalid_transition");
 if(input.Status=="cancelled")await Store.Execute(c,"UPDATE books b SET stock=b.stock+i.quantity FROM order_items i WHERE i.order_id=@id AND i.book_id=b.id",("id",id));
 await Store.Execute(c,"UPDATE orders SET status=@status WHERE id=@id",("id",id),("status",input.Status));await tx.CommitAsync();return Results.Ok(new{ok=true});
});
app.Run();

record RegisterInput(string Email,string Name,string Password);
record LoginInput(string Email,string Password);
record CheckoutInput(string Name,string Phone,string City,string Address,CartItem[] Items);
record CartItem(Guid BookId,int Quantity);
record StatusInput(string Status);
record BookInput(Guid Id,string TitleUk,string TitleEn,string AuthorUk,string AuthorEn,string Category,string Isbn,string DescriptionUk,string DescriptionEn,int Price,int Stock,int Pages,string Language,string Cover,bool Featured,bool Active=true){
 public bool Valid()=>Api.Text(TitleUk,1,200)&&Api.Text(TitleEn,1,200)&&Api.Text(AuthorUk,1,200)&&Api.Text(AuthorEn,1,200)&&new[]{"fiction","nonfiction","children","classics","fantasy"}.Contains(Category)&&Isbn.Length is 10 or 13&&Isbn.All(char.IsDigit)&&Api.Text(DescriptionUk,1,4000)&&Api.Text(DescriptionEn,1,4000)&&Price is >0 and <=10000000&&Stock is >=0 and <=100000&&Pages is >0 and <10000&&Api.Text(Language,1,50)&&Uri.TryCreate(Cover,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.Host=="covers.openlibrary.org";
}
class ApiException(int status,string message):Exception(message){public int Status=>status;}
static class Api{
 public static bool Text(string? v,int min,int max)=>v is not null&&v.Trim().Length>=min&&v.Length<=max;
 public static async Task Error(HttpContext c,int status,string error){c.Response.StatusCode=status;await c.Response.WriteAsJsonAsync(new{error});}
}
static class Security{
 public static readonly string DummyHash=HashPassword("dummy-password-for-timing");
 public static string HashPassword(string password){var salt=RandomNumberGenerator.GetBytes(16);var hash=Rfc2898DeriveBytes.Pbkdf2(password,salt,210000,HashAlgorithmName.SHA512,32);return Convert.ToBase64String(salt)+":"+Convert.ToBase64String(hash);}
 public static bool Verify(string password,string stored){if(password.Length>128)return false;var p=stored.Split(':');return CryptographicOperations.FixedTimeEquals(Rfc2898DeriveBytes.Pbkdf2(password,Convert.FromBase64String(p[0]),210000,HashAlgorithmName.SHA512,32),Convert.FromBase64String(p[1]));}
 public static string TokenHash(string token)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
 public static async Task Login(HttpContext ctx,PgConnection c,Guid id){
  if(ctx.Request.Cookies.TryGetValue("cherry_session",out var old))await Store.Execute(c,"DELETE FROM sessions WHERE token_hash=@hash",("hash",TokenHash(old)));
  await Store.Execute(c,"DELETE FROM sessions WHERE expires_at<now()");var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
  await Store.Execute(c,"INSERT INTO sessions(token_hash,user_id,expires_at) VALUES(@hash,@id,now()+interval '7 days')",("hash",TokenHash(token)),("id",id));
  ctx.Response.Cookies.Append("cherry_session",token,new CookieOptions{HttpOnly=true,Secure=Environment.GetEnvironmentVariable("COOKIE_SECURE")!="false",SameSite=SameSiteMode.Strict,MaxAge=TimeSpan.FromDays(7),Path="/",IsEssential=true});
 }
 public static async Task<Dictionary<string,object?>?> User(HttpContext ctx,PgConnection c,bool required=true){
  if(ctx.Request.Cookies.TryGetValue("cherry_session",out var token)){
   var rows=await Store.Query(c,"SELECT u.id,u.email,u.name,u.role FROM users u JOIN sessions s ON s.user_id=u.id WHERE s.token_hash=@hash AND s.expires_at>now()",("hash",TokenHash(token)));if(rows.Count>0)return rows[0];
  }
  if(required)throw new ApiException(401,"unauthorized");return null;
 }
 public static async Task Admin(HttpContext ctx,PgConnection c){var user=await User(ctx,c);if((string)user!["role"]! !="admin")throw new ApiException(403,"forbidden");}
}
static class Store{
 public static Task<int> Execute(PgConnection c,string sql,params (string,object)[] args)=>Task.FromResult(c.Run(sql,args).affected);
 public static Task<List<Dictionary<string,object?>>> Query(PgConnection c,string sql,params (string,object)[] args)=>Task.FromResult(c.Run(sql,args).rows);
 public static async Task SaveBook(PgConnection c,BookInput b,bool seed=false){
  var conflict=seed?"DO NOTHING":"DO UPDATE SET title_uk=EXCLUDED.title_uk,title_en=EXCLUDED.title_en,author_uk=EXCLUDED.author_uk,author_en=EXCLUDED.author_en,category=EXCLUDED.category,isbn=EXCLUDED.isbn,description_uk=EXCLUDED.description_uk,description_en=EXCLUDED.description_en,price=EXCLUDED.price,stock=EXCLUDED.stock,pages=EXCLUDED.pages,language=EXCLUDED.language,cover=EXCLUDED.cover,featured=EXCLUDED.featured,active=EXCLUDED.active";
  await Execute(c,"INSERT INTO books(id,title_uk,title_en,author_uk,author_en,category,isbn,description_uk,description_en,price,stock,pages,language,cover,featured,active) VALUES(@id,@tu,@te,@au,@ae,@cat,@isbn,@du,@de,@price,@stock,@pages,@lang,@cover,@feat,@active) ON CONFLICT(id) "+conflict,("id",b.Id),("tu",b.TitleUk),("te",b.TitleEn),("au",b.AuthorUk),("ae",b.AuthorEn),("cat",b.Category),("isbn",b.Isbn),("du",b.DescriptionUk),("de",b.DescriptionEn),("price",b.Price),("stock",b.Stock),("pages",b.Pages),("lang",b.Language),("cover",b.Cover),("feat",b.Featured),("active",b.Active));
 }
 public static async Task<List<Dictionary<string,object?>>> Orders(PgConnection c,Guid? uid){var rows=uid.HasValue?await Query(c,"SELECT * FROM orders WHERE user_id=@uid ORDER BY created_at DESC",("uid",uid.Value)):await Query(c,"SELECT * FROM orders ORDER BY created_at DESC LIMIT 500");foreach(var row in rows)row["items"]=await Query(c,"SELECT * FROM order_items WHERE order_id=@id",("id",row["id"]!));return rows;}
}


