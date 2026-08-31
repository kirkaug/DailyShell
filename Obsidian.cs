using System.Text.Json;

// Obsidian vault access. A vault is just a folder of Markdown files, so
// everything here reads straight from disk — no plugin, API, or running
// Obsidian needed. Vaults are found from Obsidian's own registry in %APPDATA%
// (an [obsidian] config section can override). Daily-note location, naming,
// and template come from the vault's .obsidian/daily-notes.json, so this
// matches wherever the Obsidian app itself puts them.
static class ObsidianVault
{
    public record Vault(string Name, string Root);

    // Config lines win (a path, or "Label | path"); otherwise every vault
    // Obsidian knows about. Name is what obsidian:// URIs address, so for
    // detected vaults it must stay the folder name.
    public static List<Vault> GetVaults()
    {
        var vaults = new List<Vault>();
        foreach (var line in Config.Lines("obsidian"))
        {
            var parts = line.Split('|', 2);
            var path = (parts.Length == 2 ? parts[1] : parts[0]).Trim();
            var name = parts.Length == 2 ? parts[0].Trim() : Path.GetFileName(path.TrimEnd('\\', '/'));
            if (Directory.Exists(path)) vaults.Add(new Vault(name, path));
        }
        if (vaults.Count > 0) return vaults;

        try
        {
            var registry = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obsidian", "obsidian.json");
            if (File.Exists(registry))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(registry));
                if (doc.RootElement.TryGetProperty("vaults", out var v) && v.ValueKind == JsonValueKind.Object)
                    foreach (var entry in v.EnumerateObject())
                        if (entry.Value.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String &&
                            Directory.Exists(p.GetString()))
                            vaults.Add(new Vault(Path.GetFileName(p.GetString()!.TrimEnd('\\', '/')), p.GetString()!));
            }
        }
        catch (Exception ex) { AppLog.Debug("obsidian registry", ex); }
        return vaults;
    }

    // Every note in the vault, newest first. Obsidian's own folders (.obsidian,
    // .trash, any dot-folder) are skipped.
    public static List<ObsidianNote> AllNotes(Vault vault)
    {
        var notes = new List<ObsidianNote>();
        Walk(vault.Root);
        notes.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        return notes;

        void Walk(string dir)
        {
            foreach (var sub in Directory.GetDirectories(dir))
                if (!Path.GetFileName(sub).StartsWith('.'))
                    Walk(sub);
            foreach (var file in Directory.GetFiles(dir, "*.md"))
                notes.Add(ToNote(vault, file));
        }
    }

    // One directory level: subfolders and notes, both name-sorted.
    public static (List<string> Folders, List<ObsidianNote> Notes) ListFolder(Vault vault, string relDir)
    {
        var dir = relDir.Length == 0 ? vault.Root : Path.Combine(vault.Root, relDir);
        var folders = Directory.GetDirectories(dir)
            .Select(d => Path.GetFileName(d)!)
            .Where(n => !n.StartsWith('.'))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var notes = Directory.GetFiles(dir, "*.md")
            .Select(f => ToNote(vault, f))
            .OrderBy(n => n.Title, StringComparer.OrdinalIgnoreCase).ToList();
        return (folders, notes);
    }

    static ObsidianNote ToNote(Vault vault, string fullPath) =>
        new(Path.GetRelativePath(vault.Root, fullPath).Replace('\\', '/'),
            Path.GetFileNameWithoutExtension(fullPath),
            File.GetLastWriteTime(fullPath));

    public static string Read(Vault vault, ObsidianNote note) =>
        File.ReadAllText(Path.Combine(vault.Root, note.RelPath.Replace('/', Path.DirectorySeparatorChar)));

    // Case-insensitive search over titles and contents. Title matches come
    // first (no snippet needed); content matches carry the line around the
    // first hit, windowed to menu size.
    public static List<(ObsidianNote Note, string Snippet)> Search(Vault vault, string query)
    {
        var titleHits = new List<(ObsidianNote, string)>();
        var contentHits = new List<(ObsidianNote, string)>();
        foreach (var note in AllNotes(vault))
        {
            if (note.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                titleHits.Add((note, ""));
                continue;
            }
            string text;
            try { text = Read(vault, note); }
            catch (Exception ex) { AppLog.Debug($"obsidian read {note.RelPath}", ex); continue; }
            var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;

            var lineStart = text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
            var lineEnd = text.IndexOf('\n', at);
            var line = text[lineStart..(lineEnd < 0 ? text.Length : lineEnd)].Trim();
            if (line.Length > 90)
            {
                var idx = line.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                var start = Math.Clamp(idx - 30, 0, line.Length - 90);
                line = (start > 0 ? "…" : "") + line.Substring(start, 90) + (start + 90 < line.Length ? "…" : "");
            }
            contentHits.Add((note, line));
        }
        return titleHits.Concat(contentHits).ToList();
    }

    // The vault's own daily-note settings: where they live, how they're named
    // (a Moment.js format string), and an optional template note.
    public static (string Folder, string Format, string Template) DailySettings(Vault vault)
    {
        var folder = ""; var format = "YYYY-MM-DD"; var template = "";
        try
        {
            var file = Path.Combine(vault.Root, ".obsidian", "daily-notes.json");
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                if (root.TryGetProperty("folder", out var f) && f.ValueKind == JsonValueKind.String)
                    folder = f.GetString()!;
                if (root.TryGetProperty("format", out var fmt) && fmt.ValueKind == JsonValueKind.String &&
                    fmt.GetString()!.Length > 0)
                    format = fmt.GetString()!;
                if (root.TryGetProperty("template", out var t) && t.ValueKind == JsonValueKind.String)
                    template = t.GetString()!;
            }
        }
        catch (Exception ex) { AppLog.Debug("obsidian daily settings", ex); }
        return (folder.Trim('/'), format, template);
    }

    // Vault-relative path of a date's daily note, e.g. "Daily/20260831.md".
    public static string DailyNotePath(Vault vault, DateTime date)
    {
        var (folder, format, _) = DailySettings(vault);
        return (folder.Length > 0 ? folder + "/" : "") + date.ToString(MomentToNet(format)) + ".md";
    }

    // The Moment.js date tokens Obsidian uses, mapped to .NET ones. Year and
    // day-of-month differ in case; months (M/MM) and weekday names (ddd/dddd)
    // are already identical. Enough for daily-note filenames.
    static string MomentToNet(string moment) =>
        moment.Replace("YYYY", "yyyy").Replace("YY", "yy").Replace("DD", "dd").Replace("D", "d");

    // Appends a capture line to today's daily note, creating the note first
    // (seeded from the configured template, if any). Appending is safe while
    // Obsidian is running — it picks up external file changes live.
    public static string AppendToDailyToday(Vault vault, string line)
    {
        var relPath = DailyNotePath(vault, DateTime.Now);
        var full = Path.Combine(vault.Root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (!File.Exists(full))
        {
            var (_, _, template) = DailySettings(vault);
            var seed = "";
            if (template.Length > 0)
            {
                var templateFile = Path.Combine(vault.Root,
                    (template.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? template : template + ".md")
                    .Replace('/', Path.DirectorySeparatorChar));
                try { if (File.Exists(templateFile)) seed = File.ReadAllText(templateFile); }
                catch (Exception ex) { AppLog.Debug("obsidian daily template", ex); }
            }
            File.WriteAllText(full, seed.Length > 0 && !seed.EndsWith('\n') ? seed + Environment.NewLine : seed);
        }
        var existing = File.ReadAllText(full);
        File.AppendAllText(full,
            (existing.Length > 0 && !existing.EndsWith('\n') ? Environment.NewLine : "") + line + Environment.NewLine);
        return relPath;
    }

    // obsidian:// link that opens the note in the Obsidian app (launching it if
    // needed). File is the vault-relative path without the .md extension.
    public static string NoteUri(Vault vault, string relPath)
    {
        var file = relPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? relPath[..^3] : relPath;
        return $"obsidian://open?vault={Uri.EscapeDataString(vault.Name)}&file={Uri.EscapeDataString(file)}";
    }
}
