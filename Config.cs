using System.Text;

// Single-file user configuration. Everything that used to live in separate
// files (gmail-imap.txt, newsletters.txt, calendar.txt, reddit-oauth.txt,
// nyt-cookies.txt) now lives in one config.txt next to the exe, grouped into
// [sections] that keep the old files' line formats. Legacy files found on
// startup are merged in once and renamed to .bak. Editable by hand or from
// the app's Settings menu.
static class Config
{
    // ReloadNote marks settings that are cached in static fields at startup
    // (Net.cs), so in-app edits only apply on the next launch. Defaults, when
    // given, seed the section's content the first time it is created.
    public record Section(string Name, string Title, string[] Help, bool ReloadNote, string[]? Defaults = null);

    public static readonly Section[] Sections =
    [
        new("gmail-imap", "Gmail accounts",
        [
            "Two lines per account: your Gmail address, then an app password",
            "(create one at myaccount.google.com/apppasswords).",
            "Add more accounts as extra pairs, or one per line as: address | app password",
            "The first account is the primary one, used for newsletter lookups.",
        ], false),
        new("newsletters", "Email newsletters",
        [
            "One newsletter per line:",
            "Label | text the From address must contain | text the Subject must contain (optional)",
            "If empty, built-in defaults are used.",
        ], false),
        new("news-sources", "News sources",
        [
            "Preloaded sources shown in the News menu, one per line:",
            "Name | RSS feed URL",
            "If empty, the built-in defaults are used.",
        ], false,
        [
            "NYT Daily Top Stories | https://rss.nytimes.com/services/xml/rss/nyt/HomePage.xml",
            "NYT U.S. News | https://rss.nytimes.com/services/xml/rss/nyt/US.xml",
            "BBC | http://feeds.bbci.co.uk/news/rss.xml",
            "NPR | https://feeds.npr.org/1001/rss.xml",
            "AP | https://feedx.net/rss/ap.xml",
            "Webster-Kirkwood Times | https://www.timesnewspapers.com/search/?f=rss&t=article&c=webster-kirkwoodtimes&l=50&s=start_time&sd=desc",
        ]),
        new("calendar", "Calendar feeds",
        [
            "One calendar per line: an iCal URL, or \"Label | URL\".",
            "For Google Calendar, use the 'Secret address in iCal format' from the calendar's settings.",
        ], false),
        new("reddit-oauth", "Reddit API",
        [
            "Line 1: client id. Line 2: secret.",
            "Create a free app of type 'script' at reddit.com/prefs/apps.",
        ], true),
        new("nyt-cookies", "NYT cookies",
        [
            "Your nytimes.com Cookie header, for full article text as a subscriber:",
            "either the whole header on one line, or one name=value per line.",
        ], true),
        new("discord", "Discord",
        [
            "Line 1: your Discord user token, for the Discord section.",
            "To find it: log into discord.com in a browser, press F12 > Network tab,",
            "refresh, click a request to discord.com/api (e.g. 'science' or 'messages'),",
            "and copy the value of the 'Authorization' request header.",
            "Caution: Discord's terms forbid automating a user account. This app only",
            "reads messages, marks channels seen, and posts what you type here,",
            "but the risk is yours.",
        ], false),
        new("gemini", "Gemini",
        [
            "The Gemini section drives gemini.google.com in an embedded browser with",
            "your own Google account (first use opens a sign-in window), so it shows",
            "your real Gemini history and uses your AI Pro plan — nothing needed here.",
            "To also enable the 'Local API-key chats' mode (official API, higher",
            "reliability, no web history):",
            "Line 1: a Gemini API key (free at aistudio.google.com/app/apikey).",
            "Optional: model = gemini-2.5-flash   (the default) to use a different model.",
            "API chats are saved locally in data/gemini/.",
        ], false),
        new("display", "Main menu display",
        [
            "key = value settings for the main menu and agenda:",
            "  clock = on|off         show the current time and date in the menu header",
            "  weather = on|off       show current conditions in the menu header",
            "  agenda = on|off        show upcoming events in the menu header",
            "  agenda-items = 3       how many upcoming events the header shows",
            "  agenda-days = 14       how many days the Calendar agenda view covers",
            "  agenda-hide-times =    hide events STARTING at these times, comma-separated:",
            "                         exact times or ranges, e.g. 8:00 AM, 12 PM - 1 PM",
            "                         (all-day events are never hidden; overnight ranges wrap)",
            "  agenda-hide-events =   hide events by name, comma-separated; an event is hidden",
            "                         when its title contains any entry (case-insensitive),",
            "                         e.g. standup, focus time",
            "  refresh-seconds = 60   how long the message views (texts, Discord, email)",
            "                         sit idle before auto-refreshing; 0 or off disables it",
            "Missing keys fall back to the defaults shown above.",
        ], false,
        ["clock = on", "weather = on", "agenda = on", "agenda-items = 3", "agenda-days = 14", "refresh-seconds = 60"]),
    ];

