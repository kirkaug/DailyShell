using System.Text;
using System.Text.Json;

// Google Gemini chat via the official Generative Language API ([gemini] in
// config.txt — a free key from aistudio.google.com/app/apikey). Conversations
// live locally as JSON files under data/gemini/; there is no API for reading
// gemini.google.com web history, so the section's history is what was chatted
// here.
static class GeminiApi
{
    const string DefaultModel = "gemini-2.5-flash";
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(2) };

    public static string? ApiKey =>
        Config.Lines("gemini").FirstOrDefault(l => !l.Contains('='))?.Trim().Trim('"');

    // Optional "model = name" line in the [gemini] section.
    public static string Model
    {
        get
        {
            foreach (var line in Config.Lines("gemini"))
            {
                var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && parts[0].Equals("model", StringComparison.OrdinalIgnoreCase)
                    && parts[1].Length > 0)
                    return parts[1];
            }
            return DefaultModel;
        }
    }

    // Sends the whole conversation (the API is stateless) and returns the
    // model's reply text.
    public static async Task<string> ChatAsync(IReadOnlyList<GeminiMessage> messages)
    {
        var key = ApiKey ?? throw new InvalidOperationException("No Gemini API key configured.");

        var body = JsonSerializer.Serialize(new
        {
            contents = messages.Select(m => new
            {
                role = m.Role,
                parts = new[] { new { text = m.Text } }
            })
        });

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("x-goog-api-key", key);

        var response = await Client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ExtractApiError(json)
                ?? $"Gemini returned {(int)response.StatusCode} {response.ReasonPhrase}.");

        using var doc = JsonDocument.Parse(json);
        var candidates = doc.RootElement.GetProperty("candidates");
        if (candidates.GetArrayLength() == 0)
            throw new InvalidOperationException("Gemini returned no reply (the prompt may have been blocked).");

        var sb = new StringBuilder();
        foreach (var part in candidates[0].GetProperty("content").GetProperty("parts").EnumerateArray())
            if (part.TryGetProperty("text", out var t)) sb.Append(t.GetString());

        var text = sb.ToString().Trim();
        return text.Length > 0 ? text : "(empty reply)";
    }

    // The API's error payload carries a human-readable message ("API key not
    // valid...", quota details); surface that instead of a bare status code.
    static string? ExtractApiError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("error").GetProperty("message").GetString();
        }
        catch
        {
            return null;
        }
    }
}

// One turn of a conversation. Role is "user" or "model" (the API's names).
record GeminiMessage(string Role, string Text, DateTime Time);

// A saved conversation: data/gemini/<id>.json. Title comes from the first
// user message; Updated orders the conversation list.
sealed class GeminiChat
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTime Updated { get; set; }
    public List<GeminiMessage> Messages { get; set; } = [];

    static string Dir
    {
        get
        {
            var dir = Paths.Data("gemini");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static GeminiChat CreateNew() => new()
    {
        Id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"),
        Model = GeminiApi.Model,
        Updated = DateTime.Now,
    };

    public static List<GeminiChat> LoadAll()
    {
        var chats = new List<GeminiChat>();
        foreach (var file in Directory.GetFiles(Dir, "*.json"))
        {
            try
            {
                var chat = JsonSerializer.Deserialize<GeminiChat>(File.ReadAllText(file));
                if (chat is { Messages.Count: > 0 }) chats.Add(chat);
            }
            catch (Exception ex) { AppLog.Debug($"gemini chat load {file}", ex); }
        }
        return chats.OrderByDescending(c => c.Updated).ToList();
    }

    public void Save()
    {
        Updated = DateTime.Now;
        if (Title.Length == 0 && Messages.FirstOrDefault(m => m.Role == "user") is { } first)
            Title = first.Text.Length > 60 ? first.Text[..60].TrimEnd() + "…" : first.Text;
        File.WriteAllText(Path.Combine(Dir, Id + ".json"),
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Delete()
    {
        try { File.Delete(Path.Combine(Dir, Id + ".json")); }
        catch (Exception ex) { AppLog.Debug("gemini chat delete", ex); }
    }
}
