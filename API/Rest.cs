using System.Net.Http.Json;
using System.Text.Json;
using Brokers.Brokers;
namespace API;

public class Rest
{
    private readonly HttpClient _httpClient;
    public Rest()
    { 
        _httpClient = new HttpClient();
    }
    public async Task<List<List<object>>> Get(string url)
    {
        List<List<object>>? rawData = await _httpClient.GetFromJsonAsync<List<List<object>>>(url);
        return rawData ?? new List<List<object>>(); // Ensure it never returns null
    }
    
    public async Task<List<T>> Get<T>(string url)
    {
        var json = await _httpClient.GetStringAsync(url);
        var options = new JsonSerializerOptions();
        options.Converters.Add( new BinanceKlineConverter());
        return JsonSerializer.Deserialize<List<T>>(json, options) ?? new List<T>();
    }
}