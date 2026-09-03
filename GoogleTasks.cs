using System.Net;
using System.Text;
using System.Text.Json;

// Google Tasks through the official REST API ([google-tasks] in config.txt
// holds an OAuth "Desktop app" client id/secret from console.cloud.google.com;
// setup steps are in the section's help). First use runs a one-time browser
// sign-in (authorization-code flow caught on a loopback listener, the same
// shape as the Webex link); tokens live in data/google-tasks-token.json and
// refresh automatically. Note: while the Google Cloud app's publishing status
// is "Testing", Google expires the refresh token after 7 days — setting the
// app to "In production" (unverified is fine for personal use) makes it stick.
static class GoogleTasksApi
{
    const string ApiBase = "https://tasks.googleapis.com/tasks/v1";
    const string AuthBase = "https://accounts.google.com/o/oauth2/v2/auth";
    const string TokenUrl = "https://oauth2.googleapis.com/token";
    const string Scope = "https://www.googleapis.com/auth/tasks";

    // Loopback catch for the OAuth redirect; Desktop-app clients may use any
    // localhost URI, so only this app's config needs to know it.
    public const string RedirectUri = "http://localhost:8443/gtasks";
    const string ListenerPrefix = "http://localhost:8443/";

    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };
    static readonly string TokenPath = Paths.Data("google-tasks-token.json");

    static TokenSet? _tokens;

    record TokenSet(string AccessToken, DateTimeOffset AccessExpiresUtc, string RefreshToken);

    public static (string Id, string Secret)? Credentials
    {
        get
        {
            var lines = Config.Lines("google-tasks");
            return lines.Length >= 2 ? (lines[0].Trim(), lines[1].Trim()) : null;
        }
    }

    public static bool IsLinked => LoadTokens() is { RefreshToken.Length: > 0 };

    public static void Unlink()
    {
        _tokens = null;
        try { File.Delete(TokenPath); }
        catch (Exception ex) { AppLog.Debug("gtasks unlink", ex); }
    }

    // ----- OAuth link flow (mirrors the Webex link) --------------------------

    public static string BuildAuthUrl(string state)
    {
        var (id, _) = Credentials ?? throw new InvalidOperationException(
            "No Google Tasks client id configured — see Settings > Google Tasks.");
        return $"{AuthBase}?client_id={Uri.EscapeDataString(id)}&response_type=code" +
               $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope={Uri.EscapeDataString(Scope)}" +
               "&access_type=offline&prompt=consent" + // always mint a refresh token
               $"&state={state}";
    }

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
            AppLog.Debug("gtasks listener", ex);
            return null;
        }
    }

    // Waits for the browser to land on the redirect URI and hands back the
    // authorization code; stray requests are answered and ignored.
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
                    Respond(ctx, 404, "DailyShell is waiting for the Google sign-in.");
                    continue;
                }
                if (error != null)
                {
                    Respond(ctx, 200, "Sign-in was declined — you can close this tab.");
                    throw new InvalidOperationException($"Google sign-in was declined ({error}).");
                }
                if (ctx.Request.QueryString["state"] != expectedState)
                {
                    Respond(ctx, 200, "This sign-in link is stale — return to DailyShell and try again.");
                    throw new InvalidOperationException("The sign-in response didn't match this session — try again.");
                }
                Respond(ctx, 200, "Google Tasks is linked — you can close this tab and return to DailyShell.");
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

    // Pulls the code out of a hand-pasted redirect URL, for when the loopback
    // listener couldn't start (port 8443 taken).
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

    // ----- Token upkeep ------------------------------------------------------

    static async Task<string> GetTokenAsync()
    {
        var t = LoadTokens() ?? throw new InvalidOperationException(
            "Google Tasks isn't linked yet — reopen the Tasks section to sign in.");
        if (DateTimeOffset.UtcNow < t.AccessExpiresUtc - TimeSpan.FromMinutes(5)) return t.AccessToken;
        return (await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = t.RefreshToken,
        }, unlinkOnRejection: true)).AccessToken;
    }

    static async Task<TokenSet> RequestTokenAsync(Dictionary<string, string> form, bool unlinkOnRejection = false)
    {
        var (id, secret) = Credentials ?? throw new InvalidOperationException(
            "No Google Tasks client id/secret configured — see Settings > Google Tasks.");
        form["client_id"] = id;
        form["client_secret"] = secret;

        var response = await Client.PostAsync(TokenUrl, new FormUrlEncodedContent(form));
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            AppLog.Debug($"gtasks token: {(int)response.StatusCode} {body}");
            // invalid_grant = revoked or (in Testing mode) the 7-day expiry —
            // drop it so the next visit reruns the sign-in instead of failing
            // forever. Network/server errors keep the tokens for a later retry.
            if (unlinkOnRejection &&
                response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                Unlink();
                throw new InvalidOperationException(
                    "Google rejected the saved sign-in (links expire weekly while the Cloud " +
                    "app is in 'Testing') — open the Tasks section to sign in again.");
            }
            throw new InvalidOperationException($"Google token request failed ({(int)response.StatusCode}).");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var t = new TokenSet(
            root.GetProperty("access_token").GetString()!,
            DateTimeOffset.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out var e) ? e.GetInt64() : 3600),
            root.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()! : _tokens?.RefreshToken ?? "");
        _tokens = t;
        try
        {
            File.WriteAllText(TokenPath, JsonSerializer.Serialize(t, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { AppLog.Debug("gtasks token save", ex); }
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
        catch (Exception ex) { AppLog.Debug("gtasks token load", ex); }
        return _tokens;
    }

    // ----- REST calls --------------------------------------------------------

    public static async Task<List<GoogleTaskList>> GetListsAsync()
    {
        using var doc = JsonDocument.Parse(
            await SendAsync(HttpMethod.Get, $"{ApiBase}/users/@me/lists?maxResults=100"));
        var lists = new List<GoogleTaskList>();
        if (!doc.RootElement.TryGetProperty("items", out var items)) return lists;
        foreach (var l in items.EnumerateArray())
            lists.Add(new GoogleTaskList(
                l.GetProperty("id").GetString()!,
                l.TryGetProperty("title", out var t) ? t.GetString() ?? "(unnamed)" : "(unnamed)"));
        return lists;
    }

    // All of a list's tasks, completed ones included (the caller filters).
    public static async Task<List<GoogleTask>> GetTasksAsync(string listId)
    {
        var tasks = new List<GoogleTask>();
        string? pageToken = null;
        do
        {
            var url = $"{ApiBase}/lists/{Uri.EscapeDataString(listId)}/tasks" +
                      "?maxResults=100&showCompleted=true&showHidden=true" +
                      (pageToken != null ? $"&pageToken={Uri.EscapeDataString(pageToken)}" : "");
            using var doc = JsonDocument.Parse(await SendAsync(HttpMethod.Get, url));
            if (doc.RootElement.TryGetProperty("items", out var items))
                foreach (var t in items.EnumerateArray())
                    tasks.Add(new GoogleTask(
                        t.GetProperty("id").GetString()!,
                        t.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "",
                        t.TryGetProperty("notes", out var no) && no.ValueKind == JsonValueKind.String ? no.GetString()! : "",
                        // due is a pure calendar date stamped as UTC midnight
                        // ("...T00:00:00.000Z") — read just the date part, or
                        // local-time conversion shifts it back a day.
                        t.TryGetProperty("due", out var du) && du.ValueKind == JsonValueKind.String
                            ? DateTime.ParseExact(du.GetString()![..10], "yyyy-MM-dd", null) : null,
                        t.TryGetProperty("status", out var st) && st.GetString() == "completed",
                        t.TryGetProperty("parent", out var pa) && pa.ValueKind == JsonValueKind.String ? pa.GetString() : null,
                        t.TryGetProperty("position", out var po) && po.ValueKind == JsonValueKind.String ? po.GetString()! : ""));
            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var np) ? np.GetString() : null;
        } while (pageToken != null);
        return tasks;
    }

    public static Task AddTaskAsync(string listId, string title, DateTime? due) =>
        SendAsync(HttpMethod.Post, $"{ApiBase}/lists/{Uri.EscapeDataString(listId)}/tasks",
            JsonSerializer.Serialize(due == null
                ? new { title }
                : (object)new { title, due = DueJson(due.Value) }));

    public static Task SetCompletedAsync(string listId, string taskId, bool completed) =>
        SendAsync(HttpMethod.Patch, TaskUrl(listId, taskId),
            // Reopening also needs completed cleared, or the API rejects it.
            completed ? "{\"status\":\"completed\"}" : "{\"status\":\"needsAction\",\"completed\":null}");

    public static Task RenameTaskAsync(string listId, string taskId, string title) =>
        SendAsync(HttpMethod.Patch, TaskUrl(listId, taskId), JsonSerializer.Serialize(new { title }));

    public static Task SetDueAsync(string listId, string taskId, DateTime? due) =>
        SendAsync(HttpMethod.Patch, TaskUrl(listId, taskId),
            due == null ? "{\"due\":null}" : JsonSerializer.Serialize(new { due = DueJson(due.Value) }));

    public static Task DeleteTaskAsync(string listId, string taskId) =>
        SendAsync(HttpMethod.Delete, TaskUrl(listId, taskId));

    static string TaskUrl(string listId, string taskId) =>
        $"{ApiBase}/lists/{Uri.EscapeDataString(listId)}/tasks/{Uri.EscapeDataString(taskId)}";

    // The API stores due dates as date-only RFC3339 — any time of day is
    // discarded on write, and times set in Google's own apps are never
    // exposed on read, so this app doesn't do due times at all.
    static string DueJson(DateTime due) => due.ToString("yyyy-MM-dd") + "T00:00:00.000Z";

    static async Task<string> SendAsync(HttpMethod method, string url, string? jsonBody = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + await GetTokenAsync());
        if (jsonBody != null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        var response = await Client.SendAsync(request);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Google rejected the token — open the Tasks section to sign in again.");
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException(
                "Google refused — check that the Google Tasks API is enabled on the Cloud project.");
        if ((int)response.StatusCode == 429)
            throw new InvalidOperationException("Google rate-limited the request — wait a moment and try again.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
