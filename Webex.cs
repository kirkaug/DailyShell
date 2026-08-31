using System.Net;
using System.Text;
using System.Text.Json;

// Webex access through an official OAuth "integration" ([webex] in config.txt
// holds the integration's client id/secret; create one free at
// developer.webex.com/my-apps). First use runs a one-time browser sign-in
// (authorization-code flow caught on a loopback listener); the resulting
// tokens live in data/webex-token.json and refresh automatically, so the link
// keeps working indefinitely with regular use (~90 days idle at most).
// Everything else is the plain public REST API at webexapis.com/v1.
static class WebexApi
{
    const string ApiBase = "https://webexapis.com/v1";

    // Must match the Redirect URI configured on the integration, character for
    // character. The listener binds the whole port (HttpListener prefixes are
    // directory-shaped, and the callback lands on the bare /webex path).
    public const string RedirectUri = "http://localhost:8442/webex";
    const string ListenerPrefix = "http://localhost:8442/";
    const string Scopes = "spark:all";

    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };
    static readonly string TokenPath = Paths.Data("webex-token.json");

    // personId -> displayName, filled lazily as messages are fetched (the
    // messages API only carries the author's id and email).
    static readonly Dictionary<string, string> PeopleNames = [];

    static TokenSet? _tokens;

    record TokenSet(string AccessToken, DateTimeOffset AccessExpiresUtc,
                    string RefreshToken, DateTimeOffset RefreshExpiresUtc);

    public static (string Id, string Secret)? Credentials
    {
        get
        {
            var lines = Config.Lines("webex");
            return lines.Length >= 2 ? (lines[0].Trim(), lines[1].Trim()) : null;
        }
    }

    // Linked = a saved refresh token that hasn't aged out. An expired one is as
    // good as none: the caller should rerun the sign-in rather than call the API.
    public static bool IsLinked =>
        LoadTokens() is { RefreshToken.Length: > 0 } t && DateTimeOffset.UtcNow < t.RefreshExpiresUtc;

    public static void Unlink()
    {
        _tokens = null;
        try { File.Delete(TokenPath); }
        catch (Exception ex) { AppLog.Debug("webex unlink", ex); }
    }

    // ----- OAuth link flow -------------------------------------------------

    public static string BuildAuthUrl(string state)
    {
        var (id, _) = Credentials ?? throw new InvalidOperationException("No Webex client id configured — see Settings > Webex.");
        return $"{ApiBase}/authorize?client_id={Uri.EscapeDataString(id)}&response_type=code" +
               $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope={Uri.EscapeDataString(Scopes)}" +
               $"&state={state}";
    }

    // Null when the port is already taken — the caller falls back to letting
    // the user paste the redirected URL by hand.
    public static HttpListener? TryStartLoopbackListener()
    {
        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(ListenerPrefix);
            listener.Start();
            return listener;
        }
        catch (Exception ex)
        {
            AppLog.Debug("webex listener", ex);
            return null;
        }
    }

    // Waits for the browser to land on the redirect URI and hands back the
    // authorization code. Stray requests (favicon and the like) are answered
    // and ignored. Cancelling the token stops the listener and aborts the wait.
    public static async Task<string> WaitForCodeAsync(HttpListener listener, string expectedState, CancellationToken ct)
    {
        using var reg = ct.Register(() => { try { listener.Stop(); } catch { /* stopping is the point */ } });
        try
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }

                var code = ctx.Request.QueryString["code"];
                var error = ctx.Request.QueryString["error"];
                if (code == null && error == null)
                {
                    Respond(ctx, 404, "DailyShell is waiting for the Webex sign-in.");
                    continue;
                }
                if (error != null)
                {
                    Respond(ctx, 200, "Sign-in was declined — you can close this tab.");
                    throw new InvalidOperationException($"Webex sign-in was declined ({error}).");
                }
                if (ctx.Request.QueryString["state"] != expectedState)
                {
                    Respond(ctx, 200, "This sign-in link is stale — return to DailyShell and try again.");
                    throw new InvalidOperationException("The sign-in response didn't match this session — try again.");
                }
                Respond(ctx, 200, "Webex is linked — you can close this tab and return to DailyShell.");
                return code!;
            }
        }
        finally
        {
            try { listener.Close(); } catch { /* already stopped */ }
        }
    }

    static void Respond(HttpListenerContext ctx, int status, string message)
    {
        try
        {
            var html = Encoding.UTF8.GetBytes(
                $"<html><body style=\"font-family:sans-serif;margin:3em\"><h2>DailyShell</h2><p>{message}</p></body></html>");
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = html.Length;
            ctx.Response.OutputStream.Write(html);
            ctx.Response.Close();
        }
        catch { /* the browser gave up — the code (if any) is already captured */ }
    }

    // Pulls the code out of a hand-pasted redirect URL
    // ("http://localhost:8442/webex?code=...&state=..."), for when the loopback
    // listener couldn't start. Null when there is no code or the state doesn't
    // match this session.
    public static string? CodeFromRedirectUrl(string url, string expectedState)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        string? code = null, state = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (pair[..eq] == "code") code = value;
            else if (pair[..eq] == "state") state = value;
        }
        return state == expectedState ? code : null;
    }

    public static async Task ExchangeCodeAsync(string code) =>
        await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
        });

    // ----- Token upkeep ----------------------------------------------------

    static async Task<string> GetTokenAsync()
    {
        var t = LoadTokens() ?? throw new InvalidOperationException(
            "Webex isn't linked yet — reopen the Webex section to sign in.");
        if (DateTimeOffset.UtcNow < t.AccessExpiresUtc - TimeSpan.FromMinutes(5)) return t.AccessToken;
        return (await RefreshAsync(t)).AccessToken;
    }

    static async Task<TokenSet> RefreshAsync(TokenSet t)
    {
        if (DateTimeOffset.UtcNow >= t.RefreshExpiresUtc)
        {
            Unlink();
            throw new InvalidOperationException("The Webex link has expired — open the Webex section to sign in again.");
        }
        return await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = t.RefreshToken,
        }, unlinkOnRejection: true);
    }

    static async Task<TokenSet> RequestTokenAsync(Dictionary<string, string> form, bool unlinkOnRejection = false)
    {
        var (id, secret) = Credentials ?? throw new InvalidOperationException(
            "No Webex client id/secret configured — see Settings > Webex.");
        form["client_id"] = id;
        form["client_secret"] = secret;

        var response = await Client.PostAsync($"{ApiBase}/access_token", new FormUrlEncodedContent(form));
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            AppLog.Debug($"webex token: {(int)response.StatusCode} {body}");
            // A rejected grant is dead (revoked, or the integration changed) —
            // drop it so the next visit reruns the sign-in instead of failing
            // forever. Network/server errors keep the tokens for a later retry.
            if (unlinkOnRejection &&
                response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                Unlink();
                throw new InvalidOperationException(
                    "Webex rejected the saved sign-in — open the Webex section to sign in again.");
            }
            throw new InvalidOperationException($"Webex token request failed ({(int)response.StatusCode}).");
        }
        return SaveTokenResponse(body);
    }

    static TokenSet SaveTokenResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var now = DateTimeOffset.UtcNow;
        var t = new TokenSet(
            root.GetProperty("access_token").GetString()!,
            now.AddSeconds(root.TryGetProperty("expires_in", out var e) ? e.GetInt64() : 14 * 24 * 3600),
            root.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()! : _tokens?.RefreshToken ?? "",
            now.AddSeconds(root.TryGetProperty("refresh_token_expires_in", out var re) ? re.GetInt64() : 90 * 24 * 3600));
        _tokens = t;
        try
        {
            File.WriteAllText(TokenPath, JsonSerializer.Serialize(t, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { AppLog.Debug("webex token save", ex); }
        return t;
    }

    static TokenSet? LoadTokens()
    {
        if (_tokens != null) return _tokens;
        try
        {
            if (File.Exists(TokenPath))
                _tokens = JsonSerializer.Deserialize<TokenSet>(File.ReadAllText(TokenPath));
        }
        catch (Exception ex) { AppLog.Debug("webex token load", ex); }
        return _tokens;
    }

    // ----- REST calls ------------------------------------------------------

    // Spaces the account is in, most recently active first.
    public static async Task<List<WebexRoom>> GetRoomsAsync()
    {
        using var doc = JsonDocument.Parse(
            await SendAsync(HttpMethod.Get, $"{ApiBase}/rooms?max=250&sortBy=lastactivity"));
        var rooms = new List<WebexRoom>();
        foreach (var r in doc.RootElement.GetProperty("items").EnumerateArray())
            rooms.Add(new WebexRoom(
                r.GetProperty("id").GetString()!,
                r.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()! : "(unnamed space)",
                r.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String && ty.GetString() == "direct",
                r.TryGetProperty("lastActivity", out var la) && la.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(la.GetString()!).ToLocalTime() : DateTimeOffset.MinValue));
        return rooms;
    }

    // Newest-first from the API; returned oldest-first for display.
    // beforeMessageId = null fetches the latest messages.
    public static async Task<List<WebexMessage>> GetMessagesAsync(string roomId, string? beforeMessageId = null, int limit = 50)
    {
        var url = $"{ApiBase}/messages?roomId={Uri.EscapeDataString(roomId)}&max={limit}" +
                  (beforeMessageId != null ? $"&beforeMessage={Uri.EscapeDataString(beforeMessageId)}" : "");
        using var doc = JsonDocument.Parse(await SendAsync(HttpMethod.Get, url));

        var raw = new List<(string Id, string PersonId, string PersonEmail, DateTimeOffset Created, string Text)>();
        foreach (var m in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            var parts = new List<string>();
            if (m.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String && t.GetString()!.Length > 0)
                parts.Add(t.GetString()!);
            // File URLs point at the API and need the bearer token, so a browser
            // can't open them — note their presence instead of linking them.
            if (m.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array &&
                files.GetArrayLength() > 0)
                parts.Add($"[{files.GetArrayLength()} attachment{(files.GetArrayLength() == 1 ? "" : "s")} — open the Webex app to view]");
            if (m.TryGetProperty("attachments", out var cards) && cards.ValueKind == JsonValueKind.Array &&
                cards.GetArrayLength() > 0)
                parts.Add("[interactive card — open the Webex app to respond]");
            if (parts.Count == 0) parts.Add("(no text)");

            raw.Add((
                m.GetProperty("id").GetString()!,
                m.TryGetProperty("personId", out var pid) && pid.ValueKind == JsonValueKind.String ? pid.GetString()! : "",
                m.TryGetProperty("personEmail", out var pe) && pe.ValueKind == JsonValueKind.String ? pe.GetString()! : "",
                m.TryGetProperty("created", out var c) && c.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(c.GetString()!).ToLocalTime() : DateTimeOffset.MinValue,
                string.Join("\n", parts)));
        }
        raw.Reverse();

        var messages = new List<WebexMessage>();
        foreach (var m in raw)
            messages.Add(new WebexMessage(m.Id, await DisplayNameAsync(m.PersonId, m.PersonEmail), m.Created, m.Text));
        return messages;
    }

    public static Task SendMessageAsync(string roomId, string text) =>
        SendAsync(HttpMethod.Post, $"{ApiBase}/messages",
            JsonSerializer.Serialize(new { roomId, text }));

    // The linked account's display name — shown once after the OAuth link as a
    // "signed in as" confirmation.
    public static async Task<string> GetMyNameAsync()
    {
        using var doc = JsonDocument.Parse(await SendAsync(HttpMethod.Get, $"{ApiBase}/people/me"));
        return doc.RootElement.TryGetProperty("displayName", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()! : "this account";
    }

    // One person lookup per unique author per app session; falls back to the
    // email's local part when the lookup fails (left the org, hidden, etc.).
    static async Task<string> DisplayNameAsync(string personId, string personEmail)
    {
        if (personId.Length == 0) return EmailName(personEmail);
        if (PeopleNames.TryGetValue(personId, out var cached)) return cached;
        try
        {
            using var doc = JsonDocument.Parse(
                await SendAsync(HttpMethod.Get, $"{ApiBase}/people/{Uri.EscapeDataString(personId)}"));
            var name = doc.RootElement.TryGetProperty("displayName", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()! : EmailName(personEmail);
            return PeopleNames[personId] = name;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"webex person {personId}", ex);
            return PeopleNames[personId] = EmailName(personEmail);
        }
    }

    static string EmailName(string email) =>
        email.Length == 0 ? "unknown" : email.Split('@')[0];

    static async Task<string> SendAsync(HttpMethod method, string url, string? jsonBody = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + await GetTokenAsync());
            if (jsonBody != null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            var response = await Client.SendAsync(request);

            // A 401 on a token that looked fresh means it was revoked server-side;
            // one forced refresh usually recovers without bothering the user.
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && LoadTokens() is { } t)
            {
                try { await RefreshAsync(t); continue; }
                catch (Exception ex) { AppLog.Debug("webex forced refresh", ex); }
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new InvalidOperationException("Webex rejected the token — open the Webex section to sign in again.");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new InvalidOperationException("Webex refused — this account can't view or post there.");
            if ((int)response.StatusCode == 429)
                throw new InvalidOperationException("Webex rate-limited the request — wait a moment and try again.");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
    }
}

// Local per-space read markers (data/webex-seen.json). Webex's public API has
// no read-state endpoint, so "unread" here means: activity since this app last
// showed the space. Spaces seen for the first time are baselined as read so a
// fresh install doesn't flag everything at once.
static class WebexSeen
{
    static readonly string FilePath = Paths.Data("webex-seen.json");
    static Dictionary<string, DateTimeOffset>? _map;

    static Dictionary<string, DateTimeOffset> Load()
    {
        if (_map != null) return _map;
        try
        {
            _map = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (Exception ex)
        {
            AppLog.Debug("webex seen load", ex);
            _map = [];
        }
        return _map;
    }

    public static DateTimeOffset LastSeen(string roomId) =>
        Load().TryGetValue(roomId, out var t) ? t : DateTimeOffset.MinValue;

    public static void MarkSeen(string roomId, DateTimeOffset at)
    {
        Load()[roomId] = at;
        Save();
    }

    public static void BaselineMissing(IEnumerable<WebexRoom> rooms)
    {
        var map = Load();
        var changed = false;
        foreach (var r in rooms)
            if (!map.ContainsKey(r.Id))
            {
                map[r.Id] = r.LastActivity;
                changed = true;
            }
        if (changed) Save();
    }

    static void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { AppLog.Debug("webex seen save", ex); }
    }
}
