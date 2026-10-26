using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

record ShippingPlace(string Ref,string Name);
sealed class NovaPoshta(IHttpClientFactory clients,IMemoryCache cache){
 async Task<List<JsonElement>> Call(string method,Dictionary<string,string> properties){
  var key=method+JsonSerializer.Serialize(properties);
  if(cache.TryGetValue<List<JsonElement>>(key,out var cached))return cached!;
  try{
   using var client=clients.CreateClient("nova-poshta");
   using var response=await client.PostAsJsonAsync("https://api.novaposhta.ua/v2.0/json/",new{apiKey=Environment.GetEnvironmentVariable("NOVA_POSHTA_API_KEY")??"",modelName="Address",calledMethod=method,methodProperties=properties});
   response.EnsureSuccessStatusCode();using var document=JsonDocument.Parse(await response.Content.ReadAsStringAsync());var root=document.RootElement;
   if(!root.GetProperty("success").GetBoolean())throw new ApiException(503,"shipping_unavailable");
   var result=root.GetProperty("data").EnumerateArray().Select(x=>x.Clone()).ToList();cache.Set(key,result,TimeSpan.FromMinutes(15));return result;
  }catch(ApiException){throw;}catch{throw new ApiException(503,"shipping_unavailable");}
 }
 public async Task<List<ShippingPlace>> Cities(string query){
  if(query.Length<2)return [];if(query.Length>100)throw new ApiException(400,"invalid_shipping");
  return (await Call("getCities",new(){{"FindByString",query},{"Limit","20"},{"Page","1"}})).Select(x=>new ShippingPlace(x.GetProperty("Ref").GetString()!,x.GetProperty("Description").GetString()!)).ToList();
 }
 public async Task<List<ShippingPlace>> Warehouses(string city,string query){
  if(!Guid.TryParse(city,out _)||query.Length>100)throw new ApiException(400,"invalid_shipping");
  return (await Call("getWarehouses",new(){{"CityRef",city},{"FindByString",query},{"Limit","50"},{"Page","1"}})).Select(x=>new ShippingPlace(x.GetProperty("Ref").GetString()!,x.GetProperty("Description").GetString()!)).ToList();
 }
 public async Task<(string city,string warehouse)> Validate(string? city,string? warehouse){
  if(!Guid.TryParse(city,out _)||!Guid.TryParse(warehouse,out _))throw new ApiException(400,"invalid_shipping");
  var places=await Call("getWarehouses",new(){{"CityRef",city!},{"Ref",warehouse!},{"Limit","1"},{"Page","1"}});
  var place=places.FirstOrDefault(p=>p.GetProperty("Ref").GetString()==warehouse&&p.GetProperty("CityRef").GetString()==city);
  if(place.ValueKind==JsonValueKind.Undefined)throw new ApiException(400,"invalid_shipping");
  return(place.GetProperty("CityDescription").GetString()!,place.GetProperty("Description").GetString()!);
 }
}
