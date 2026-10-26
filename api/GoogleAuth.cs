using System.Security.Claims;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;

static class GoogleAuth {
 public static bool Enabled=>!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID"))&&!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET"));
 public static void Configure(IServiceCollection services){
  if(!Enabled)return;
  services.AddAuthentication().AddCookie("GoogleExternal",o=>{o.Cookie.Name="cherry_google_external";o.Cookie.HttpOnly=true;o.Cookie.SecurePolicy=CookieSecurePolicy.Always;o.Cookie.SameSite=SameSiteMode.Lax;o.ExpireTimeSpan=TimeSpan.FromMinutes(5);})
  .AddOAuth("Google",o=>{
   o.SignInScheme="GoogleExternal";
   o.ClientId=Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID")!;
   o.ClientSecret=Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET")!;
   o.CallbackPath="/api/auth/google/callback";
   o.AuthorizationEndpoint="https://accounts.google.com/o/oauth2/v2/auth";
   o.TokenEndpoint="https://oauth2.googleapis.com/token";
   o.UserInformationEndpoint="https://openidconnect.googleapis.com/v1/userinfo";
   o.UsePkce=true;o.SaveTokens=false;o.Scope.Add("openid");o.Scope.Add("email");o.Scope.Add("profile");
   o.CorrelationCookie.SecurePolicy=CookieSecurePolicy.Always;
   o.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier,"sub");o.ClaimActions.MapJsonKey(ClaimTypes.Name,"name");o.ClaimActions.MapJsonKey(ClaimTypes.Email,"email");
   o.ClaimActions.MapJsonKey("email_verified","email_verified");
   o.Events=new OAuthEvents{
    OnCreatingTicket=async ctx=>{
     using var request=new HttpRequestMessage(HttpMethod.Get,ctx.Options.UserInformationEndpoint);
     request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",ctx.AccessToken);
     using var response=await ctx.Backchannel.SendAsync(request,ctx.HttpContext.RequestAborted);response.EnsureSuccessStatusCode();
     using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());ctx.RunClaimActions(doc.RootElement);
    },
    OnRemoteFailure=ctx=>{ctx.HandleResponse();ctx.Response.Redirect("/login/?auth_error=google_failed");return Task.CompletedTask;}
   };
  });
 }
 public static void Map(WebApplication app,PgDataSource db){
  app.MapGet("/api/auth/providers",()=>Results.Ok(new{google=Enabled}));
  app.MapGet("/api/auth/google",()=>Enabled?Results.Challenge(new AuthenticationProperties{RedirectUri="/api/auth/google/complete"},["Google"]):Results.Redirect("/login/?auth_error=google_not_configured"));
  app.MapGet("/api/auth/google/complete",async(HttpContext ctx)=>{
   if(!Enabled)return Results.Redirect("/login/?auth_error=google_not_configured");
   var result=await ctx.AuthenticateAsync("GoogleExternal");
   if(!result.Succeeded||result.Principal is null)return Results.Redirect("/login/?auth_error=google_failed");
   var subject=result.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
   var email=result.Principal.FindFirstValue(ClaimTypes.Email)?.Trim().ToLowerInvariant();
   var verified=result.Principal.FindFirstValue("email_verified");
   await ctx.SignOutAsync("GoogleExternal");
   if(string.IsNullOrWhiteSpace(subject)||subject.Length>255||email is null||email.Length>254||!string.Equals(verified,"true",StringComparison.OrdinalIgnoreCase))return Results.Redirect("/login/?auth_error=google_failed");
   await using var c=await db.OpenConnectionAsync();
   var existing=await Store.Query(c,"SELECT id FROM users WHERE google_sub=@sub",("sub",subject));
   Guid id;
   if(existing.Count>0)id=(Guid)existing[0]["id"]!;
   else{
    // Do not silently link a password account by email: that permits account pre-hijacking.
    if((await Store.Query(c,"SELECT id FROM users WHERE email=@email",("email",email))).Count>0)return Results.Redirect("/login/?auth_error=google_email_exists");
    id=Guid.NewGuid();var name=result.Principal.FindFirstValue(ClaimTypes.Name)??email.Split('@')[0];name=name[..Math.Min(name.Length,100)];
    await Store.Execute(c,"INSERT INTO users(id,email,name,password_hash,google_sub) VALUES(@id,@email,@name,@hash,@sub)",("id",id),("email",email),("name",name),("hash",Security.HashPassword(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)))),("sub",subject));
   }
   await Security.Login(ctx,c,id);return Results.Redirect("/account/");
  }).RequireRateLimiting("auth");
 }
}
