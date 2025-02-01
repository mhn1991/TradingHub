namespace API;

public class Rest
{
    private readonly HttpClient _httpClient;
    public Rest()
    { 
        _httpClient = new HttpClient();
    }
    public async Task Get(string url)
    {
        HttpResponseMessage response = await this._httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        string responseBody = await response.Content.ReadAsStringAsync();
        Console.WriteLine(responseBody);
    }
}