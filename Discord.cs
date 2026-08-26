using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// Discord access using the user's own account token ([discord] in config.txt).
// One short gateway connection per visit grabs the same READY snapshot the real
// client boots from (servers, channels, per-channel read state); messages and
// read-acks then go over the plain REST API. Note this is user-account
// automation, which Discord's terms disallow — usage here is read-mostly and
// low-volume, but the account owner accepts the risk (see the Settings help).
static class DiscordApi
{
    const string ApiBase = "https://discord.com/api/v9";
    static readonly HttpClient Client = CreateClient();

    // Channel id -> name across all servers, so <#id> mentions in message text
    // can be shown by name. Filled by the last FetchStateAsync.
    static readonly Dictionary<string, string> ChannelNames = [];

    public static string? Token
    {
        get
        {
            var line = Config.Lines("discord").FirstOrDefault();
            return string.IsNullOrWhiteSpace(line) ? null : line.Trim().Trim('"');
        }
    }

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        return client;
    }

    // Connects to the gateway just long enough to receive READY, then closes.
    // No heartbeat is needed: READY arrives well inside the first heartbeat
    // interval, and nothing stays subscribed afterwards.
    public static async Task<DiscordState> FetchStateAsync()
    {
        var token = Token ?? throw new InvalidOperationException("No Discord token configured.");

        using var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await ws.ConnectAsync(new Uri("wss://gateway.discord.gg/?v=9&encoding=json"), cts.Token);

        using (var hello = await ReceiveJsonAsync(ws, cts.Token))
            if (hello.RootElement.GetProperty("op").GetInt32() != 10)
                throw new InvalidOperationException("Discord gateway didn't send the expected hello.");

        var identify = JsonSerializer.Serialize(new
        {
            op = 2,
            d = new
            {
                token,
                properties = new { os = "Windows", browser = "Chrome", device = "" },
            }
        });
        await ws.SendAsync(Encoding.UTF8.GetBytes(identify), WebSocketMessageType.Text, true, cts.Token);

        while (true)
        {
            using var doc = await ReceiveJsonAsync(ws, cts.Token);
            var root = doc.RootElement;
            var op = root.GetProperty("op").GetInt32();
            if (op == 9)
                throw new InvalidOperationException(
                    "Discord rejected the session — the token in Settings > Discord is likely invalid or expired.");
            if (op != 0 || root.GetProperty("t").GetString() != "READY") continue;

            var d = root.GetProperty("d");
            try
            {
                var state = ParseReady(d);
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
                catch { /* snapshot already in hand */ }
                return state;
            }
            catch (Exception ex)
            {
                // The READY layout varies by account/gateway version; keep the raw
                // payload so the parser can be adapted to what Discord actually sent.
                try { File.WriteAllText(Paths.Data("discord-ready-debug.json"), d.GetRawText()); }
                catch { /* diagnostics only */ }
                AppLog.Debug("discord READY parse", ex);
                throw new InvalidOperationException(
                    "Couldn't read Discord's server list (layout may have changed). " +
                    "Saved the raw payload to data/discord-ready-debug.json.", ex);
            }
        }
    }

    static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket ws, CancellationToken ct)
    {
        // READY for an account in many servers can run to several megabytes.
        var buffer = new byte[65536];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                var code = (int?)ws.CloseStatus ?? 0;
                throw new InvalidOperationException(code == 4004
                    ? "Discord rejected the token — update it in Settings > Discord."
                    : $"Discord closed the connection ({code} {ws.CloseStatusDescription}).");
            }
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return JsonDocument.Parse(stream.ToArray());
        }
    }

    static DiscordState ParseReady(JsonElement d)
    {
        var state = new DiscordState();
        ChannelNames.Clear();

        // Read states: v9 without capabilities is a bare array; newer gateway
        // variants wrap the same entries in {entries:[...]}.
        if (d.TryGetProperty("read_state", out var rs))
            foreach (var e in Entries(rs))
            {
                if (!e.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                state.ReadStates[id.GetString()!] = (
                    Snowflake(e, "last_message_id"),
                    e.TryGetProperty("mention_count", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 0);
            }

        // Per-server notification settings, for muted servers/channels.
        var mutedGuilds = new HashSet<string>();
        var mutedChannels = new HashSet<string>();
        if (d.TryGetProperty("user_guild_settings", out var ugs))
            foreach (var g in Entries(ugs))
            {
                if (IsTrue(g, "muted") &&
                    g.TryGetProperty("guild_id", out var gi) && gi.ValueKind == JsonValueKind.String)
                    mutedGuilds.Add(gi.GetString()!);
                if (g.TryGetProperty("channel_overrides", out var cos) && cos.ValueKind == JsonValueKind.Array)
                    foreach (var co in cos.EnumerateArray())
                        if (IsTrue(co, "muted") &&
                            co.TryGetProperty("channel_id", out var ci) && ci.ValueKind == JsonValueKind.String)
                            mutedChannels.Add(ci.GetString()!);
            }

        // Direct messages as a synthetic first "server", newest activity on top.
        if (d.TryGetProperty("private_channels", out var dms) && dms.ValueKind == JsonValueKind.Array)
        {
            var channels = new List<DiscordChannel>();
            foreach (var c in dms.EnumerateArray())
            {
                var type = c.GetProperty("type").GetInt32();
                if (type != 1 && type != 3) continue; // DMs and group DMs
                var name = c.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
                if (name.Length == 0 && c.TryGetProperty("recipients", out var recips) && recips.ValueKind == JsonValueKind.Array)
                    name = string.Join(", ", recips.EnumerateArray().Select(DisplayName));
                if (name.Length == 0) name = "(unknown)";
                var id = c.GetProperty("id").GetString()!;
                ChannelNames[id] = name;
                channels.Add(new DiscordChannel(id, name, type == 3 ? "group" : "",
                    Snowflake(c, "last_message_id"), Muted: mutedChannels.Contains(id)));
            }
            state.Guilds.Add(new DiscordGuild("", "Direct messages",
                channels.OrderByDescending(c => c.LastMessageId).ToList()));
        }

        if (d.TryGetProperty("guilds", out var guilds) && guilds.ValueKind == JsonValueKind.Array)
        {
            var list = new List<DiscordGuild>();
            foreach (var g in guilds.EnumerateArray())
            {
                if (IsTrue(g, "unavailable")) continue;
                // Some gateway variants nest the display fields under "properties".
                var props = g.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : g;
                var guildId = g.GetProperty("id").GetString()!;
                var guildName = props.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()! : guildId;
                if (!g.TryGetProperty("channels", out var chArr) || chArr.ValueKind != JsonValueKind.Array) continue;

                // Category id -> (name, position), to group and order channels the
                // way the Discord sidebar does.
                var categories = new Dictionary<string, (string Name, int Pos)>();
                foreach (var c in chArr.EnumerateArray())
                    if (c.GetProperty("type").GetInt32() == 4)
                        categories[c.GetProperty("id").GetString()!] = (
                            c.TryGetProperty("name", out var cn) && cn.ValueKind == JsonValueKind.String ? cn.GetString()! : "",
                            Position(c));

                var chans = new List<(int CatPos, int Pos, DiscordChannel Ch)>();
                foreach (var c in chArr.EnumerateArray())
                {
                    var type = c.GetProperty("type").GetInt32();
                    if (type != 0 && type != 5) continue; // text and announcement channels
                    var chId = c.GetProperty("id").GetString()!;
                    if (!LikelyVisible(c, guildId) && !state.ReadStates.ContainsKey(chId)) continue;
                    var parent = c.TryGetProperty("parent_id", out var pid) && pid.ValueKind == JsonValueKind.String
                        ? pid.GetString()! : "";
                    var (cat, catPos) = categories.TryGetValue(parent, out var ci) ? ci : ("", -1);
                    var chName = c.TryGetProperty("name", out var chn) && chn.ValueKind == JsonValueKind.String
                        ? chn.GetString()! : chId;
                    ChannelNames[chId] = chName;
                    chans.Add((catPos, Position(c), new DiscordChannel(chId, chName, cat,
                        Snowflake(c, "last_message_id"),
                        mutedGuilds.Contains(guildId) || mutedChannels.Contains(chId))));
                }
                list.Add(new DiscordGuild(guildId, guildName,
                    chans.OrderBy(x => x.CatPos).ThenBy(x => x.Pos).Select(x => x.Ch).ToList()));
            }
            state.Guilds.AddRange(list.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase));
        }

        return state;
    }

    // Full permission math needs role data the READY snapshot doesn't reliably
    // carry, so use the common case: a channel is hidden when @everyone is
    // denied VIEW_CHANNEL. Role-gated channels the user can actually read still
    // show up because the client keeps a read state for them (caller checks).
    static bool LikelyVisible(JsonElement channel, string guildId)
    {
        const ulong ViewChannel = 1 << 10;
        if (!channel.TryGetProperty("permission_overwrites", out var ows) || ows.ValueKind != JsonValueKind.Array)
            return true;
        foreach (var ow in ows.EnumerateArray())
            if (ow.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                id.GetString() == guildId &&
                ow.TryGetProperty("deny", out var deny) &&
                ulong.TryParse(deny.ValueKind == JsonValueKind.String ? deny.GetString() : deny.GetRawText(), out var bits))
                return (bits & ViewChannel) == 0;
        return true;
    }

    // Newest-first from the API; returned oldest-first for display.
    // before = 0 fetches the latest messages.
    public static async Task<List<DiscordMessage>> GetMessagesAsync(string channelId, ulong before = 0, int limit = 100)
    {
        var url = $"{ApiBase}/channels/{channelId}/messages?limit={limit}" + (before > 0 ? $"&before={before}" : "");
        using var doc = JsonDocument.Parse(await SendAsync(HttpMethod.Get, url));
        var messages = doc.RootElement.EnumerateArray().Select(ParseMessage).ToList();
        messages.Reverse();
        return messages;
    }

    // The same read-receipt the official client sends when you view a channel.
    public static Task AckAsync(string channelId, ulong messageId) =>
        SendAsync(HttpMethod.Post, $"{ApiBase}/channels/{channelId}/messages/{messageId}/ack",
            """{"token":null}""");

    static async Task<string> SendAsync(HttpMethod method, string url, string? jsonBody = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Authorization", Token);
        if (jsonBody != null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        var response = await Client.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Discord rejected the token — update it in Settings > Discord.");
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Discord says this account can't access that channel.");
        if ((int)response.StatusCode == 429)
            throw new InvalidOperationException("Discord rate-limited the request — wait a moment and try again.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    static DiscordMessage ParseMessage(JsonElement m)
    {
        var author = m.TryGetProperty("author", out var a) ? DisplayName(a) : "unknown";
        var timestamp = m.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(t.GetString()!).ToLocalTime()
            : DateTimeOffset.MinValue;

        var parts = new List<string>();
        var links = new List<(string Label, string Url)>();

        if (m.TryGetProperty("referenced_message", out var r) && r.ValueKind == JsonValueKind.Object)
        {
            var replyAuthor = r.TryGetProperty("author", out var ra) ? DisplayName(ra) : "unknown";
            var excerpt = CleanContent(r.TryGetProperty("content", out var rc) ? rc.GetString() ?? "" : "", r);
            if (excerpt.Length > 80) excerpt = excerpt[..80] + "…";
            parts.Add($"↩ {replyAuthor}: {excerpt}");
        }

        var text = CleanContent(m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "", m);
        if (text.Length > 0) parts.Add(text);

        if (m.TryGetProperty("attachments", out var atts) && atts.ValueKind == JsonValueKind.Array)
            foreach (var att in atts.EnumerateArray())
            {
                var filename = att.TryGetProperty("filename", out var f) && f.ValueKind == JsonValueKind.String
                    ? f.GetString()! : "attachment";
                parts.Add($"[attachment: {filename}]");
                if (att.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                    links.Add((filename, u.GetString()!));
            }

        if (m.TryGetProperty("sticker_items", out var stickers) && stickers.ValueKind == JsonValueKind.Array)
            foreach (var s in stickers.EnumerateArray())
                parts.Add($"[sticker: {(s.TryGetProperty("name", out var sn) ? sn.GetString() : "?")}]");

        if (m.TryGetProperty("embeds", out var embeds) && embeds.ValueKind == JsonValueKind.Array)
            foreach (var e in embeds.EnumerateArray())
            {
                var title = e.TryGetProperty("title", out var et) && et.ValueKind == JsonValueKind.String ? et.GetString()! : "";
                var desc = e.TryGetProperty("description", out var ed) && ed.ValueKind == JsonValueKind.String ? ed.GetString()! : "";
                if (desc.Length > 300) desc = desc[..300] + "…";
                var embedText = string.Join(" — ", new[] { title, desc }.Where(s => s.Length > 0));
                if (embedText.Length > 0) parts.Add($"▐ {embedText}");
                if (title.Length > 0 && e.TryGetProperty("url", out var eu) && eu.ValueKind == JsonValueKind.String)
                    links.Add((title, eu.GetString()!));
            }

        if (parts.Count == 0) parts.Add(SystemMessageText(m));
        return new DiscordMessage(Snowflake(m, "id"), author, timestamp, string.Join("\n", parts), links);
    }

    // Turns Discord's raw mention/emoji codes into readable text:
    // <@123>/<@!123> -> @name (from the message's mentions list), <#123> -> #name,
    // <@&123> -> @role, <a:wave:123> -> :wave:.
    static string CleanContent(string text, JsonElement message)
    {
        if (text.Length == 0 || !text.Contains('<')) return text;

        var names = new Dictionary<string, string>();
        if (message.TryGetProperty("mentions", out var mentions) && mentions.ValueKind == JsonValueKind.Array)
            foreach (var u in mentions.EnumerateArray())
                if (u.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    names[id.GetString()!] = DisplayName(u);

        text = Regex.Replace(text, @"<@!?(\d+)>",
            m => "@" + (names.TryGetValue(m.Groups[1].Value, out var n) ? n : "someone"));
        text = Regex.Replace(text, @"<@&\d+>", "@role");
        text = Regex.Replace(text, @"<#(\d+)>",
            m => "#" + (ChannelNames.TryGetValue(m.Groups[1].Value, out var n) ? n : "channel"));
        text = Regex.Replace(text, @"<a?:(\w+):\d+>", ":$1:");
        return text;
    }

    static string SystemMessageText(JsonElement m) =>
        (m.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0) switch
        {
            4 => "[changed the channel name]",
            6 => "[pinned a message]",
            7 => "[joined the server]",
            8 or 9 or 10 or 11 => "[boosted the server]",
            _ => "(no text)",
        };

    static string DisplayName(JsonElement user) =>
        user.TryGetProperty("global_name", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString()! :
        user.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString()! : "unknown";

    // A list that is either a bare array (legacy READY) or {entries:[...]}.
    static IEnumerable<JsonElement> Entries(JsonElement listOrWrapper)
    {
        var array = listOrWrapper;
        if (array.ValueKind == JsonValueKind.Object &&
            array.TryGetProperty("entries", out var e))
            array = e;
        if (array.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in array.EnumerateArray()) yield return item;
    }

    static bool IsTrue(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    static int Position(JsonElement channel) =>
        channel.TryGetProperty("position", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;

    static ulong Snowflake(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String &&
        ulong.TryParse(v.GetString(), out var id) ? id : 0;
}
