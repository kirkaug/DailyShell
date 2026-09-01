using System.Text;

// Resolves where the app keeps its generated files. User-provided config lives
// in config.txt next to the exe (see Config.cs); everything the app itself
// writes (caches, progress, logs, the word list) lives in a data/ subfolder so
// the app directory stays tidy. Existing generated files are migrated into
// data/ on first run.
static class Paths
{
    static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "data");

    // Generated files that used to sit next to the exe and are moved into data/.
    static readonly string[] LegacyGenerated =
    [
        "weather.txt", "weather-cache.txt",
        "spellingbee-cache.json", "spellingbee-progress.json",
        "wordle-cache.json", "wordle-progress.json", "wordle-words.txt",
        "connections-cache.json", "connections-progress.json",
        "strands-cache.json", "strands-progress.json",
        "crossword-progress.json", "crossword-mini-cache.json",
        "crossword-midi-cache.json", "crossword-daily-cache.json",
        "gmessages-debug.txt", "gmessages-debug.png", "nyt-sync.log",
    ];

    static Paths()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            foreach (var name in LegacyGenerated) TryMigrate(name);
            foreach (var ics in Directory.GetFiles(AppContext.BaseDirectory, "calendar-cache-*.ics"))
                TryMigrate(Path.GetFileName(ics));
        }
        catch (Exception ex) { AppLog.Debug("Paths init", ex); }
    }

    // Full path for a generated data file.
    public static string Data(string name) => Path.Combine(DataDir, name);

    // Search order for user-provided config: exe dir, current dir, then data/.
    public static IEnumerable<string> ConfigDirs()
    {
        yield return AppContext.BaseDirectory;
        yield return Directory.GetCurrentDirectory();
        yield return DataDir;
    }

    static void TryMigrate(string name)
    {
        try
        {
            var from = Path.Combine(AppContext.BaseDirectory, name);
            var to = Path.Combine(DataDir, name);
            if (File.Exists(from) && !File.Exists(to)) File.Move(from, to);
        }
        catch (Exception ex) { AppLog.Debug($"migrate {name}", ex); }
    }
}

// Shared state for the persistent header (clock + current conditions) that
// follows the user through every menu and view, not just the main menu.
// Program.cs's PersistentHeaderLines composes the lines; this just holds the
// last weather reading so sub-screens never wait on the network to draw.
static class HeaderBar
{
    public static string? WeatherLine;      // last fetched conditions markup
    public static DateTime FetchedAt;       // when WeatherLine was last refreshed
    public static Task? Refresh;            // in-flight background refresh, if any
}

// Opt-in debug log. Off by default; enable by setting the DAILYSHELL_DEBUG=1
// environment variable or creating a data/debug.on file. Keeps diagnostics
// available without cluttering normal runs.
static class AppLog
{
    static readonly bool Enabled =
        Environment.GetEnvironmentVariable("DAILYSHELL_DEBUG") == "1" ||
        File.Exists(Path.Combine(AppContext.BaseDirectory, "data", "debug.on"));

    public static void Debug(string msg)
    {
        if (!Enabled) return;
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "data", "debug.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }

    public static void Debug(string context, Exception ex) =>
        Debug($"{context}: {ex.GetType().Name}: {ex.Message}");
}
