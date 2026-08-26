using System.Text;
using System.Text.Json;

// Shared HTTP layer. If the [nyt-cookies] section of config.txt is filled in,
// its contents are sent as the Cookie header on nytimes.com requests so a
// subscriber's own logged-in session is used for full article text.
static class Web
{
    private static readonly HttpClient Client = new();
    private static readonly string? NytCookie = LoadNytCookie();

    static Web()
    {
        Client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) DailyShell/1.0");
        // The .NET default of 100s lets one slow host stall background work (the
        // menu header) for minutes; nothing this app fetches should take this long.
        Client.Timeout = TimeSpan.FromSeconds(30);
    }

    public static async Task<Stream> GetStreamAsync(string url)
    {
        var response = await SendAsync(url);
        return await response.Content.ReadAsStreamAsync();
    }

    public static async Task<string> GetStringAsync(string url)
    {
        var response = await SendAsync(url);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<HttpResponseMessage> SendAsync(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        var host = request.RequestUri?.Host ?? string.Empty;
        if (NytCookie != null &&
            (host.Equals("nytimes.com", StringComparison.OrdinalIgnoreCase) ||
             host.EndsWith(".nytimes.com", StringComparison.OrdinalIgnoreCase)))
        {
            request.Headers.TryAddWithoutValidation("Cookie", NytCookie);
        }

        var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static string? LoadNytCookie()
    {
        // Allow either a raw Cookie header on one line or one "name=value" per line.
        var cookie = string.Join("; ", Config.Lines("nyt-cookies"));
        return cookie.Length > 0 ? cookie : null;
    }
}

// Authenticated Reddit API access. If the [reddit-oauth] section of config.txt
// has a Reddit app's client id on line 1 and secret on line 2 (create one free
// at reddit.com/prefs/apps, type 'script'), requests go through
// oauth.reddit.com — which is not bot-blocked and includes upvote counts.
static class RedditApi
{
    private static readonly HttpClient Client = CreateClient();
    private static readonly (string Id, string Secret)? Credentials = LoadCredentials();
    private static string? _token;
    private static DateTime _tokenExpiresUtc;

    public static bool HasCredentials => Credentials != null;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "windows:DailyShell:v1.0 (personal terminal news reader)");
        return client;
    }

    public static async Task<string> GetAsync(string pathAndQuery)
    {
        var token = await GetTokenAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "https://oauth.reddit.com" + pathAndQuery);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    // Application-only OAuth token (no Reddit account login involved).
    private static async Task<string> GetTokenAsync()
    {
        if (_token != null && DateTime.UtcNow < _tokenExpiresUtc) return _token;

        var (id, secret) = Credentials!.Value;
        var request = new HttpRequestMessage(HttpMethod.Post, "https://www.reddit.com/api/v1/access_token")
        {
            Content = new FormUrlEncodedContent(
                new Dictionary<string, string> { ["grant_type"] = "client_credentials" })
        };
        request.Headers.TryAddWithoutValidation("Authorization",
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{id}:{secret}")));

        var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        _token = doc.RootElement.GetProperty("access_token").GetString()
                 ?? throw new InvalidOperationException("Reddit token response had no access_token.");
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        _tokenExpiresUtc = DateTime.UtcNow.AddSeconds(expiresIn - 60);
        return _token;
    }

    private static (string, string)? LoadCredentials()
    {
        var lines = Config.Lines("reddit-oauth");
        return lines.Length >= 2 ? (lines[0], lines[1]) : null;
    }
}