    public static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "config.txt");

    static Config()
    {
        try { MigrateLegacyFiles(); }
        catch (Exception ex) { AppLog.Debug("config migrate", ex); }
    }

    // Content lines of a section: trimmed, with blanks and #-comments skipped —
    // the same filtering every legacy loader applied to its own file.
    public static string[] Lines(string section)
    {
        try
        {
            var body = Load().Find(section);
            return body == null
                ? []
                : body.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();
        }
        catch (Exception ex)
        {
            AppLog.Debug($"config read [{section}]", ex);
            return [];
        }
    }

    // Replaces a section's content. The section's body is regenerated as its
    // help comments plus the given lines; all other sections are preserved
    // verbatim (including any hand-written comments).
    public static void SetLines(string section, IEnumerable<string> lines)
    {
        var file = Load();
        var def = Sections.FirstOrDefault(s => s.Name.Equals(section, StringComparison.OrdinalIgnoreCase))
                  ?? new Section(section, section, [], false);
        var body = def.Help.Select(h => "# " + h).Concat(lines.Select(l => l.TrimEnd())).ToList();
        file.Set(section, body);
        file.Save();
    }

    // One-time consolidation: build config.txt from whatever legacy files exist
    // (first found wins, same search order the old loaders used), then rename
    // the merged files to .bak so there is a single source of truth. Also adds
    // any known section missing from an existing config.txt.
    static void MigrateLegacyFiles()
    {
        var file = Load();
        var changed = !File.Exists(FilePath);

        foreach (var def in Sections)
        {
            if (file.Find(def.Name) != null) continue;

            List<string>? body = null;
            foreach (var dir in Paths.ConfigDirs())
            {
                var legacy = Path.Combine(dir, def.Name + ".txt");
                if (!File.Exists(legacy)) continue;
                body = File.ReadAllLines(legacy).ToList();
                try
                {
                    if (!File.Exists(legacy + ".bak")) File.Move(legacy, legacy + ".bak");
                }
                catch (Exception ex) { AppLog.Debug($"config rename {legacy}", ex); }
                break;
            }

            file.Set(def.Name, body ?? def.Help.Select(h => "# " + h).Concat(def.Defaults ?? []).ToList());
            changed = true;
        }

        if (changed) file.Save();
    }

    // The parsed file: a preamble (comments before the first section) plus the
    // sections in file order, each with its raw body lines kept verbatim.
    sealed class ConfigFile
    {
        public List<string> Preamble = [];
        public List<(string Name, List<string> Body)> Sections = [];

        public List<string>? Find(string name) =>
            Sections.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Body;

        public void Set(string name, List<string> body)
        {
            var idx = Sections.FindIndex(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) Sections[idx] = (Sections[idx].Name, body);
            else Sections.Add((name, body));
        }

        public void Save()
        {
            var sb = new StringBuilder();
            foreach (var l in Preamble) sb.AppendLine(l);
            foreach (var (name, body) in Sections)
            {
                sb.AppendLine($"[{name}]");
                foreach (var l in body) sb.AppendLine(l);
                if (body.Count == 0 || body[^1].Trim().Length > 0) sb.AppendLine();
            }
            File.WriteAllText(FilePath, sb.ToString());
        }
    }

    static ConfigFile Load()
    {
        var file = new ConfigFile();
        if (!File.Exists(FilePath))
        {
            file.Preamble =
            [
                "# DailyShell configuration — all settings in one file.",
                "# Lines starting with # are comments; blank lines are ignored.",
                "# Edit here, or from the app's Settings menu.",
                "",
            ];
            return file;
        }

        List<string>? current = null;
        foreach (var raw in File.ReadAllLines(FilePath))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
            {
                current = [];
                file.Sections.Add((line[1..^1].Trim(), current));
            }
            else if (current != null) current.Add(raw);
            else file.Preamble.Add(raw);
        }

        // Drop each body's trailing blank lines (Save re-adds one as a separator).
        foreach (var (_, body) in file.Sections)
            while (body.Count > 0 && body[^1].Trim().Length == 0) body.RemoveAt(body.Count - 1);
        return file;
    }
}
