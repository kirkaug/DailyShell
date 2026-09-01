using Microsoft.Playwright;

// One pending notification, shown in the header count and the N center.
// Key is stable for one underlying event (it includes the newest message id
// or activity stamp), so dismissing hides it until genuinely new activity
// arrives. TargetId/TargetId2 carry what the deep link needs: email account +
// uid, texts conversation name, Webex room id, or Discord guild + channel ids.
record AppNotification(string Source, string Key, DateTimeOffset When, string Title, string Detail,
    string TargetId = "", string TargetId2 = "");

// Shared state between the background notification poller (Program.cs's
// NotificationPollLoopAsync) and the UI. All mutation goes through the lock;
// the poller never touches the console, and the UI never blocks on the network.
static class Notify
{
    static readonly object Sync = new();
    static readonly List<AppNotification> Items = [];
    static readonly HashSet<string> Dismissed = [];         // session-only
    static readonly Dictionary<string, string> Errors = []; // source -> last poll error

    public static DateTimeOffset LastPollAt;   // MinValue until the first poll lands
    public static bool CenterOpen;             // blocks nested N presses

    // Latest source snapshots, kept so the center can deep-link without
    // refetching (a jump uses the same data the notification came from).
    public static DiscordState? DiscordState;
    public static List<WebexRoom> WebexRooms = [];

    // Replaces one source's notifications with a fresh poll result. Items whose
    // key already existed keep their original When (texts have no reliable
    // timestamp, so their When is "first noticed").
    public static void Update(string source, List<AppNotification> fresh)
    {
        lock (Sync)
        {
            var previous = Items.Where(n => n.Source == source).ToDictionary(n => n.Key, n => n.When);
            Items.RemoveAll(n => n.Source == source);
            Items.AddRange(fresh.Select(n =>
                previous.TryGetValue(n.Key, out var was) ? n with { When = was } : n));
        }
    }

    public static void Dismiss(string key)
    {
        lock (Sync) Dismissed.Add(key);
    }

    public static void Drop(Func<AppNotification, bool> stale)
    {
        lock (Sync) Items.RemoveAll(n => stale(n));
    }

    public static List<AppNotification> Snapshot()
    {
        lock (Sync)
            return Items.Where(n => !Dismissed.Contains(n.Key))
                        .OrderByDescending(n => n.When).ToList();
    }

    public static int Count
    {
        get { lock (Sync) return Items.Count(n => !Dismissed.Contains(n.Key)); }
    }

    public static void SetError(string source, string message)
    {
        lock (Sync) Errors[source] = message;
    }

    public static void ClearError(string source)
    {
        lock (Sync) Errors.Remove(source);
    }

    public static List<(string Source, string Message)> ErrorSnapshot()
    {
        lock (Sync) return Errors.Select(e => (e.Key, e.Value)).ToList();
    }
}

// Persisted email watermarks (data/notify-seen.json): per account, the highest
// inbox UID that was on screen the last time the user looked at that inbox.
// Only unread messages above the watermark notify, so mail deliberately left
// unread doesn't nag forever. A first-ever poll baselines to the current top
// so a fresh install doesn't flood.
static class NotifySeen
{
    static readonly string FilePath = Paths.Data("notify-seen.json");
    static readonly object Sync = new();
    static Dictionary<string, uint>? _map;

    static Dictionary<string, uint> Load()
    {
        if (_map != null) return _map;
        try
        {
            _map = File.Exists(FilePath)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, uint>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (Exception ex)
        {
            AppLog.Debug("notify seen load", ex);
            _map = [];
        }
        return _map;
    }

    public static uint EmailWatermark(string account)
    {
        lock (Sync) return Load().TryGetValue("email:" + account, out var uid) ? uid : 0;
    }

    // Watermarks only move forward — a truncated fetch can't un-see mail.
    public static void RaiseEmailWatermark(string account, uint uid)
    {
        lock (Sync)
        {
            var map = Load();
            var key = "email:" + account;
            if (map.TryGetValue(key, out var old) && old >= uid) return;
            map[key] = uid;
            try
            {
                File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(map,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AppLog.Debug("notify seen save", ex); }
        }
    }
}

// The one shared Google Messages browser. Both the Texts section and the
// notification poller drive the same persistent profile, and two Chromes can't
// open one profile dir — so the context lives here and stays up for the whole
// app session once launched (closed at app exit, or briefly for pairing).
// Program.cs owns the launch/scrape logic; this holds the instances and the
// coordination flags.
static class TextsBrowser
{
    public static readonly string ProfileDir = Path.Combine(AppContext.BaseDirectory, "gmessages-profile");
    public const string ConversationsUrl = "https://messages.google.com/web/conversations";
    public const string ConvItemSelector = "mws-conversation-list-item";

    // True while the Texts section (or a notification jump into a conversation)
    // is driving the page — the poller keeps its hands off entirely.
    public static volatile bool SectionActive;

    // Serializes poller scrapes against section entry, so a poll in flight
    // finishes before the section starts clicking around.
    public static readonly SemaphoreSlim Gate = new(1, 1);

    // Set when a background poll finds the page unpaired or unreadable; polls
    // stop for the session (visiting the Texts section can repair pairing).
    public static bool PollBroken;

    public static IPlaywright? Driver;
    public static IBrowserContext? Context;
    public static IPage? Page;
}
