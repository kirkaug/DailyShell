using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Playwright;
using IcsCalendar = Ical.Net.Calendar;
using IcsEvent = Ical.Net.CalendarComponents.CalendarEvent;
using System.ServiceModel.Syndication;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Spectre.Console;
using Spectre.Console.Rendering;
using HtmlAgilityPack;
using System.Text;
using System.Net;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

// Render Unicode (box borders, ▲ ● ★ █ ✓, emoji) consistently regardless of the
// console's default code page. Without this the symbols garble or vanish on hosts
// whose active code page isn't UTF-8. Best-effort: harmless if the host rejects it.
try
{
    Console.OutputEncoding = Encoding.UTF8;
    Console.InputEncoding = Encoding.UTF8;
}
catch { /* redirected/unsupported console — leave the default */ }

// Preloaded sources shown in the selection menu. Display name -> feed URL.
// Note: NYT's "The Morning" email newsletter has no public RSS feed;
// "NYT Daily Top Stories" is the official feed behind the daily headlines.
var preloadedSources = new List<(string Name, string Url)>
{
    ("NYT Daily Top Stories", "https://rss.nytimes.com/services/xml/rss/nyt/HomePage.xml"),
    ("NYT U.S. News", "https://rss.nytimes.com/services/xml/rss/nyt/US.xml"),
    ("BBC", "http://feeds.bbci.co.uk/news/rss.xml"),
    ("NPR", "https://feeds.npr.org/1001/rss.xml"),
    ("AP", "https://feedx.net/rss/ap.xml"),
    ("Webster-Kirkwood Times", "https://www.timesnewspapers.com/search/?f=rss&t=article&c=webster-kirkwoodtimes&l=50&s=start_time&sd=desc"),
};

// Name-based lookup so typing "nyt", "bbc", etc. still works with custom input.
var sourceLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    { "nyt", "https://rss.nytimes.com/services/xml/rss/nyt/HomePage.xml" },
    { "new york times", "https://rss.nytimes.com/services/xml/rss/nyt/HomePage.xml" },
    { "bbc", "http://feeds.bbci.co.uk/news/rss.xml" },
    { "npr", "https://feeds.npr.org/1001/rss.xml" },
    { "webster-kirkwood times", "https://www.timesnewspapers.com/search/?f=rss&t=article&c=webster-kirkwoodtimes&l=50&s=start_time&sd=desc" },
	{ "ap", "https://feedx.net/rss/ap.xml"}
};

// Email newsletters read straight from Gmail (full text). Editable in Settings ([newsletters]).
var emailNewsletters = LoadEmailNewsletters();

const string newsOption = "News & newsletters";
const string weatherOption = "Weather forecast";
const string calendarOption = "Calendar agenda";
const string unreadOption = "Email inbox";
const string smsOption = "Text messages";
const string discordOption = "Discord";
const string gamesOption = "Games";
const string settingsOption = "Settings";
const string exitOption = "Exit";

// Current conditions for the main-menu header, fetched in the background so the
// menu isn't held up by a slow lookup.
var menuHeaderTask = FetchMenuHeaderAsync();
var headerFetchedAt = DateTime.Now;

var lastSourceIdx = 0;
while (true)
{
    AnsiConsole.Clear();

    if (menuHeaderTask.IsCompleted && DateTime.Now - headerFetchedAt > TimeSpan.FromMinutes(30))
    {
        menuHeaderTask = FetchMenuHeaderAsync();
        headerFetchedAt = DateTime.Now;
    }

    string? headerText = null;
    if (await Task.WhenAny(menuHeaderTask, Task.Delay(2500)) == menuHeaderTask)
        headerText = await menuHeaderTask;
    if (headerText != null)
        AnsiConsole.MarkupLine(headerText + "\n");

    var mainOptions = new List<string> { newsOption, weatherOption, calendarOption, unreadOption, smsOption, discordOption, gamesOption, settingsOption, exitOption };

    var mainIdx = PromptMenu("[green]Pick an option:[/]", mainOptions, 15, backAction: "exit", initialSelected: lastSourceIdx);
    if (mainIdx < 0) break;
    lastSourceIdx = mainIdx;
    var choice = mainOptions[mainIdx];

    if (choice == exitOption) break;

    if (choice == newsOption)
    {
        await ShowNewsMenuAsync(emailNewsletters, preloadedSources, sourceLookup);
        continue;
    }

    if (choice == weatherOption)
    {
        await ShowWeatherForecastAsync();
        continue;
    }

    if (choice == calendarOption)
    {
        await ShowCalendarAgendaAsync();
        continue;
    }

    if (choice == unreadOption)
    {
        await ShowUnreadEmailAsync();
        continue;
    }

    if (choice == smsOption)
    {
        await ShowTextMessagesAsync();
        continue;
    }

    if (choice == discordOption)
    {
        await ShowDiscordAsync();
        continue;
    }

    if (choice == gamesOption)
    {
        await ShowGamesMenuAsync();
        continue;
    }

    if (choice == settingsOption)
    {
        ShowSettings();

        // Newsletters or display settings may have been edited; reload so the
        // news menu and the header reflect them right away.
        emailNewsletters = LoadEmailNewsletters();
        menuHeaderTask = FetchMenuHeaderAsync();
        headerFetchedAt = DateTime.Now;
        continue;
    }
}

// Release the embedded browser / Playwright driver cleanly on exit.
await NytBrowser.ShutdownAsync();

// Games submenu — currently Spelling Bee, room for more NYT-style games later.
static async Task ShowGamesMenuAsync()
{
    const string spellingBee = "Spelling Bee";
    const string wordle = "Wordle";
    const string connections = "Connections";
    const string strands = "Strands";
    const string mini = "The Mini (crossword)";
    const string midi = "The Midi (crossword)";
    const string daily = "Daily crossword (full-size)";
    const string back = "<= Back to Main Menu";

    var lastIdx = 0;
    while (true)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold blue]Games[/]");

        var options = new List<string> { wordle, mini, midi, daily, connections, strands, spellingBee, back };
        var idx = PromptMenu("[green]Pick a game:[/]", options, 15, initialSelected: lastIdx);
        if (idx < 0 || options[idx] == back)
        {
            AnsiConsole.Clear();
            return;
        }
        lastIdx = idx;

        if (options[idx] == spellingBee)
            await PlaySpellingBeeAsync();
        else if (options[idx] == wordle)
            await PlayWordleAsync();
        else if (options[idx] == connections)
            await PlayConnectionsAsync();
        else if (options[idx] == strands)
            await PlayStrandsAsync();
        else if (options[idx] == mini)
            await PlayCrosswordAsync("mini", "The Mini");
        else if (options[idx] == midi)
            await PlayCrosswordAsync("midi", "The Midi");
        else if (options[idx] == daily)
            await PlayCrosswordAsync("daily", "Daily Crossword");
    }
}

// The news submenu: email newsletters, preloaded feeds, and custom source entry
// (RSS URLs, known source names, subreddits, or newsletter web links).
static async Task ShowNewsMenuAsync(List<EmailNewsletter> emailNewsletters,
    List<(string Name, string Url)> preloadedSources, Dictionary<string, string> sourceLookup)
{
    const string customOption = "Enter a custom source, subreddit, or RSS URL...";
    var nytConnectOption = NytBrowser.IsConnected
        ? "Reconnect NYT account (for full articles)"
        : "Connect NYT account (for full articles)";
    const string back = "<= Back to Main Menu";

    var lastIdx = 0;
    while (true)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold blue]News & newsletters[/]");

        var options = new List<string>();
        options.AddRange(emailNewsletters.Select(n => n.Label));
        options.AddRange(preloadedSources.Select(s => s.Name));
        options.Add(customOption);
        options.Add(nytConnectOption);
        options.Add(back);

        var idx = PromptMenu("[green]Pick a news source:[/]", options, 15, initialSelected: lastIdx);
        if (idx < 0 || options[idx] == back)
        {
            AnsiConsole.Clear();
            return;
        }
        lastIdx = idx;
        var choice = options[idx];

        if (choice == nytConnectOption)
        {
            await NytBrowser.ConnectAsync();
            nytConnectOption = "Reconnect NYT account (for full articles)";
            continue;
        }

        var newsletter = emailNewsletters.FirstOrDefault(n => n.Label == choice);
        if (newsletter != null)
        {
            await ShowNewsletterFromGmailAsync(newsletter);
            continue;
        }

        string feedUrl;
        if (choice == customOption)
        {
            var input = AnsiConsole.Ask<string>("[green]Enter a news source name, an RSS URL, or a subreddit (e.g. r/stlouis):[/]");

            var subreddit = TryParseSubreddit(input);
            if (subreddit != null)
            {
                await ShowSubredditAsync(subreddit);
                continue;
            }

            feedUrl = ResolveFeedUrl(input, sourceLookup);
        }
        else
        {
            feedUrl = preloadedSources.First(s => s.Name == choice).Url;
        }

        if (string.IsNullOrEmpty(feedUrl))
        {
            AnsiConsole.MarkupLine("[red]Could not resolve source. Try a direct RSS URL or a known source.[/]\n");
            PauseForKey();
            continue;
        }

        try
        {
            var feed = await FetchFeedAsync(feedUrl);
            if (feed == null || !feed.Items.Any())
            {
                AnsiConsole.MarkupLine("[yellow]No articles found for this source.[/]\n");
                PauseForKey();
                continue;
            }

            await DisplayArticlesAsync(feed);
        }
        catch (XmlException)
        {
            // Not an RSS/Atom feed — treat it as a web page (e.g. a newsletter
            // "view in browser" link) and render it directly.
            await ShowWebPageAsync(feedUrl);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error fetching news:[/] {ex.Message}\n");
            PauseForKey();
        }
    }
}

// Maps user text input to a URL
static string ResolveFeedUrl(string input, Dictionary<string, string> lookup)
{
    if (Uri.TryCreate(input, UriKind.Absolute, out var uriResult) && 
        (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps))
    {
        return input;
    }

    if (lookup.TryGetValue(input, out var url))
    {
        return url;
    }

    return string.Empty;
}

// Fetches and parses the RSS/Atom feed
static async Task<SyndicationFeed> FetchFeedAsync(string url)
{
    AnsiConsole.MarkupLine($"[dim]Fetching {url}...[/]");

    await using var stream = await Web.GetStreamAsync(url);

    // Configure the reader to ignore DTDs safely
    var settings = new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Ignore
    };

    using var reader = XmlReader.Create(stream, settings);

    return SyndicationFeed.Load(reader);
}

// Renders a plain web page (e.g. a newsletter "view in browser" link) as an article.
static async Task ShowWebPageAsync(string url)
{
    AnsiConsole.Clear();

    var (title, articleText) = await AnsiConsole.Status()
        .StartAsync("Fetching page...", async _ => await ScrapePageAsync(url));

    var panel = new Panel(new Markup($"[bold]{Markup.Escape(title)}[/]\n" +
                                     $"[link]{url}[/]\n\n" +
                                     $"{articleText}"))
    {
        Border = BoxBorder.Rounded,
        Padding = new Padding(1, 1, 1, 1),
        Expand = true
    };

    ShowInPager(panel, BuildLinks([("This page", url)], articleText));
    AnsiConsole.Clear();
}

// Handles the interactive selection and reading UI
static async Task DisplayArticlesAsync(SyndicationFeed feed)
{
    var articles = feed.Items.Take(15).ToList();

    var lastIdx = 0;
    while (true)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine($"[bold blue]{Markup.Escape(feed.Title.Text)}[/]");

        var options = articles.Select(a => Markup.Escape(a.Title.Text)).ToList();
        options.Add("<= Back to News Menu");

        var idx = PromptMenu("Select an article to read:", options, 10, initialSelected: lastIdx);
        if (idx < 0 || idx == options.Count - 1)
        {
            AnsiConsole.Clear();
            break;
        }

        lastIdx = idx;
        await ReadArticleAsync(articles[idx]);
    }
}

// Asks for a location (remembering the last one in weather.txt), geocodes it, and
// shows the forecast in the pager. Uses the National Weather Service for US
// locations (prose forecasts) and falls back to Open-Meteo elsewhere.
static async Task ShowWeatherForecastAsync()
{
    var prompt = new TextPrompt<string>("[green]Location (e.g. 'st louis' or 'kirkwood, missouri'):[/]");
    var saved = LoadSavedWeatherLocation();
    if (saved != null) prompt.DefaultValue(saved);
    var query = AnsiConsole.Prompt(prompt).Trim();
    if (query.Length == 0) return;

    try
    {
        var matches = await AnsiConsole.Status()
            .StartAsync("Looking up location...", async _ => await GeocodeLocationAsync(query));
        if (matches.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No matching location found. Try a city name like 'st louis, missouri'.[/]\n");
            PauseForKey();
            return;
        }

        var place = matches[0];
        if (matches.Count > 1)
        {
            var placeIdx = PromptMenu("Which location?",
                matches.Select(m => Markup.Escape(m.Display)).ToList(), 10, backAction: "cancel");
            if (placeIdx < 0) return;
            place = matches[placeIdx];
        }

        var view = await AnsiConsole.Status()
            .StartAsync($"Fetching forecast for {Markup.Escape(place.Display)}...",
                async _ => await BuildForecastViewAsync(place));

        SaveWeatherLocation(query, place);
        ShowInPager(view);
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Weather error:[/] {Markup.Escape(ex.Message)}\n");
        PauseForKey();
    }
}

// Resolves a location name (optionally "name, state/country") to coordinates
// using Open-Meteo's free geocoding API.
static async Task<List<WeatherPlace>> GeocodeLocationAsync(string query)
{
    var parts = query.Split(',', 2, StringSplitOptions.TrimEntries);
    var name = parts[0];
    var qualifier = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;

    var results = await GeocodeSearchAsync(name);

    // The geocoder indexes "St Louis" rather than "Saint Louis" (and vice versa
    // for some places), so retry with the other spelling if nothing matched.
    if (results.Count == 0)
    {
        string? alt = null;
        if (Regex.IsMatch(name, @"^saint\b", RegexOptions.IgnoreCase))
            alt = Regex.Replace(name, @"^saint\b", "st", RegexOptions.IgnoreCase);
        else if (Regex.IsMatch(name, @"^st\.?(?=\s)", RegexOptions.IgnoreCase))
            alt = Regex.Replace(name, @"^st\.?(?=\s)", "saint", RegexOptions.IgnoreCase);
        if (alt != null) results = await GeocodeSearchAsync(alt);
    }

    if (qualifier != null)
    {
        var filtered = results
            .Where(r => r.Display.Contains(qualifier, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (filtered.Count > 0) results = filtered;
    }

    return results;
}

static async Task<List<WeatherPlace>> GeocodeSearchAsync(string name)
{
    var url = "https://geocoding-api.open-meteo.com/v1/search?count=10&language=en&format=json&name="
              + Uri.EscapeDataString(name);
    var json = await Web.GetStringAsync(url);
    using var doc = JsonDocument.Parse(json);

    var list = new List<WeatherPlace>();
    if (!doc.RootElement.TryGetProperty("results", out var results)) return list;

    foreach (var r in results.EnumerateArray())
    {
        var placeName = r.GetProperty("name").GetString() ?? "";
        var admin1 = r.TryGetProperty("admin1", out var a) ? a.GetString() : null;
        var country = r.TryGetProperty("country", out var c) ? c.GetString() : null;
        var display = string.Join(", ",
            new[] { placeName, admin1, country }.Where(s => !string.IsNullOrEmpty(s)));
        list.Add(new WeatherPlace(display,
            r.GetProperty("latitude").GetDouble(),
            r.GetProperty("longitude").GetDouble()));
    }
    return list;
}

static async Task<IRenderable> BuildForecastViewAsync(WeatherPlace place)
{
    try
    {
        return await BuildNwsForecastAsync(place); // US only, but the best text forecasts
    }
    catch
    {
        return await BuildOpenMeteoForecastAsync(place);
    }
}

// National Weather Service: 7 days of day/night prose forecast periods.
static async Task<IRenderable> BuildNwsForecastAsync(WeatherPlace place)
{
    var lat = place.Lat.ToString("F4", CultureInfo.InvariantCulture);
    var lon = place.Lon.ToString("F4", CultureInfo.InvariantCulture);

    var points = await Web.GetStringAsync($"https://api.weather.gov/points/{lat},{lon}");
    string forecastUrl;
    using (var pointsDoc = JsonDocument.Parse(points))
    {
        forecastUrl = pointsDoc.RootElement.GetProperty("properties").GetProperty("forecast").GetString()
                      ?? throw new InvalidOperationException("No forecast URL for this location.");
    }

    var json = await Web.GetStringAsync(forecastUrl);
    using var doc = JsonDocument.Parse(json);

    var sb = new StringBuilder();
    sb.Append($"[bold]{Markup.Escape(place.Display)}[/]\n");
    sb.Append("[dim]Source: National Weather Service (weather.gov)[/]\n\n");

    foreach (var period in doc.RootElement.GetProperty("properties").GetProperty("periods").EnumerateArray())
    {
        var name = period.GetProperty("name").GetString() ?? "";
        var temp = period.GetProperty("temperature").GetInt32();
        var unit = period.GetProperty("temperatureUnit").GetString() ?? "F";
        var detail = period.GetProperty("detailedForecast").GetString() ?? "";
        var isDay = period.TryGetProperty("isDaytime", out var d) && d.GetBoolean();
        var color = isDay ? "yellow" : "blue";
        sb.Append($"[bold {color}]{Markup.Escape(name)}[/]  [bold]{temp}°{unit}[/]\n{Markup.Escape(detail)}\n\n");
    }

    return new Panel(new Markup(sb.ToString().TrimEnd()))
    {
        Border = BoxBorder.Rounded,
        Padding = new Padding(1, 1, 1, 1),
        Expand = true
    };
}

// Open-Meteo fallback: current conditions plus a 7-day table. Works worldwide.
static async Task<IRenderable> BuildOpenMeteoForecastAsync(WeatherPlace place)
{
    var lat = place.Lat.ToString("F4", CultureInfo.InvariantCulture);
    var lon = place.Lon.ToString("F4", CultureInfo.InvariantCulture);
    var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
              "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m" +
              "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,wind_speed_10m_max" +
              "&temperature_unit=fahrenheit&wind_speed_unit=mph&timezone=auto&forecast_days=7";

    var json = await Web.GetStringAsync(url);
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;

    var current = root.GetProperty("current");
    var header = new Markup(
        $"[bold]{Markup.Escape(place.Display)}[/]\n" +
        "[dim]Source: Open-Meteo[/]\n\n" +
        $"Now: [bold]{current.GetProperty("temperature_2m").GetDouble():F0}°F[/] " +
        $"(feels like {current.GetProperty("apparent_temperature").GetDouble():F0}°F) — " +
        $"{WeatherCodeText(current.GetProperty("weather_code").GetInt32())}\n" +
        $"Humidity {current.GetProperty("relative_humidity_2m").GetInt32()}% • " +
        $"Wind {current.GetProperty("wind_speed_10m").GetDouble():F0} mph\n");

    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumns("Day", "Forecast", "High", "Low", "Rain", "Wind");

    var daily = root.GetProperty("daily");
    var times = daily.GetProperty("time");
    for (var i = 0; i < times.GetArrayLength(); i++)
    {
        var precip = daily.GetProperty("precipitation_probability_max")[i];
        table.AddRow(
            DateTime.Parse(times[i].GetString()!, CultureInfo.InvariantCulture).ToString("ddd MM/dd"),
            WeatherCodeText(daily.GetProperty("weather_code")[i].GetInt32()),
            $"{daily.GetProperty("temperature_2m_max")[i].GetDouble():F0}°F",
            $"{daily.GetProperty("temperature_2m_min")[i].GetDouble():F0}°F",
            precip.ValueKind == JsonValueKind.Number ? $"{precip.GetInt32()}%" : "-",
            $"{daily.GetProperty("wind_speed_10m_max")[i].GetDouble():F0} mph");
    }

    return new Panel(new Rows(header, table))
    {
        Border = BoxBorder.Rounded,
        Padding = new Padding(1, 1, 1, 1),
        Expand = true
    };
}

// WMO weather interpretation codes used by Open-Meteo.
static string WeatherCodeText(int code) => code switch
{
    0 => "Clear",
    1 => "Mostly clear",
    2 => "Partly cloudy",
    3 => "Overcast",
    45 or 48 => "Fog",
    51 or 53 or 55 => "Drizzle",
    56 or 57 => "Freezing drizzle",
    61 => "Light rain",
    63 => "Rain",
    65 => "Heavy rain",
    66 or 67 => "Freezing rain",
    71 => "Light snow",
    73 => "Snow",
    75 => "Heavy snow",
    77 => "Snow grains",
    80 or 81 => "Rain showers",
    82 => "Heavy rain showers",
    85 or 86 => "Snow showers",
    95 => "Thunderstorms",
    96 or 99 => "Thunderstorms with hail",
    _ => $"Weather code {code}",
};

// Reads one "key = value" line from the [display] config section; the fallback
// applies when the key is absent or malformed.
static string GetDisplaySetting(string key, string fallback)
{
    foreach (var line in Config.Lines("display"))
    {
        var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && parts[0].Equals(key, StringComparison.OrdinalIgnoreCase))
            return parts[1];
    }
    return fallback;
}

// A [display] toggle: anything except "off" counts as on.
static bool DisplayOn(string key) =>
    !GetDisplaySetting(key, "on").Equals("off", StringComparison.OrdinalIgnoreCase);

// A numeric [display] setting, clamped to a sane range.
static int DisplayNumber(string key, int fallback, int min, int max) =>
    int.TryParse(GetDisplaySetting(key, ""), out var n) ? Math.Clamp(n, min, max) : fallback;

// Builds the multi-line main-menu header: current conditions with today's
// high/low, then the next few timed (non-all-day) calendar events. Both parts
// can be toggled off in Settings > Main menu display.
static async Task<string?> FetchMenuHeaderAsync()
{
    var parts = new List<string>();

    // Run both lookups concurrently, and never let slow calendar feeds (Google's
    // 429 throttle responses take ~7s EACH) hold the weather line hostage.
    var weatherTask = DisplayOn("weather") ? FetchCurrentWeatherLineAsync() : Task.FromResult<string?>(null);
    var calendarTask = DisplayOn("agenda") ? FetchUpcomingEventLinesAsync() : Task.FromResult(new List<string>());
    var calendarDeadline = Task.Delay(TimeSpan.FromSeconds(10));

    var weather = await weatherTask;
    if (weather != null) parts.Add(weather);

    if (await Task.WhenAny(calendarTask, calendarDeadline) == calendarTask)
    {
        try
        {
            parts.AddRange(await calendarTask);
        }
        catch
        {
            // The header is optional — never block the menu over it.
        }
    }

    return parts.Count > 0 ? string.Join("\n", parts) : null;
}

// The next few upcoming timed events across all calendar feeds (all-day events
// are skipped), deduplicated across calendars; the count comes from the
// agenda-items display setting. Uses the ICS disk cache, so this rarely costs
// a network request.
static async Task<List<string>> FetchUpcomingEventLinesAsync()
{
    var feeds = LoadCalendarFeeds();
    if (feeds.Count == 0) return [];

    var now = DateTime.Now;
    var events = new List<(DateTime Start, string Title)>();
    var seen = new HashSet<(DateTime, string)>();

    // Fetch all feeds in parallel — a throttled feed answers slowly, and paying
    // that cost once beats paying it per feed.
    var fetched = await Task.WhenAll(feeds.Select(async f =>
    {
        try { return (await FetchCalendarIcsAsync(f.Url)).Ics; }
        catch (Exception ex) { AppLog.Debug("returned null", ex); return null; } // a broken/throttled feed shouldn't take the header down
    }));

    foreach (var ics in fetched)
    {
        if (ics == null) continue;
        try
        {
            var calendar = IcsCalendar.Load(ics);
            foreach (var occurrence in calendar.GetOccurrences(now.Date, now.Date.AddDays(8)))
            {
                if (occurrence.Source is not IcsEvent ev || ev.IsAllDay) continue;
                var start = occurrence.Period.StartTime.AsSystemLocal;
                var end = occurrence.Period.EndTime?.AsSystemLocal ?? start;
                if (end < now) continue; // already over

                var title = ev.Summary ?? "(untitled)";
                if (seen.Add((start, title.Trim().ToLowerInvariant())))
                    events.Add((start, title));
            }
        }
        catch
        {
            // Unparseable feed — skip it.
        }
    }

    return events
        .OrderBy(e => e.Start)
        .Take(DisplayNumber("agenda-items", 3, 1, 10))
        .Select(e =>
        {
            var day = e.Start.Date == DateTime.Today ? "Today"
                : e.Start.Date == DateTime.Today.AddDays(1) ? "Tomorrow"
                : e.Start.ToString("ddd MM/dd");
            return $"[dim]{day} {e.Start:h:mm tt}[/]  {Markup.Escape(e.Title)}";
        })
        .ToList();
}

// Today's high/low from the NWS daily forecast, formatted for the header line
// (" • H 88° / L 71°"). In the evening the day period is gone, so only the low
// may be available. Empty string when the lookup fails — the header still shows.
static async Task<string> FetchNwsHighLowAsync(string dailyForecastUrl)
{
    try
    {
        var json = await Web.GetStringAsync(dailyForecastUrl);
        using var doc = JsonDocument.Parse(json);

        int? high = null, low = null;
        foreach (var period in doc.RootElement.GetProperty("properties").GetProperty("periods")
                     .EnumerateArray().Take(2))
        {
            var isDay = period.TryGetProperty("isDaytime", out var d) && d.GetBoolean();
            var temp = period.GetProperty("temperature").GetInt32();
            if (isDay) high ??= temp;
            else low ??= temp;
        }

        if (high == null && low == null) return "";
        var partsText = new List<string>();
        if (high != null) partsText.Add($"H {high}°");
        if (low != null) partsText.Add($"L {low}°");
        return " • " + string.Join(" / ", partsText);
    }
    catch
    {
        return "";
    }
}

// One-line current conditions for the header, cached on disk like the calendar:
// a fresh cache (15 min) skips the network entirely, and when the fetch fails
// (NWS has intermittent hiccups) the last good reading is shown with its age.
static async Task<string?> FetchCurrentWeatherLineAsync()
{
    var cachePath = Paths.Data("weather-cache.txt");
    var cacheTime = File.Exists(cachePath) ? File.GetLastWriteTime(cachePath) : (DateTime?)null;

    if (cacheTime != null && DateTime.Now - cacheTime < TimeSpan.FromMinutes(15))
        return await File.ReadAllTextAsync(cachePath);

    var line = await BuildCurrentWeatherLineAsync();
    if (line != null)
    {
        try { await File.WriteAllTextAsync(cachePath, line); } catch { /* cache is best effort */ }
        return line;
    }

    // Fetch failed — a reading from the last few hours beats an empty header.
    if (cacheTime != null && DateTime.Now - cacheTime < TimeSpan.FromHours(6))
        return await File.ReadAllTextAsync(cachePath) + $" [dim](as of {cacheTime:h:mm tt})[/]";

    return null;
}

// Builds the conditions line from the live APIs, or null when no weather location
// has been saved yet or every lookup fails. Prefers NWS (like the full forecast
// view) and falls back to Open-Meteo.
static async Task<string?> BuildCurrentWeatherLineAsync()
{
    try
    {
        var place = LoadCachedWeatherPlace();
        if (place == null)
        {
            var saved = LoadSavedWeatherLocation();
            if (saved == null) return null;
            var matches = await GeocodeLocationAsync(saved);
            if (matches.Count == 0) return null;
            place = matches[0];
            SaveWeatherLocation(saved, place); // cache coordinates for next launch
        }

        var lat = place.Lat.ToString("F4", CultureInfo.InvariantCulture);
        var lon = place.Lon.ToString("F4", CultureInfo.InvariantCulture);
        try
        {
            var points = await Web.GetStringAsync($"https://api.weather.gov/points/{lat},{lon}");
            string hourlyUrl;
            string? dailyUrl;
            using (var pointsDoc = JsonDocument.Parse(points))
            {
                var props = pointsDoc.RootElement.GetProperty("properties");
                hourlyUrl = props.GetProperty("forecastHourly").GetString()
                            ?? throw new InvalidOperationException("No hourly forecast URL.");
                dailyUrl = props.TryGetProperty("forecast", out var f) ? f.GetString() : null;
            }

            var highLow = dailyUrl != null ? await FetchNwsHighLowAsync(dailyUrl) : "";

            var json = await Web.GetStringAsync(hourlyUrl);
            using var doc = JsonDocument.Parse(json);
            var now = doc.RootElement.GetProperty("properties").GetProperty("periods")[0];
            return $"[bold]{now.GetProperty("temperature").GetInt32()}°{now.GetProperty("temperatureUnit").GetString()}[/] " +
                   $"{Markup.Escape(now.GetProperty("shortForecast").GetString() ?? "")}{highLow} • " +
                   $"wind {Markup.Escape(now.GetProperty("windSpeed").GetString() ?? "?")} " +
                   $"[dim]— {Markup.Escape(place.Display)}[/]";
        }
        catch
        {
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
                      "&current=temperature_2m,weather_code,wind_speed_10m" +
                      "&daily=temperature_2m_max,temperature_2m_min&forecast_days=1" +
                      "&temperature_unit=fahrenheit&wind_speed_unit=mph&timezone=auto";
            var json = await Web.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var current = doc.RootElement.GetProperty("current");
            var daily = doc.RootElement.GetProperty("daily");
            var highLow = $" • H {daily.GetProperty("temperature_2m_max")[0].GetDouble():F0}° " +
                          $"/ L {daily.GetProperty("temperature_2m_min")[0].GetDouble():F0}°";
            return $"[bold]{current.GetProperty("temperature_2m").GetDouble():F0}°F[/] " +
                   $"{WeatherCodeText(current.GetProperty("weather_code").GetInt32())}{highLow} • " +
                   $"wind {current.GetProperty("wind_speed_10m").GetDouble():F0} mph " +
                   $"[dim]— {Markup.Escape(place.Display)}[/]";
        }
    }
    catch
    {
        return null; // the header is optional — never block the menu over it
    }
}

// Last-used weather location, kept in data/weather.txt.
static string? LoadSavedWeatherLocation()
{
    var path = Paths.Data("weather.txt");
    if (!File.Exists(path)) return null;
    return File.ReadLines(path)
        .Select(l => l.Trim())
        .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
}

// Geocoded coordinates cached on the second line of weather.txt ("display|lat|lon"),
// so the menu-header conditions cost one API call instead of a geocode every launch.
static WeatherPlace? LoadCachedWeatherPlace()
{
    var path = Paths.Data("weather.txt");
    if (!File.Exists(path)) return null;

    foreach (var line in File.ReadLines(path).Select(l => l.Trim()))
    {
        if (line.StartsWith('#')) continue;
        var parts = line.Split('|');
        if (parts.Length == 3 &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
        {
            return new WeatherPlace(parts[0], lat, lon);
        }
    }
    return null;
}

static void SaveWeatherLocation(string location, WeatherPlace? place = null)
{
    try
    {
        var content = location + Environment.NewLine;
        if (place != null)
            content += FormattableString.Invariant($"{place.Display}|{place.Lat}|{place.Lon}") + Environment.NewLine;
        File.WriteAllText(Paths.Data("weather.txt"), content);
    }
    catch (Exception ex)
    {
        AppLog.Debug("SaveWeatherLocation", ex); // best effort
    }
}

// Lets a message be read before the main menu clears the screen.
static void PauseForKey()
{
    AnsiConsole.MarkupLine("[grey]Press any key to continue...[/]");
    Console.ReadKey(intercept: true);
}

// Terminal NYT Spelling Bee. Fetches the day's puzzle (letters + answer list) once,
// caches it, and validates/scores entirely offline. Type words; blank line shuffles;
// ":q" quits. Only the puzzle's functional word data is used — no NYT text.
static async Task PlaySpellingBeeAsync()
{
    var puzzle = await AnsiConsole.Status().StartAsync("Fetching today's Spelling Bee...",
        async _ => await FetchSpellingBeeAsync());
    if (puzzle == null)
    {
        AnsiConsole.MarkupLine("[red]Could not load the Spelling Bee puzzle (and no cached copy is available).[/]\n");
        PauseForKey();
        return;
    }

    var answers = new HashSet<string>(puzzle.Answers, StringComparer.OrdinalIgnoreCase);
    var pangrams = new HashSet<string>(puzzle.Pangrams, StringComparer.OrdinalIgnoreCase);
    var valid = puzzle.ValidLetters.Select(s => char.ToLowerInvariant(s[0])).ToHashSet();
    var center = char.ToLowerInvariant(puzzle.CenterLetter[0]);
    var totalScore = puzzle.Answers.Sum(BeeScore);

    // Resume any words already found for this puzzle (local save + NYT sync).
    var found = LoadBeeProgress(puzzle.PrintDate);
    found.IntersectWith(answers); // guard against a mismatched/old save

    var syncNote = "";
    if (NytBrowser.IsConnected && puzzle.Id.Length > 0)
    {
        var remote = await AnsiConsole.Status().StartAsync("Syncing with your NYT account...",
            async _ => await NytBrowser.GetSpellingBeeFoundAsync(puzzle.Id));
        if (remote != null)
        {
            remote.IntersectWith(answers);
            found.UnionWith(remote); // merge both ways — never lose a word
            syncNote = "   [dim](NYT sync on)[/]";
        }
        else
        {
            // Pull failed now, but the exit push re-reads and unions, so nothing is lost.
            syncNote = "   [dim](NYT sync will retry on exit)[/]";
        }
    }

    var score = found.Sum(BeeScore);
    var outer = puzzle.OuterLetters.Select(s => s.ToUpperInvariant()).ToList();
    var showHintsPanel = false;
    var message = "[grey]Type a word and press Enter. Blank line shuffles. \":hint\" popup, \":hints\" side-by-side, \":q\" to quit.[/]";

    // Live sync: a background pump pushes the latest snapshot to NYT as words are
    // found, chaining until caught up — so the final state syncs even if the player
    // goes idle or closes without a clean exit.
    var syncer = new BackgroundSyncer();
    void MaybePushBee()
    {
        if (!NytBrowser.IsConnected || puzzle.Id.Length == 0) return;
        var snapshot = found.ToList();                 // captured on the game thread
        var rank = BeeRank(score, totalScore).Name;
        syncer.Queue(() => NytBrowser.SaveSpellingBeeFoundAsync(puzzle.Id, puzzle.PrintDate, snapshot, rank));
    }

    // Push once on open so any local words that never synced go up immediately.
    if (found.Count > 0) MaybePushBee();

    while (true)
    {
        AnsiConsole.Clear();

        var (rankName, nextInfo) = BeeRank(score, totalScore);
        var geniusScore = (int)(0.70 * totalScore);
        var toGenius = geniusScore - score > 0
            ? $"   [dim](Genius at {geniusScore}, +{geniusScore - score})[/]"
            : "   [bold gold1](Genius reached!)[/]";

        var syncStatus = syncer.Failed ? "   [red](NYT sync failing — reconnect NYT account)[/]" : syncNote;
        var lines = new List<string>
        {
            $"[bold blue]Spelling Bee[/] [dim]{Markup.Escape(puzzle.DisplayDate)}[/]{syncStatus}",
            "",
            $"   Center: [bold yellow]{char.ToUpperInvariant(center)}[/]    Letters: [bold]{string.Join(" ", outer)}[/]",
            "",
            $"Rank: [bold green]{rankName}[/]   Score: [bold]{score}[/]   Words: [bold]{found.Count}[/]{nextInfo}{toGenius}",
        };
        if (found.Count > 0)
        {
            var shown = found.OrderBy(w => w, StringComparer.Ordinal)
                .Select(w => pangrams.Contains(w)
                    ? $"[bold gold1]{Markup.Escape(w.ToUpperInvariant())}[/]"
                    : Markup.Escape(w));
            lines.Add("");
            lines.Add($"[dim]Found:[/] {string.Join("  ", shown)}");
        }
        var gamePanel = new Rows(lines.Select(l => new Markup(l)));

        // Hints layout when toggled on: side-by-side if the terminal is wide enough,
        // otherwise stacked above the game if it's tall enough, otherwise just the
        // game with a nudge (the ":hint" popup still works at any size).
        const int sideBySideMinWidth = 110;
        if (showHintsPanel && Console.WindowWidth >= sideBySideMinWidth)
        {
            var layout = new Table().Border(TableBorder.None).HideHeaders();
            layout.AddColumn(new TableColumn("game"));
            layout.AddColumn(new TableColumn("hints"));
            layout.AddRow(gamePanel, BuildBeeHints(puzzle, found, expand: false));
            AnsiConsole.Write(layout);
        }
        else if (showHintsPanel)
        {
            var hintsPanel = BuildBeeHints(puzzle, found, expand: false);
            // Stack only if the game + hints + prompt actually fit the window height.
            var needed = RenderToLines(hintsPanel).Count + RenderToLines(gamePanel).Count + 3;
            if (needed <= Console.WindowHeight)
            {
                AnsiConsole.Write(hintsPanel);
                AnsiConsole.Write(gamePanel);
            }
            else
            {
                AnsiConsole.Write(gamePanel);
                message = $"[grey](Terminal too small for docked hints — widen to ~{sideBySideMinWidth} cols " +
                          "or make it taller; \":hint\" shows a full-screen view.)[/] " + message;
            }
        }
        else
        {
            AnsiConsole.Write(gamePanel);
        }

        AnsiConsole.MarkupLine($"\n{message}");
        AnsiConsole.Markup("[green]> [/]");
        var input = Console.ReadLine();

        if (input == null) break;                          // Ctrl+Z / EOF
        var word = input.Trim().ToLowerInvariant();

        if (word is ":q" or ":quit" or "quit") break;
        if (word is ":hints")                              // toggle the side panel
        {
            showHintsPanel = !showHintsPanel;
            message = showHintsPanel ? "[grey]Side-by-side hints on.[/]" : "[grey]Side-by-side hints off.[/]";
            continue;
        }
        if (word is ":hint" or ":h" or "?")
        {
            ShowInPager(BuildBeeHints(puzzle, found));
            continue;
        }
        if (word.Length == 0)                              // shuffle
        {
            for (var i = outer.Count - 1; i > 0; i--)
            {
                var j = Random.Shared.Next(i + 1);
                (outer[i], outer[j]) = (outer[j], outer[i]);
            }
            message = "[grey]Shuffled.[/]";
            continue;
        }

        if (word.Length < 4)
            message = "[yellow]Too short — words must be at least 4 letters.[/]";
        else if (word.Any(c => !valid.Contains(c)))
            message = "[yellow]Uses a letter that isn't in the hive.[/]";
        else if (!word.Contains(center))
            message = $"[yellow]Must include the center letter [bold]{char.ToUpperInvariant(center)}[/].[/]";
        else if (found.Contains(word))
            message = "[grey]Already found.[/]";
        else if (!answers.Contains(word))
            message = "[red]Not in the word list.[/]";
        else
        {
            var pts = BeeScore(word);
            found.Add(word);
            var before = BeeRank(score, totalScore).Name;
            score += pts;
            var after = BeeRank(score, totalScore).Name;

            message = pangrams.Contains(word)
                ? $"[bold gold1]PANGRAM![/] [green]{Markup.Escape(word.ToUpperInvariant())} +{pts}[/]"
                : $"[green]{Markup.Escape(word)} +{pts}[/]";
            if (after != before) message += $"   [bold green]➜ {after}![/]";

            SaveBeeProgress(puzzle.PrintDate, found); // persist after each new word
            MaybePushBee();                           // and sync up in the background
        }
    }

    SaveBeeProgress(puzzle.PrintDate, found);

    // Push progress back to NYT. Safe unconditionally: the save re-reads the server
    // and writes the union, so a failed start-pull can't cause a word to be lost.
    if (NytBrowser.IsConnected && puzzle.Id.Length > 0)
        await AnsiConsole.Status().StartAsync("Saving progress to your NYT account...",
            async _ => await NytBrowser.SaveSpellingBeeFoundAsync(
                puzzle.Id, puzzle.PrintDate, found, BeeRank(score, totalScore).Name));

    // End-of-session summary, including any pangrams missed.
    AnsiConsole.Clear();
    var (finalRank, _) = BeeRank(score, totalScore);
    var missedPangrams = puzzle.Pangrams.Where(p => !found.Contains(p)).ToList();
    var sb = new StringBuilder();
    sb.Append($"[bold]Spelling Bee — {Markup.Escape(puzzle.DisplayDate)}[/]\n\n");
    sb.Append($"Final rank: [bold green]{finalRank}[/]\n");
    sb.Append($"Score: [bold]{score}[/] of {totalScore}\n");
    sb.Append($"Words: [bold]{found.Count}[/] of {puzzle.Answers.Count}\n");
    if (missedPangrams.Count > 0)
        sb.Append($"\n[grey]Pangrams you missed:[/] [gold1]{string.Join(", ", missedPangrams.Select(p => Markup.Escape(p.ToUpperInvariant())))}[/]");
    var panel = new Panel(new Markup(sb.ToString().TrimEnd()))
    {
        Border = BoxBorder.Rounded,
        Padding = new Padding(1, 1, 1, 1),
        Expand = true
    };
    ShowInPager(panel);
}

// Builds NYT-style hints: word/point/pangram totals, a first-letter × length count
// grid, and the "two-letter list" — all derived from the answer set, so no spoilers
// beyond what NYT's own hint page shows. Cells show found/total so it's a live aid.
static IRenderable BuildBeeHints(SpellingBeePuzzle puzzle, HashSet<string> found, bool expand = true)
{
    var answers = puzzle.Answers;
    var letters = puzzle.ValidLetters.Select(s => char.ToUpperInvariant(s[0])).OrderBy(c => c).ToList();
    var lengths = answers.Select(a => a.Length).Distinct().OrderBy(n => n).ToList();

    var sb = new StringBuilder();
    sb.Append($"[bold]Hints — {Markup.Escape(puzzle.DisplayDate)}[/]\n");
    sb.Append($"[dim]Words:[/] {found.Count}/{answers.Count}    " +
              $"[dim]Points:[/] {found.Sum(BeeScore)}/{answers.Sum(BeeScore)}    " +
              $"[dim]Pangrams:[/] {found.Count(w => puzzle.Pangrams.Contains(w, StringComparer.OrdinalIgnoreCase))}/{puzzle.Pangrams.Count}\n\n");

    // Grid: rows = first letter, columns = word length; each cell = found/total.
    var grid = new Table().Border(TableBorder.Rounded);
    grid.AddColumn(new TableColumn("[dim]start[/]"));
    foreach (var len in lengths) grid.AddColumn(new TableColumn($"[dim]{len}[/]").RightAligned());
    grid.AddColumn(new TableColumn("[dim]Σ[/]").RightAligned());

    foreach (var letter in letters)
    {
        var forLetter = answers.Where(a => char.ToUpperInvariant(a[0]) == letter).ToList();
        if (forLetter.Count == 0) continue;
        var cells = new List<string> { $"[bold]{letter}[/]" };
        foreach (var len in lengths)
        {
            var tot = forLetter.Count(a => a.Length == len);
            var got = forLetter.Count(a => a.Length == len && found.Contains(a));
            cells.Add(tot == 0 ? "[dim]·[/]" : (got == tot ? $"[green]{got}/{tot}[/]" : $"{got}/{tot}"));
        }
        var rowTot = forLetter.Count;
        var rowGot = forLetter.Count(found.Contains);
        cells.Add(rowGot == rowTot ? $"[green]{rowGot}/{rowTot}[/]" : $"[bold]{rowGot}/{rowTot}[/]");
        grid.AddRow(cells.ToArray());
    }

    // Two-letter list: counts of words grouped by their first two letters.
    var twoLetter = answers
        .GroupBy(a => a[..2].ToUpperInvariant())
        .OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g =>
        {
            var got = g.Count(found.Contains);
            var tot = g.Count();
            return got == tot ? $"[green]{g.Key} {got}/{tot}[/]" : $"{g.Key} {got}/{tot}";
        });

    var rows = new Rows(
        new Markup(sb.ToString().TrimEnd()),
        grid,
        new Markup("\n[dim]Two-letter list:[/]  " + string.Join("   ", twoLetter)));

    return new Panel(rows)
    {
        Border = BoxBorder.Rounded,
        Padding = new Padding(1, 1, 1, 1),
        Expand = expand
    };
}

// Local progress: found words for a given puzzle date, in spellingbee-progress.json.
static HashSet<string> LoadBeeProgress(string printDate)
{
    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    try
    {
        var path = Paths.Data("spellingbee-progress.json");
        if (!File.Exists(path)) return set;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.TryGetProperty("printDate", out var d) && d.GetString() == printDate &&
            doc.RootElement.TryGetProperty("found", out var f))
        {
            foreach (var w in f.EnumerateArray())
                if (w.GetString() is { } s) set.Add(s);
        }
    }
    catch { /* corrupt/missing save — start fresh */ }
    return set;
}

static void SaveBeeProgress(string printDate, HashSet<string> found)
{
    try
    {
        var path = Paths.Data("spellingbee-progress.json");
        var json = JsonSerializer.Serialize(new { printDate, found = found.ToArray() });
        File.WriteAllText(path, json);
    }
    catch { /* best effort */ }
}

// NYT scoring: 4-letter words = 1 point; longer = one point per letter; pangrams
// (words using all seven letters) get 7 bonus points.
static int BeeScore(string word)
{
    var pts = word.Length == 4 ? 1 : word.Length;
    if (word.Distinct().Count() == 7) pts += 7;
    return pts;
}

// Returns (rank name, progress hint) for a score, using NYT's rank thresholds.
static (string Name, string NextInfo) BeeRank(int score, int total)
{
    (string Name, double Pct)[] tiers =
    [
        ("Beginner", 0), ("Good Start", 2), ("Moving Up", 5), ("Good", 8), ("Solid", 15),
        ("Nice", 25), ("Great", 40), ("Amazing", 50), ("Genius", 70), ("Queen Bee", 100),
    ];

    var current = 0;
    for (var i = 0; i < tiers.Length; i++)
        if (score >= (int)(tiers[i].Pct / 100.0 * total)) current = i;

    if (current >= tiers.Length - 1)
        return (tiers[current].Name, "");

    var nextScore = (int)(tiers[current + 1].Pct / 100.0 * total);
    return (tiers[current].Name, $"   [dim](+{nextScore - score} to {tiers[current + 1].Name})[/]");
}

// Fetches today's Spelling Bee from NYT's puzzle page (the data is embedded as a
// window.gameData JSON blob), caching it to disk for offline replay.
static async Task<SpellingBeePuzzle?> FetchSpellingBeeAsync()
{
    var cachePath = Paths.Data("spellingbee-cache.json");

    try
    {
        var html = await Web.GetStringAsync("https://www.nytimes.com/puzzles/spelling-bee");
        var m = Regex.Match(html, @"window\.gameData\s*=\s*(\{.*?\})\s*</script>", RegexOptions.Singleline);
        if (m.Success)
        {
            using var doc = JsonDocument.Parse(m.Groups[1].Value);
            var today = doc.RootElement.GetProperty("today");
            try { await File.WriteAllTextAsync(cachePath, today.GetRawText()); } catch { /* cache best effort */ }
            return ParseBee(today);
        }
    }
    catch
    {
        // fall through to cache
    }

    if (File.Exists(cachePath))
    {
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(cachePath));
            return ParseBee(doc.RootElement);
        }
        catch { /* corrupt cache */ }
    }
    return null;
}

static SpellingBeePuzzle ParseBee(JsonElement today)
{
    string[] Arr(string name) => today.GetProperty(name).EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
    return new SpellingBeePuzzle(
        today.GetProperty("centerLetter").GetString() ?? "",
        Arr("outerLetters"),
        Arr("validLetters"),
        Arr("answers").ToList(),
        Arr("pangrams").ToList(),
        today.TryGetProperty("displayDate", out var d) ? d.GetString() ?? "" : "",
        today.TryGetProperty("printDate", out var pd) ? pd.GetString() ?? "" : "",
        today.TryGetProperty("id", out var idEl)
            ? (idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64().ToString() : idEl.GetString() ?? "")
            : "");
}

// Terminal NYT Connections. Group the 16 words into 4 sets of 4 by entering their
// numbers. Four mistakes ends it. Public data; cached; local progress.
static async Task PlayConnectionsAsync()
{
    var puzzle = await AnsiConsole.Status().StartAsync("Fetching today's Connections...",
        async _ => await FetchConnectionsAsync());
    if (puzzle == null)
    {
        AnsiConsole.MarkupLine("[red]Could not load Connections (and no cached copy is available).[/]\n");
        PauseForKey();
        return;
    }

    string[] colors = ["yellow", "green", "blue", "mediumpurple"];
    var wordCategory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    for (var ci = 0; ci < puzzle.Categories.Count; ci++)
        foreach (var w in puzzle.Categories[ci].Words) wordCategory[w] = ci;

    // Grid positions for each category (level == category index in NYT's data).
    var categoryPositions = new List<int>[puzzle.Categories.Count];
    for (var ci = 0; ci < categoryPositions.Length; ci++) categoryPositions[ci] = new List<int>();
    for (var p = 0; p < 16; p++) categoryPositions[wordCategory[puzzle.WordByPosition[p]]].Add(p);

    var (solved, mistakes) = LoadConnectionsProgress(puzzle.PrintDate);

    // NYT two-way sync: pull solved groups + mistakes, merge (never un-solve).
    if (NytBrowser.IsConnected && puzzle.Id.Length > 0)
    {
        var state = await AnsiConsole.Status().StartAsync("Syncing with your NYT account...",
            async _ => await NytBrowser.GetGameStateAsync("connections", puzzle.Id));
        if (state != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(state);
                if (doc.RootElement.TryGetProperty("solvedCategories", out var sc))
                    foreach (var cat in sc.EnumerateArray())
                        if (cat.TryGetProperty("level", out var lv)) solved.Add(lv.GetInt32());
                if (doc.RootElement.TryGetProperty("mistakes", out var mk))
                    mistakes = Math.Max(mistakes, mk.GetInt32());
            }
            catch (Exception ex) { AppLog.Debug("io", ex); }
        }
    }

    var syncer = new BackgroundSyncer();
    void MaybePushConnections()
    {
        if (!NytBrowser.IsConnected || puzzle.Id.Length == 0) return;
        var gd = BuildConnectionsGameData(categoryPositions, solved, mistakes);
        syncer.Queue(() => NytBrowser.SaveGameStateAsync("connections", puzzle.Id, puzzle.PrintDate, gd));
    }
    if (solved.Count > 0 || mistakes > 0) MaybePushConnections();

    var message = "[grey]Enter the 4 numbers of a group (e.g. \"1 6 9 14\"). \":q\" to quit.[/]";

    while (true)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine($"[bold blue]Connections[/] [dim]{Markup.Escape(puzzle.PrintDate)}[/]" +
            (syncer.Failed ? "   [red](NYT sync failing — reconnect NYT account)[/]" : "") + "\n");

        // Solved groups first, colored.
        foreach (var ci in solved.OrderBy(x => x))
        {
            var cat = puzzle.Categories[ci];
            AnsiConsole.MarkupLine($"[black on {colors[ci]}] {Markup.Escape(cat.Title)}: {Markup.Escape(string.Join(", ", cat.Words))} [/]");
        }
        if (solved.Count > 0) AnsiConsole.WriteLine();

        // Remaining words, numbered by their original position (1-16).
        for (var row = 0; row < 4; row++)
        {
            var sb = new StringBuilder("  ");
            for (var col = 0; col < 4; col++)
            {
                var pos = row * 4 + col;
                var word = puzzle.WordByPosition[pos];
                var solvedWord = solved.Contains(wordCategory[word]);
                var cell = solvedWord ? $"[grey37]{pos + 1,2}. {Markup.Escape(word)}[/]" : $"[bold]{pos + 1,2}.[/] {Markup.Escape(word)}";
                sb.Append(cell.PadRight(28));
            }
            AnsiConsole.MarkupLine(sb.ToString());
        }

        var dots = string.Concat(Enumerable.Range(0, 4).Select(i => i < 4 - mistakes ? "●" : "○"));
        AnsiConsole.MarkupLine($"\nMistakes remaining: [bold]{dots}[/]");

        if (solved.Count == 4) { message = "[bold green]Solved it![/]"; }
        else if (mistakes >= 4) { message = "[bold red]Out of guesses.[/]"; }

        AnsiConsole.MarkupLine($"\n{message}");

        if (solved.Count == 4 || mistakes >= 4)
        {
            // Reveal any unsolved groups.
            for (var ci = 0; ci < puzzle.Categories.Count; ci++)
                if (!solved.Contains(ci))
                    AnsiConsole.MarkupLine($"[grey](answer) {Markup.Escape(puzzle.Categories[ci].Title)}: {Markup.Escape(string.Join(", ", puzzle.Categories[ci].Words))}[/]");
            AnsiConsole.Markup("\n[grey]Press Enter to return...[/]");
            Console.ReadLine();
            break;
        }

        AnsiConsole.Markup("[green]> [/]");
        var input = Console.ReadLine();
        if (input == null || input.Trim() is ":q" or ":quit" or "quit") break;

        var nums = System.Text.RegularExpressions.Regex.Matches(input, @"\d+")
            .Select(m => int.Parse(m.Value)).Distinct().ToList();
        if (nums.Count != 4 || nums.Any(n => n < 1 || n > 16))
        {
            message = "[yellow]Enter exactly 4 numbers from 1–16.[/]";
            continue;
        }
        var chosen = nums.Select(n => puzzle.WordByPosition[n - 1]).ToList();
        if (chosen.Any(w => solved.Contains(wordCategory[w])))
        {
            message = "[yellow]One of those is already solved — pick from the remaining words.[/]";
            continue;
        }

        var cats = chosen.Select(w => wordCategory[w]).ToList();
        if (cats.Distinct().Count() == 1)
        {
            solved.Add(cats[0]);
            message = $"[green]Correct — {Markup.Escape(puzzle.Categories[cats[0]].Title)}![/]";
        }
        else
        {
            mistakes++;
            var oneAway = cats.GroupBy(c => c).Any(g => g.Count() == 3);
            message = oneAway ? "[yellow]One away…[/]" : "[red]Not a group.[/]";
        }
        SaveConnectionsProgress(puzzle.PrintDate, solved, mistakes);
        MaybePushConnections();
    }

    // Final flush to NYT.
    if (NytBrowser.IsConnected && puzzle.Id.Length > 0)
        await AnsiConsole.Status().StartAsync("Saving to your NYT account...",
            async _ => await NytBrowser.SaveGameStateAsync("connections", puzzle.Id, puzzle.PrintDate,
                BuildConnectionsGameData(categoryPositions, solved, mistakes)));

    AnsiConsole.Clear();
}

// Builds Connections game_data matching NYT's shape: solvedCategories (with each
// group's cards + level + solve order), a guesses history (correct groups plus
// filler incorrect guesses to reflect the mistake count), and the status flags.
static string BuildConnectionsGameData(List<int>[] categoryPositions, HashSet<int> solved, int mistakes)
{
    object Card(int level, int position) => new { level, position };
    object[] CardsFor(int ci) => categoryPositions[ci].Select(p => Card(ci, p)).ToArray();

    var solvedList = solved.OrderBy(x => x).ToList();
    var solvedCategories = solvedList
        .Select((ci, k) => new { cards = CardsFor(ci), level = ci, orderSolved = k + 1 })
        .ToArray();

    var guesses = new List<object>();
    foreach (var ci in solvedList)
        guesses.Add(new { cards = CardsFor(ci), correct = true });
    // Filler incorrect guesses (one card from each category → guaranteed wrong) so
    // the guess count reflects the mistake total; harmless for board/stat display.
    if (categoryPositions.Length >= 4)
    {
        var wrong = new { cards = Enumerable.Range(0, 4).Select(l => Card(l, categoryPositions[l][0])).ToArray(), correct = false };
        for (var i = 0; i < mistakes; i++) guesses.Add(wrong);
    }

    return JsonSerializer.Serialize(new
    {
        guesses = guesses.ToArray(),
        isPlayingArchive = false,
        mistakes,
        puzzleComplete = solved.Count == 4 || mistakes >= 4,
        puzzleWon = solved.Count == 4,
        solvedCategories
    });
}

static async Task<ConnectionsPuzzle?> FetchConnectionsAsync()
{
    var cachePath = Paths.Data("connections-cache.json");
    var date = DateTime.Now.ToString("yyyy-MM-dd");
    try
    {
        var json = await Web.GetStringAsync($"https://www.nytimes.com/svc/connections/v2/{date}.json");
        var p = ParseConnections(json);
        if (p != null) { try { await File.WriteAllTextAsync(cachePath, json); } catch (Exception ex) { AppLog.Debug("io", ex); } return p; }
    }
    catch (Exception ex) { AppLog.Debug("io", ex); }
    if (File.Exists(cachePath))
    {
        try { return ParseConnections(await File.ReadAllTextAsync(cachePath)); } catch (Exception ex) { AppLog.Debug("io", ex); }
    }
    return null;
}

static ConnectionsPuzzle? ParseConnections(string json)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetInt32().ToString() : "";
        var printDate = root.TryGetProperty("print_date", out var pd) ? pd.GetString() ?? "" : "";
        var byPos = new string[16];
        var cats = new List<ConnectionsCategory>();
        foreach (var cat in root.GetProperty("categories").EnumerateArray())
        {
            var words = new List<string>();
            foreach (var card in cat.GetProperty("cards").EnumerateArray())
            {
                var content = card.GetProperty("content").GetString() ?? "";
                var pos = card.GetProperty("position").GetInt32();
                byPos[pos] = content;
                words.Add(content);
            }
            cats.Add(new ConnectionsCategory(cat.GetProperty("title").GetString() ?? "", words.ToArray()));
        }
        return new ConnectionsPuzzle(id, printDate, byPos, cats);
    }
    catch (Exception ex) { AppLog.Debug("returned null", ex); return null; }
}

static (HashSet<int> Solved, int Mistakes) LoadConnectionsProgress(string date)
{
    try
    {
        var path = Paths.Data("connections-progress.json");
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("date", out var d) && d.GetString() == date)
            {
                var solved = doc.RootElement.GetProperty("solved").EnumerateArray().Select(e => e.GetInt32()).ToHashSet();
                var mistakes = doc.RootElement.GetProperty("mistakes").GetInt32();
                return (solved, mistakes);
            }
        }
    }
    catch (Exception ex) { AppLog.Debug("io", ex); }
    return (new HashSet<int>(), 0);
}

static void SaveConnectionsProgress(string date, HashSet<int> solved, int mistakes)
{
    try
    {
        var path = Paths.Data("connections-progress.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { date, solved = solved.ToArray(), mistakes }));
    }
    catch (Exception ex) { AppLog.Debug("io", ex); }
}

// Terminal NYT Strands. Type theme words you spot in the board; the spangram spans
// it. Found words light up on the grid. Public data; cached; local progress.
static async Task PlayStrandsAsync()
{
    var puzzle = await AnsiConsole.Status().StartAsync("Fetching today's Strands...",
        async _ => await FetchStrandsAsync());
    if (puzzle == null)
    {
        AnsiConsole.MarkupLine("[red]Could not load Strands (and no cached copy is available).[/]\n");
        PauseForKey();
        return;
    }

    var allWords = new List<string>(puzzle.ThemeWords) { puzzle.Spangram };
    var found = LoadStrandsProgress(puzzle.PrintDate);
    found.IntersectWith(allWords);

    // NYT two-way sync: pull found words from the server and merge; preserve the
    // server's list of non-theme words found so a push doesn't erase them.
    var otherWords = new List<string>();
    if (NytBrowser.IsConnected && puzzle.Id.Length > 0)
    {
        var state = await AnsiConsole.Status().StartAsync("Syncing with your NYT account...",
            async _ => await NytBrowser.GetGameStateAsync("strands", puzzle.Id));
        if (state != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(state);
                if (doc.RootElement.TryGetProperty("history", out var hist))
                    foreach (var h in hist.EnumerateArray())
                    {
                        var w = (h.GetProperty("w").GetString() ?? "").ToUpperInvariant();
                        if (allWords.Any(a => string.Equals(a, w, StringComparison.OrdinalIgnoreCase))) found.Add(w);
                    }
                if (doc.RootElement.TryGetProperty("otherWordsFound", out var ow))
                    otherWords = ow.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
            }
            catch (Exception ex) { AppLog.Debug("io", ex); }
        }
    }

    var syncer = new BackgroundSyncer();
    void MaybePushStrands()
    {
        if (!NytBrowser.IsConnected || puzzle.Id.Length == 0) return;
        var gd = BuildStrandsGameData(puzzle, found, otherWords, allWords.Count);
        syncer.Queue(() => NytBrowser.SaveGameStateAsync("strands", puzzle.Id, puzzle.PrintDate, gd));
    }
    if (found.Count > 0) MaybePushStrands(); // sync any local-only words up on open

    var message = "[grey]Type a theme word you spot. \":q\" to quit.[/]";

    while (true)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine($"[bold blue]Strands[/] [dim]{Markup.Escape(puzzle.PrintDate)}[/]" +
            (syncer.Failed ? "   [red](NYT sync failing — reconnect NYT account)[/]" : ""));
        AnsiConsole.MarkupLine($"[italic]Theme: {Markup.Escape(puzzle.Clue)}[/]\n");

        // Map each cell to a color if it belongs to a found word (spangram distinct).
        var cellColor = new Dictionary<(int, int), string>();
        foreach (var w in found)
        {
            var color = string.Equals(w, puzzle.Spangram, StringComparison.OrdinalIgnoreCase) ? "gold1" : "green";
            if (puzzle.Coords.TryGetValue(w, out var pts))
                foreach (var pt in pts) cellColor[pt] = color;
        }

        for (var r = 0; r < puzzle.Board.Length; r++)
        {
            var sb = new StringBuilder("  ");
            for (var c = 0; c < puzzle.Board[r].Length; c++)
            {
                var ch = char.ToUpperInvariant(puzzle.Board[r][c]);
                sb.Append(cellColor.TryGetValue((r, c), out var col)
                    ? $"[black on {col}] {ch} [/]"
                    : $"[white on grey27] {ch} [/]");
                sb.Append(' ');
            }
            AnsiConsole.MarkupLine(sb.ToString());
        }

        AnsiConsole.MarkupLine($"\nFound: [bold]{found.Count}[/] of {allWords.Count}   " +
            (found.Contains(puzzle.Spangram) ? "[gold1]★ spangram found[/]" : "[dim]spangram not found[/]"));
        if (found.Count > 0)
            AnsiConsole.MarkupLine("[dim]" + string.Join(", ", found.OrderBy(w => w)
                .Select(w => string.Equals(w, puzzle.Spangram, StringComparison.OrdinalIgnoreCase) ? $"[gold1]{Markup.Escape(w)}[/]" : Markup.Escape(w))) + "[/]");

        if (found.Count == allWords.Count) message = "[bold green]All found — nicely done![/]";
        AnsiConsole.MarkupLine($"\n{message}");

        if (found.Count == allWords.Count)
        {
            AnsiConsole.Markup("[grey]Press Enter to return...[/]");
            Console.ReadLine();
            break;
        }

        AnsiConsole.Markup("[green]> [/]");
        var input = Console.ReadLine();
        if (input == null) break;
        var word = input.Trim().ToUpperInvariant();
        if (word is ":Q" or ":QUIT" or "QUIT") break;
        if (word.Length == 0) continue;

        if (found.Contains(word)) message = "[grey]Already found.[/]";
        else if (allWords.Any(w => string.Equals(w, word, StringComparison.OrdinalIgnoreCase)))
        {
            found.Add(word);
            message = string.Equals(word, puzzle.Spangram, StringComparison.OrdinalIgnoreCase)
                ? $"[bold gold1]★ Spangram! {Markup.Escape(word)}[/]"
                : $"[green]{Markup.Escape(word)} ✓[/]";
            SaveStrandsProgress(puzzle.PrintDate, found);
            MaybePushStrands();
        }
        else message = "[red]Not a theme word.[/]";
    }

    // Final flush to NYT.
    if (NytBrowser.IsConnected && puzzle.Id.Length > 0)
        await AnsiConsole.Status().StartAsync("Saving to your NYT account...",
            async _ => await NytBrowser.SaveGameStateAsync("strands", puzzle.Id, puzzle.PrintDate,
                BuildStrandsGameData(puzzle, found, otherWords, allWords.Count)));

    AnsiConsole.Clear();
}

// Builds Strands game_data matching NYT's shape: history of THEME/SPANGRAM finds,
// preserved non-theme words, and the solved flag.
static string BuildStrandsGameData(StrandsPuzzle puzzle, HashSet<string> found, List<string> otherWords, int total)
{
    var history = found
        .Select(w => new
        {
            t = string.Equals(w, puzzle.Spangram, StringComparison.OrdinalIgnoreCase) ? "SPANGRAM" : "THEME",
            w
        })
        .ToArray();
    return JsonSerializer.Serialize(new
    {
        history,
        isPlayingArchive = false,
        isSolved = found.Count >= total,
        otherWordsFound = otherWords.ToArray()
    });
}

static async Task<StrandsPuzzle?> FetchStrandsAsync()
{
    var cachePath = Paths.Data("strands-cache.json");
    var date = DateTime.Now.ToString("yyyy-MM-dd");
    try
    {
        var json = await Web.GetStringAsync($"https://www.nytimes.com/svc/strands/v2/{date}.json");
        var p = ParseStrands(json);
        if (p != null) { try { await File.WriteAllTextAsync(cachePath, json); } catch (Exception ex) { AppLog.Debug("io", ex); } return p; }
    }
    catch (Exception ex) { AppLog.Debug("io", ex); }
    if (File.Exists(cachePath))
    {
        try { return ParseStrands(await File.ReadAllTextAsync(cachePath)); } catch (Exception ex) { AppLog.Debug("io", ex); }
    }
    return null;
}

static StrandsPuzzle? ParseStrands(string json)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetInt32().ToString() : "";
        var printDate = root.TryGetProperty("printDate", out var pd) ? pd.GetString() ?? "" : "";
        var clue = root.GetProperty("clue").GetString() ?? "";
        var spangram = (root.GetProperty("spangram").GetString() ?? "").ToUpperInvariant();
        var themeWords = root.GetProperty("themeWords").EnumerateArray()
            .Select(e => (e.GetString() ?? "").ToUpperInvariant()).ToList();
        var board = root.GetProperty("startingBoard").EnumerateArray().Select(e => e.GetString() ?? "").ToArray();

        var coords = new Dictionary<string, List<(int, int)>>(StringComparer.OrdinalIgnoreCase);
        void AddCoords(string word, JsonElement arr)
        {
            var pts = arr.EnumerateArray()
                .Select(p => (p[0].GetInt32(), p[1].GetInt32())).ToList();
            coords[word] = pts;
        }
        if (root.TryGetProperty("themeCoords", out var tc))
            foreach (var prop in tc.EnumerateObject())
                AddCoords(prop.Name.ToUpperInvariant(), prop.Value);
        if (root.TryGetProperty("spangramCoords", out var sc))
            AddCoords(spangram, sc);

        return new StrandsPuzzle(id, printDate, clue, spangram, themeWords, board, coords);
    }
    catch (Exception ex) { AppLog.Debug("returned null", ex); return null; }
}

static HashSet<string> LoadStrandsProgress(string date)
{
    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    try
    {
        var path = Paths.Data("strands-progress.json");
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("date", out var d) && d.GetString() == date)
                foreach (var w in doc.RootElement.GetProperty("found").EnumerateArray())
                    if (w.GetString() is { } s) set.Add(s);
        }
    }
    catch (Exception ex) { AppLog.Debug("io", ex); }
    return set;
}

static void SaveStrandsProgress(string date, HashSet<string> found)
{
    try
    {
        var path = Paths.Data("strands-progress.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { date, found = found.ToArray() }));
    }
    catch (Exception ex) { AppLog.Debug("io", ex); }
}

// Terminal NYT crossword (The Mini / The Midi). Fetches the day's puzzle through
// the signed-in nyt-profile (the puzzle data needs auth), caches it, and saves your
// letters locally. Arrow keys move, letters fill, Tab flips across/down, Esc = menu.
// Crossword progress isn't in NYT's synced game store, so this is local-only.
static async Task PlayCrosswordAsync(string publishType, string title)
{
    if (!NytBrowser.IsConnected)
    {
        AnsiConsole.MarkupLine(
            $"[yellow]{Markup.Escape(title)} needs your NYT account.[/] [grey]Connect it first via " +
            "News & newsletters > Connect NYT account (the crossword data requires a signed-in NYT session).[/]\n");
        PauseForKey();
        return;
    }

    var puzzle = await AnsiConsole.Status().StartAsync($"Fetching {title}...",
        async _ => await FetchCrosswordAsync(publishType, title));
    if (puzzle == null)
    {
        AnsiConsole.MarkupLine($"[red]Could not load {Markup.Escape(title)} (and no cached copy is available).[/]\n");
        PauseForKey();
        return;
    }

    // Player letters, resumed from local save.
    var entry = LoadCrosswordProgress(puzzle.Id, puzzle.Cells.Length);

    // Start on the first white cell; default direction Across.
    var cur = Array.FindIndex(puzzle.Cells, c => !c.IsBlack);
    var across = true;

    while (true)
    {
        AnsiConsole.Clear();
        RenderCrossword(puzzle, entry, cur, across);

        var done = Enumerable.Range(0, puzzle.Cells.Length)
            .Where(i => !puzzle.Cells[i].IsBlack)
            .All(i => string.Equals(entry[i], puzzle.Cells[i].Answer, StringComparison.OrdinalIgnoreCase) && entry[i].Length > 0);
        if (done)
        {
            AnsiConsole.MarkupLine("\n[bold green]Solved![/] [grey]Press any key to return.[/]");
            Console.ReadKey(intercept: true);
            SaveCrosswordProgress(puzzle.Id, entry);
            break;
        }

        var key = Console.ReadKey(intercept: true);

        if (key.Key == ConsoleKey.Escape)
        {
            SaveCrosswordProgress(puzzle.Id, entry);
            var action = PromptMenu("[green]Crossword menu[/]",
                ["Resume", "Check the puzzle", "Reveal the puzzle", "Clear the puzzle", "Save & quit"], 10, backAction: "resume");
            if (action <= 0) continue;                       // Resume / cancelled
            if (action == 1) { CrosswordCheck(puzzle, entry, cur); continue; }
            if (action == 2) { for (var i = 0; i < entry.Length; i++) if (!puzzle.Cells[i].IsBlack) entry[i] = puzzle.Cells[i].Answer; SaveCrosswordProgress(puzzle.Id, entry); continue; }
            if (action == 3) { for (var i = 0; i < entry.Length; i++) entry[i] = ""; SaveCrosswordProgress(puzzle.Id, entry); continue; }
            if (action == 4) { SaveCrosswordProgress(puzzle.Id, entry); break; }
        }
        else if (key.Key is ConsoleKey.Tab or ConsoleKey.Spacebar or ConsoleKey.Enter)
        {
            across = !across;
        }
        else if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.LeftArrow or ConsoleKey.RightArrow)
        {
            cur = CrosswordMove(puzzle, cur, key.Key);
        }
        else if (key.Key == ConsoleKey.Backspace)
        {
            if (entry[cur].Length > 0) entry[cur] = "";
            else { var prev = CrosswordStep(puzzle, cur, across, -1); if (prev >= 0) { cur = prev; entry[cur] = ""; } }
            SaveCrosswordProgress(puzzle.Id, entry);
        }
        else if (char.IsLetter(key.KeyChar))
        {
            entry[cur] = char.ToUpperInvariant(key.KeyChar).ToString();
            var next = CrosswordStep(puzzle, cur, across, +1);
            if (next >= 0) cur = next;
            SaveCrosswordProgress(puzzle.Id, entry);
        }
    }

    AnsiConsole.Clear();
}

// Renders the grid (cursor cell highlighted, current word tinted) plus the active clue.
static void RenderCrossword(Crossword p, string[] entry, int cur, bool across)
{
    AnsiConsole.MarkupLine($"[bold blue]{Markup.Escape(p.Title)}[/] [dim]{Markup.Escape(p.PrintDate)}[/]\n");

    var wordCells = CrosswordWordCells(p, cur, across);
    var wordSet = new HashSet<int>(wordCells);

    var sb = new StringBuilder();
    for (var row = 0; row < p.Height; row++)
    {
        sb.Append("  ");
        for (var col = 0; col < p.Width; col++)
        {
            var i = row * p.Width + col;
            var cell = p.Cells[i];
            if (cell.IsBlack)
            {
                sb.Append("[grey15 on grey15]███[/]");
            }
            else
            {
                var ch = entry[i].Length > 0 ? entry[i] : " ";
                var body = $" {ch} ";
                if (i == cur) sb.Append($"[black on green]{body}[/]");
                else if (wordSet.Contains(i)) sb.Append($"[black on yellow]{body}[/]");
                else sb.Append($"[white on grey35]{body}[/]");
            }
            sb.Append(' ');
        }
        sb.Append('\n');
    }
    AnsiConsole.Markup(sb.ToString());

    // Active clue.
    var clueIdx = across ? p.Cells[cur].AcrossClue : p.Cells[cur].DownClue;
    if (clueIdx < 0) clueIdx = across ? p.Cells[cur].DownClue : p.Cells[cur].AcrossClue;
    if (clueIdx >= 0)
    {
        var clue = p.Clues[clueIdx];
        AnsiConsole.MarkupLine($"\n[bold]{clue.Label} {clue.Direction}:[/] {Markup.Escape(clue.Text)}");
    }
    AnsiConsole.MarkupLine($"[dim]Dir: [bold]{(across ? "Across" : "Down")}[/]  •  arrows move, letters fill, Tab flips, Esc menu[/]");
}

// The cell indices of the word through `cur` in the given direction.
static List<int> CrosswordWordCells(Crossword p, int cur, bool across)
{
    var clueIdx = across ? p.Cells[cur].AcrossClue : p.Cells[cur].DownClue;
    if (clueIdx < 0) return [cur];
    return p.Clues[clueIdx].Cells.ToList();
}

// Steps one cell forward/back within the current word; -1 if none.
static int CrosswordStep(Crossword p, int cur, bool across, int delta)
{
    var cells = CrosswordWordCells(p, cur, across);
    var pos = cells.IndexOf(cur);
    var next = pos + delta;
    return next >= 0 && next < cells.Count ? cells[next] : -1;
}

// Moves the cursor by an arrow key, skipping black cells and staying in bounds.
static int CrosswordMove(Crossword p, int cur, ConsoleKey key)
{
    var row = cur / p.Width;
    var col = cur % p.Width;
    var (dr, dc) = key switch
    {
        ConsoleKey.UpArrow => (-1, 0),
        ConsoleKey.DownArrow => (1, 0),
        ConsoleKey.LeftArrow => (0, -1),
        _ => (0, 1),
    };
    for (var step = 0; step < Math.Max(p.Width, p.Height); step++)
    {
        row += dr; col += dc;
        if (row < 0 || row >= p.Height || col < 0 || col >= p.Width) return cur;
        var i = row * p.Width + col;
        if (!p.Cells[i].IsBlack) return i;
    }
    return cur;
}

// Marks incorrect/blank letters in the current word; briefly shows the result.
static void CrosswordCheck(Crossword p, string[] entry, int cur)
{
    var wrong = Enumerable.Range(0, p.Cells.Length)
        .Count(i => !p.Cells[i].IsBlack && entry[i].Length > 0 &&
                    !string.Equals(entry[i], p.Cells[i].Answer, StringComparison.OrdinalIgnoreCase));
    var blank = Enumerable.Range(0, p.Cells.Length)
        .Count(i => !p.Cells[i].IsBlack && entry[i].Length == 0);
    AnsiConsole.MarkupLine($"\n[grey]Check:[/] [red]{wrong} wrong[/], {blank} blank. [grey]Press any key.[/]");
    Console.ReadKey(intercept: true);
}

// Fetches the latest puzzle for a type, cached to disk for offline replay. The Midi
// has no id in the v3 list API — it's fetched directly by the slug "midi"; the Mini
// and Daily are looked up by id from the public listing.
static async Task<Crossword?> FetchCrosswordAsync(string publishType, string title)
{
    var cachePath = Paths.Data($"crossword-{publishType}-cache.json");
    var bySlug = publishType is "midi";

    try
    {
        string id, printDate;
        string? puzzleJson;

        if (bySlug)
        {
            puzzleJson = await NytBrowser.FetchJsonAsync(
                $"https://www.nytimes.com/svc/crosswords/v6/puzzle/{publishType}.json");
            (id, printDate) = ("", "");
            if (puzzleJson != null)
            {
                using var d = JsonDocument.Parse(puzzleJson);
                if (d.RootElement.TryGetProperty("id", out var idEl))
                    id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64().ToString() : idEl.GetString() ?? "";
                if (d.RootElement.TryGetProperty("publicationDate", out var pdd)) printDate = pdd.GetString() ?? "";
                else if (d.RootElement.TryGetProperty("printDate", out var pdd2)) printDate = pdd2.GetString() ?? "";
            }
            if (id.Length == 0) id = publishType; // stable local-save key fallback
        }
        else
        {
            var listJson = await Web.GetStringAsync(
                $"https://www.nytimes.com/svc/crosswords/v3/puzzles.json?publish_type={publishType}&sort_order=desc&sort_by=print_date&limit=1");
            using var listDoc = JsonDocument.Parse(listJson);
            var first = listDoc.RootElement.GetProperty("results")[0];
            id = first.GetProperty("puzzle_id").GetInt64().ToString();
            printDate = first.TryGetProperty("print_date", out var pd) ? pd.GetString() ?? "" : "";
            puzzleJson = await NytBrowser.FetchJsonAsync($"https://www.nytimes.com/svc/crosswords/v6/puzzle/{id}.json");
        }

        if (puzzleJson != null)
        {
            var parsed = ParseCrossword(puzzleJson, id, printDate, title);
            if (parsed != null)
            {
                try { await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(new { id, printDate, title, json = puzzleJson })); } catch (Exception ex) { AppLog.Debug("io", ex); }
                return parsed;
            }
        }
    }
    catch (Exception ex) { AppLog.Debug("crossword fetch", ex); /* fall through to cache */ }

    if (File.Exists(cachePath))
    {
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(cachePath));
            var root = doc.RootElement;
            return ParseCrossword(root.GetProperty("json").GetString() ?? "",
                root.GetProperty("id").GetString() ?? "", root.GetProperty("printDate").GetString() ?? "", title);
        }
        catch (Exception ex) { AppLog.Debug("io", ex); }
    }
    return null;
}

static Crossword? ParseCrossword(string json, string id, string printDate, string title)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        var body = doc.RootElement.GetProperty("body")[0];
        var dims = body.GetProperty("dimensions");
        var width = dims.GetProperty("width").GetInt32();
        var height = dims.GetProperty("height").GetInt32();

        var cellsJson = body.GetProperty("cells");
        var cells = new CrossCell[cellsJson.GetArrayLength()];
        for (var i = 0; i < cells.Length; i++)
        {
            var cj = cellsJson[i];
            var cell = new CrossCell();
            if (cj.ValueKind != JsonValueKind.Object || !cj.TryGetProperty("answer", out var ans))
            {
                cell.IsBlack = true;
            }
            else
            {
                cell.Answer = (ans.GetString() ?? "").ToUpperInvariant();
                if (cj.TryGetProperty("label", out var lbl)) cell.Label = lbl.GetString() ?? "";
            }
            cells[i] = cell;
        }

        var cluesJson = body.GetProperty("clues");
        var clues = new List<CrossClue>();
        for (var ci = 0; ci < cluesJson.GetArrayLength(); ci++)
        {
            var qj = cluesJson[ci];
            var dir = qj.GetProperty("direction").GetString() ?? "";
            var label = qj.GetProperty("label").GetString() ?? "";
            var text = qj.GetProperty("text")[0].TryGetProperty("plain", out var pl) ? pl.GetString() ?? "" : "";
            var cellIdx = qj.GetProperty("cells").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            clues.Add(new CrossClue(dir, label, text, cellIdx));
            foreach (var idx in cellIdx)
            {
                if (dir.StartsWith("A", StringComparison.OrdinalIgnoreCase)) cells[idx].AcrossClue = ci;
                else cells[idx].DownClue = ci;
            }
        }

        return new Crossword(id, printDate, title, width, height, cells, clues);
    }
    catch (Exception ex) { AppLog.Debug("returned null", ex); return null; }
}

static string[] LoadCrosswordProgress(string id, int cellCount)
{
    var entry = new string[cellCount];
    for (var i = 0; i < cellCount; i++) entry[i] = "";
    try
    {
        var path = Paths.Data("crossword-progress.json");
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty(id, out var arr) && arr.GetArrayLength() == cellCount)
                for (var i = 0; i < cellCount; i++) entry[i] = arr[i].GetString() ?? "";
        }
    }
    catch (Exception ex) { AppLog.Debug("io", ex); }
    return entry;
}

static void SaveCrosswordProgress(string id, string[] entry)
{
    try
    {
        var path = Paths.Data("crossword-progress.json");
        var all = new Dictionary<string, string[]>();
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var prop in doc.RootElement.EnumerateObject())
                all[prop.Name] = prop.Value.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
        }
        all[id] = entry;
        File.WriteAllText(path, JsonSerializer.Serialize(all));
    }
    catch (Exception ex) { AppLog.Debug("io", ex); }
}

// Terminal NYT Wordle. Fetches the day's solution (a /svc/ endpoint, no auth),
// caches it, persists guesses locally, and syncs with your NYT account when a
// profile is connected. Guesses are validated as any 5 letters (NYT's exact
// allowed-word list isn't cleanly fetchable).
static async Task PlayWordleAsync()
{
    var puzzle = await AnsiConsole.Status().StartAsync("Fetching today's Wordle...",
        async _ => await FetchWordleAsync());
    if (puzzle == null)
    {
        AnsiConsole.MarkupLine("[red]Could not load the Wordle puzzle (and no cached copy is available).[/]\n");
        PauseForKey();
        return;
    }

    // NYT's official allowed-guess list (cached after first fetch). Null → lenient.
    var validWords = await AnsiConsole.Status().StartAsync("Loading word list...",
        async _ => await LoadWordleWordListAsync());

    var solution = puzzle.Solution.ToLowerInvariant();

    // Resume local progress; then adopt NYT's state if it's further along.
    var (guesses, status) = LoadWordleProgress(puzzle.PrintDate);
    var syncNote = "";
    if (NytBrowser.IsConnected && puzzle.Id.Length > 0)
    {
        var remote = await AnsiConsole.Status().StartAsync("Syncing with your NYT account...",
            async _ => await NytBrowser.GetWordleStateAsync(puzzle.Id));
        if (remote != null)
        {
            if (remote.Value.Guesses.Count > guesses.Count)
            {
                guesses = remote.Value.Guesses;
                status = remote.Value.Status;
            }
            syncNote = "   [dim](NYT sync on)[/]";
        }
        else
        {
            syncNote = "   [dim](NYT sync will retry on exit)[/]";
        }
    }

    // Recompute terminal status from the guesses (authoritative).
    if (guesses.Contains(solution)) status = "WIN";
    else if (guesses.Count >= 6) status = "FAIL";
    else status = "IN_PROGRESS";

    var keyState = new Dictionary<char, string>(); // letter -> g/y/- (best seen)
    foreach (var g in guesses) UpdateWordleKeyState(keyState, g, solution);

    var message = status == "IN_PROGRESS"
        ? "[grey]Type a 5-letter guess and press Enter. \":q\" to quit.[/]"
        : "";

    while (true)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine($"[bold blue]Wordle[/] [dim]{Markup.Escape(puzzle.PrintDate)}[/]{syncNote}\n");

        // Board: 6 rows of 5 tiles.
        for (var row = 0; row < 6; row++)
        {
            if (row < guesses.Count)
                AnsiConsole.MarkupLine("  " + RenderWordleRow(guesses[row], solution));
            else
                AnsiConsole.MarkupLine("  " + string.Concat(Enumerable.Repeat("[grey][[ ]][/]", 5)));
        }

        AnsiConsole.MarkupLine("\n" + RenderWordleKeyboard(keyState));

        if (status == "WIN")
            message = $"[bold green]Solved in {guesses.Count}/6![/]";
        else if (status == "FAIL")
            message = $"[bold red]Out of guesses — the word was {solution.ToUpperInvariant()}.[/]";

        AnsiConsole.MarkupLine($"\n{message}");

        if (status != "IN_PROGRESS")
        {
            AnsiConsole.Markup("[grey]Press Enter to return...[/]");
            Console.ReadLine();
            break;
        }

        AnsiConsole.Markup("[green]> [/]");
        var input = Console.ReadLine();
        if (input == null) break;
        var guess = input.Trim().ToLowerInvariant();

        if (guess is ":q" or ":quit" or "quit") break;
        if (guess.Length != 5 || !guess.All(char.IsLetter))
        {
            message = "[yellow]Guesses must be exactly 5 letters.[/]";
            continue;
        }
        if (validWords != null && !validWords.Contains(guess))
        {
            message = "[yellow]Not in word list.[/]";
            continue;
        }

        guesses.Add(guess);
        UpdateWordleKeyState(keyState, guess, solution);
        if (guess == solution) status = "WIN";
        else if (guesses.Count >= 6) status = "FAIL";
        SaveWordleProgress(puzzle.PrintDate, guesses, status);
        message = "";
    }

    SaveWordleProgress(puzzle.PrintDate, guesses, status);

    // Push to NYT (the save guards against regressing a further-along server game).
    if (NytBrowser.IsConnected && puzzle.Id.Length > 0 && guesses.Count > 0)
        await AnsiConsole.Status().StartAsync("Saving progress to your NYT account...",
            async _ => await NytBrowser.SaveWordleStateAsync(puzzle.Id, puzzle.PrintDate, guesses, status));

    AnsiConsole.Clear();
}

// Per-letter feedback with Wordle's duplicate-letter rules: greens first, then
// yellows limited by the remaining count of each letter in the solution.
static string[] WordleFeedback(string guess, string solution)
{
    var res = new string[5];
    var counts = new Dictionary<char, int>();
    foreach (var c in solution) counts[c] = counts.GetValueOrDefault(c) + 1;

    for (var i = 0; i < 5; i++)
        if (guess[i] == solution[i]) { res[i] = "g"; counts[guess[i]]--; }
    for (var i = 0; i < 5; i++)
    {
        if (res[i] != null) continue;
        if (counts.GetValueOrDefault(guess[i]) > 0) { res[i] = "y"; counts[guess[i]]--; }
        else res[i] = "-";
    }
    return res;
}

static string RenderWordleRow(string guess, string solution)
{
    var fb = WordleFeedback(guess, solution);
    var sb = new StringBuilder();
    for (var i = 0; i < 5; i++)
    {
        var ch = char.ToUpperInvariant(guess[i]);
        sb.Append(fb[i] switch
        {
            "g" => $"[black on green] {ch} [/]",
            "y" => $"[black on yellow] {ch} [/]",
            _ => $"[white on grey35] {ch} [/]",
        });
        sb.Append(' ');
    }
    return sb.ToString();
}

static void UpdateWordleKeyState(Dictionary<char, string> keyState, string guess, string solution)
{
    var fb = WordleFeedback(guess, solution);
    for (var i = 0; i < 5; i++)
    {
        var c = guess[i];
        var cur = keyState.GetValueOrDefault(c);
        // Upgrade only: green beats yellow beats grey beats unseen.
        var rank = (string s) => s switch { "g" => 3, "y" => 2, "-" => 1, _ => 0 };
        if (rank(fb[i]) > rank(cur ?? "")) keyState[c] = fb[i];
    }
}

static string RenderWordleKeyboard(Dictionary<char, string> keyState)
{
    var rows = new[] { "QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM" };
    var sb = new StringBuilder();
    foreach (var row in rows)
    {
        sb.Append("  ");
        foreach (var c in row)
        {
            var st = keyState.GetValueOrDefault(char.ToLowerInvariant(c));
            sb.Append(st switch
            {
                "g" => $"[black on green] {c} [/]",
                "y" => $"[black on yellow] {c} [/]",
                "-" => $"[white on grey23] {c} [/]",
                _ => $"[white] {c} [/]",
            });
            sb.Append(' ');
        }
        sb.Append('\n');
    }
    return sb.ToString().TrimEnd('\n');
}

// Fetches today's Wordle solution from the public /svc/ endpoint, cached for offline.
static async Task<WordlePuzzle?> FetchWordleAsync()
{
    var cachePath = Paths.Data("wordle-cache.json");

    // NYT's puzzle rolls over at US Eastern midnight — use ET to pick the date.
    var etNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
        TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));

    foreach (var date in new[] { etNow, etNow.AddDays(-1) }) // fall back a day near rollover
    {
        try
        {
            var json = await Web.GetStringAsync(
                $"https://www.nytimes.com/svc/wordle/v2/{date:yyyy-MM-dd}.json");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            try { await File.WriteAllTextAsync(cachePath, json); } catch (Exception ex) { AppLog.Debug("io", ex); }
            return new WordlePuzzle(
                (root.GetProperty("solution").GetString() ?? "").ToLowerInvariant(),
                root.GetProperty("print_date").GetString() ?? "",
                root.GetProperty("id").GetInt32().ToString());
        }
        catch { /* try previous day, then cache */ }
    }

    if (File.Exists(cachePath))
    {
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(cachePath));
            var root = doc.RootElement;
            return new WordlePuzzle(
                (root.GetProperty("solution").GetString() ?? "").ToLowerInvariant(),
                root.GetProperty("print_date").GetString() ?? "",
                root.GetProperty("id").GetInt32().ToString());
        }
        catch (Exception ex) { AppLog.Debug("io", ex); }
    }
    return null;
}

// NYT's official allowed-guess list, cached to wordle-words.txt. First call scrapes
// it from the game's JS bundle (via a headless browser) and caches it; later calls
// read the file. Returns null if unavailable (caller then validates leniently).
static async Task<HashSet<string>?> LoadWordleWordListAsync()
{
    var cache = Paths.Data("wordle-words.txt");
    if (File.Exists(cache))
    {
        var cached = new HashSet<string>(await File.ReadAllLinesAsync(cache), StringComparer.OrdinalIgnoreCase);
        if (cached.Count > 1000) return cached;
    }

    try
    {
        var words = await ScrapeWordleWordListAsync();
        if (words.Count > 1000)
        {
            try { await File.WriteAllLinesAsync(cache, words.OrderBy(w => w, StringComparer.Ordinal)); } catch (Exception ex) { AppLog.Debug("io", ex); }
            return words;
        }
    }
    catch (Exception ex) { AppLog.Debug("wordle word list", ex); /* offline/bundle change — lenient */ }
    return null;
}

// Loads the Wordle game page in a headless browser and extracts the big 5-letter
// word arrays (answers + allowed guesses) from the JS chunk that carries them.
static async Task<HashSet<string>> ScrapeWordleWordListAsync()
{
    var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var gate = new object();

    var profileDir = Path.Combine(AppContext.BaseDirectory, "wordle-wl-profile");
    var channel = FindBrowserExe().Contains("chrome.exe", StringComparison.OrdinalIgnoreCase) ? "chrome" : "msedge";

    using var pw = await Playwright.CreateAsync();
    await using var ctx = await pw.Chromium.LaunchPersistentContextAsync(profileDir,
        new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = true, Channel = channel,
            Args = ["--disable-blink-features=AutomationControlled"],
            IgnoreDefaultArgs = ["--enable-automation"],
        });
    var page = ctx.Pages.FirstOrDefault() ?? await ctx.NewPageAsync();

    page.Response += async (_, resp) =>
    {
        try
        {
            if (!resp.Url.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) return;
            var body = await resp.TextAsync();
            foreach (Match arr in Regex.Matches(body, "\\[(?:\"[a-z]{5}\",){200,}\"[a-z]{5}\"\\]"))
                foreach (Match w in Regex.Matches(arr.Value, "[a-z]{5}"))
                    lock (gate) words.Add(w.Value);
        }
        catch (Exception ex) { AppLog.Debug("io", ex); }
    };

    await page.GotoAsync("https://www.nytimes.com/games/wordle/index.html",
        new PageGotoOptions { Timeout = 40000, WaitUntil = WaitUntilState.DOMContentLoaded });
    await page.WaitForTimeoutAsync(4000); // let the word-list chunk load
    lock (gate) return new HashSet<string>(words, StringComparer.OrdinalIgnoreCase);
}

static (List<string> Guesses, string Status) LoadWordleProgress(string printDate)
{
    try
    {
        var path = Paths.Data("wordle-progress.json");
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("printDate", out var d) && d.GetString() == printDate)
            {
                var g = doc.RootElement.GetProperty("guesses").EnumerateArray()
                    .Select(e => e.GetString() ?? "").Where(s => s.Length == 5).ToList();
                var s = doc.RootElement.TryGetProperty("status", out var st) ? st.GetString() ?? "IN_PROGRESS" : "IN_PROGRESS";
                return (g, s);
            }
        }
    }
    catch (Exception ex) { AppLog.Debug("io", ex); }
    return (new List<string>(), "IN_PROGRESS");
}

static void SaveWordleProgress(string printDate, List<string> guesses, string status)
{
    try
    {
        var path = Paths.Data("wordle-progress.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { printDate, guesses, status }));
    }
    catch (Exception ex) { AppLog.Debug("io", ex); }
}

// Recognizes subreddit input: "r/stlouis", "/r/stlouis", or a reddit.com subreddit URL.
static string? TryParseSubreddit(string input)
{
    var match = Regex.Match(input.Trim(),
        @"^(?:https?://(?:www\.|old\.)?reddit\.com)?/?r/([A-Za-z0-9_]+)/?$",
        RegexOptions.IgnoreCase);
    return match.Success ? match.Groups[1].Value : null;
}

// Interactive subreddit browser: pick sort order, filter by minimum upvotes,
// and read posts (self text or the linked article) in the pager.
static async Task ShowSubredditAsync(string subreddit)
{
    var sortName = "Hot";
    var sortPath = "hot";
    string? topRange = null;
    var minUpvotes = 0;

    const string changeSort = "== Change sort order ==";
    const string changeFilter = "== Set minimum upvotes ==";
    const string back = "<= Back to News Menu";

    List<RedditPost>? posts = null;
    var lastIdx = 0;
    while (true)
    {
        if (posts == null)
        {
            try
            {
                posts = await AnsiConsole.Status()
                    .StartAsync($"Fetching r/{subreddit} ({sortName})...",
                        async _ => await FetchSubredditPostsAsync(subreddit, sortPath, topRange));
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Could not fetch r/{Markup.Escape(subreddit)}:[/] {Markup.Escape(ex.Message)}\n" +
                    "[grey]Check the subreddit name; private or banned subreddits cannot be read. " +
                    "Reddit also rate-limits — waiting a minute usually clears a 429.[/]\n");
                PauseForKey();
                return;
            }
        }

        // RSS-fallback posts carry no scores (-1); the upvote filter can't apply to them.
        var scoresKnown = posts.Count == 0 || posts[0].Score >= 0;
        var visible = scoresKnown ? posts.Where(p => p.Score >= minUpvotes).ToList() : posts;

        AnsiConsole.Clear();
        AnsiConsole.MarkupLine(
            $"[bold blue]r/{Markup.Escape(subreddit)}[/] [grey]— {sortName}" +
            (scoresKnown && minUpvotes > 0 ? $", {minUpvotes}+ upvotes ({visible.Count} of {posts.Count} shown)" : "") + "[/]");
        if (!scoresKnown)
            AnsiConsole.MarkupLine(
                "[grey]Reddit is blocking anonymous API access from this network, so upvote counts " +
                "(and the upvote filter) are unavailable. To enable them, create a free 'script' app at " +
                "reddit.com/prefs/apps and put the client id and secret in Settings > Reddit API (restart to apply).[/]");
        if (visible.Count == 0)
            AnsiConsole.MarkupLine("[yellow]No posts match the current filter.[/]");

        var options = visible.Select(post =>
        {
            var scorePart = post.Score >= 0 ? $"[green]▲{post.Score,5}[/]  " : "";
            var commentsPart = post.NumComments >= 0 ? $"  [grey]({post.NumComments} comments)[/]" : "";
            return $"{scorePart}{Markup.Escape(post.Title)}{commentsPart}";
        }).ToList();
        options.Add(changeSort);
        options.Add(changeFilter);
        options.Add(back);

        var idx = PromptMenu("Select a post to read:", options, 15, initialSelected: lastIdx);

        if (idx < 0 || options[idx] == back)
        {
            AnsiConsole.Clear();
            return;
        }
        lastIdx = idx;

        if (options[idx] == changeSort)
        {
            var sortChoices = new List<string>
            {
                "Hot", "New (chronological)", "Rising",
                "Top: today", "Top: this week", "Top: this month", "Top: this year", "Top: all time"
            };
            var currentSortIdx = (sortPath, topRange) switch
            {
                ("new", _) => 1,
                ("rising", _) => 2,
                ("top", "day") => 3,
                ("top", "week") => 4,
                ("top", "month") => 5,
                ("top", "year") => 6,
                ("top", "all") => 7,
                _ => 0,
            };
            var sortIdx = PromptMenu("Sort posts by:", sortChoices, 10, backAction: "keep current sort",
                initialSelected: currentSortIdx);
            if (sortIdx < 0) continue;
            (sortName, sortPath, topRange) = sortChoices[sortIdx] switch
            {
                "New (chronological)" => ("New", "new", (string?)null),
                "Rising" => ("Rising", "rising", null),
                "Top: today" => ("Top today", "top", "day"),
                "Top: this week" => ("Top this week", "top", "week"),
                "Top: this month" => ("Top this month", "top", "month"),
                "Top: this year" => ("Top this year", "top", "year"),
                "Top: all time" => ("Top all time", "top", "all"),
                _ => ("Hot", "hot", null),
            };
            posts = null; // refetch with the new sort
            lastIdx = 0;  // new ordering — old position is meaningless
            continue;
        }

        if (options[idx] == changeFilter)
        {
            minUpvotes = AnsiConsole.Prompt(
                new TextPrompt<int>("[green]Minimum upvotes (0 for no filter):[/]")
                    .DefaultValue(minUpvotes)
                    .Validate(n => n >= 0 ? ValidationResult.Success() : ValidationResult.Error("Must be 0 or more")));
            continue;
        }

        await ReadRedditPostAsync(subreddit, visible[idx]);
    }
}

// Downloads one page of subreddit posts. Preference order:
//   1) Reddit's OAuth API (full data incl. scores) if [reddit-oauth] is set up,
//   2) the anonymous JSON API (works on most home networks),
//   3) the RSS feed (never blocked, but has no upvote counts).
static async Task<List<RedditPost>> FetchSubredditPostsAsync(string subreddit, string sortPath, string? topRange)
{
    var query = "limit=50&raw_json=1" + (topRange != null ? $"&t={topRange}" : "");

    if (RedditApi.HasCredentials)
        return ParseRedditListing(await RedditApi.GetAsync($"/r/{subreddit}/{sortPath}?{query}"));

    try
    {
        var json = await Web.GetStringAsync($"https://www.reddit.com/r/{subreddit}/{sortPath}.json?{query}");
        if (!json.TrimStart().StartsWith('{'))
            throw new HttpRequestException("Reddit returned a block page instead of JSON.");
        return ParseRedditListing(json);
    }
    catch (HttpRequestException)
    {
        return await FetchSubredditViaRssAsync(subreddit, sortPath, topRange);
    }
}

// Parses a Reddit "listing" JSON document into posts. Skips pinned posts.
static List<RedditPost> ParseRedditListing(string json)
{
    using var doc = JsonDocument.Parse(json);

    var posts = new List<RedditPost>();
    foreach (var child in doc.RootElement.GetProperty("data").GetProperty("children").EnumerateArray())
    {
        var d = child.GetProperty("data");
        if (d.TryGetProperty("stickied", out var stickied) && stickied.GetBoolean()) continue;

        posts.Add(new RedditPost(
            d.GetProperty("title").GetString() ?? "(no title)",
            d.GetProperty("author").GetString() ?? "?",
            d.GetProperty("score").GetInt32(),
            d.GetProperty("num_comments").GetInt32(),
            "https://www.reddit.com" + d.GetProperty("permalink").GetString(),
            d.GetProperty("url").GetString() ?? "",
            d.TryGetProperty("selftext", out var selftext) ? selftext.GetString() ?? "" : "",
            d.TryGetProperty("is_self", out var isSelf) && isSelf.GetBoolean(),
            DateTimeOffset.FromUnixTimeSeconds((long)d.GetProperty("created_utc").GetDouble())));
    }
    return posts;
}

// Fallback when the JSON API is blocked: Reddit's RSS feeds are served to feed
// readers, but carry no upvote counts (Score/NumComments = -1 marks them unknown).
static async Task<List<RedditPost>> FetchSubredditViaRssAsync(string subreddit, string sortPath, string? topRange)
{
    var url = $"https://www.reddit.com/r/{subreddit}/{sortPath}/.rss?limit=50";
    if (topRange != null) url += $"&t={topRange}";

    await using var stream = await Web.GetStreamAsync(url);
    using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
    var feed = SyndicationFeed.Load(reader);

    var posts = new List<RedditPost>();
    foreach (var item in feed.Items)
    {
        var permalink = item.Links.FirstOrDefault()?.Uri?.ToString() ?? "";
        var contentHtml = (item.Content as TextSyndicationContent)?.Text ?? "";

        // Link posts carry the target as <a href="...">[link]</a> in the entry body.
        var linkMatch = Regex.Match(contentHtml, "<a href=\"([^\"]+)\">\\s*\\[link\\]", RegexOptions.IgnoreCase);
        var externalUrl = linkMatch.Success ? WebUtility.HtmlDecode(linkMatch.Groups[1].Value) : permalink;
        var isSelf = externalUrl == permalink;

        var selfText = "";
        if (isSelf && contentHtml.Length > 0)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(contentHtml);
            var mdNode = doc.DocumentNode.SelectSingleNode("//div[contains(@class,'md')]");
            selfText = mdNode != null ? HtmlFragmentToText(mdNode.InnerHtml) : "";
        }

        var author = item.Authors.FirstOrDefault()?.Name ?? "?"; // formatted "/u/name"
        author = author.TrimStart('/');
        if (author.StartsWith("u/", StringComparison.OrdinalIgnoreCase)) author = author[2..];

        posts.Add(new RedditPost(
            item.Title?.Text ?? "(no title)", author, -1, -1,
            permalink, externalUrl, selfText, isSelf, item.PublishDate));
    }
    return posts;
}

// Downloads a post's top comments, using the same tiers as the listing fetch:
// OAuth JSON, anonymous JSON, then the comments RSS feed (Reddit's "best" order,
// no scores) when the JSON API is blocked.
static async Task<List<RedditComment>> FetchTopCommentsAsync(RedditPost post, int limit = 10)
{
    var path = new Uri(post.Permalink).AbsolutePath.TrimEnd('/');
    var query = $"sort=top&limit={limit}&depth=1&raw_json=1";

    if (RedditApi.HasCredentials)
        return ParseRedditComments(await RedditApi.GetAsync($"{path}.json?{query}"), limit);

    try
    {
        var json = await Web.GetStringAsync($"https://www.reddit.com{path}.json?{query}");
        if (!json.TrimStart().StartsWith('['))
            throw new HttpRequestException("Reddit returned a block page instead of JSON.");
        return ParseRedditComments(json, limit);
    }
    catch (HttpRequestException)
    {
        return await FetchCommentsViaRssAsync(post, limit);
    }
}

// Parses the comments JSON: an array of [post listing, comments listing].
static List<RedditComment> ParseRedditComments(string json, int limit)
{
    using var doc = JsonDocument.Parse(json);

    var comments = new List<RedditComment>();
    foreach (var child in doc.RootElement[1].GetProperty("data").GetProperty("children").EnumerateArray())
    {
        if (comments.Count >= limit) break;
        if (child.GetProperty("kind").GetString() != "t1") continue; // skip "load more" stubs

        var d = child.GetProperty("data");
        if (d.TryGetProperty("stickied", out var stickied) && stickied.GetBoolean()) continue; // automod etc.

        comments.Add(new RedditComment(
            d.GetProperty("author").GetString() ?? "?",
            d.GetProperty("score").GetInt32(),
            d.GetProperty("body").GetString() ?? ""));
    }
    return comments;
}

// RSS fallback: the post's comment feed. The first entry is the post itself;
// the rest are comments in Reddit's default "best" order (scores unavailable).
static async Task<List<RedditComment>> FetchCommentsViaRssAsync(RedditPost post, int limit)
{
    var path = new Uri(post.Permalink).AbsolutePath.TrimEnd('/');
    var url = $"https://www.reddit.com{path}/.rss?limit={limit + 1}";

    await using var stream = await Web.GetStreamAsync(url);
    using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
    var feed = SyndicationFeed.Load(reader);

    var comments = new List<RedditComment>();
    foreach (var item in feed.Items.Skip(1)) // first entry is the post itself
    {
        if (comments.Count >= limit) break;

        var author = item.Authors.FirstOrDefault()?.Name ?? "?"; // formatted "/u/name"
        author = author.TrimStart('/');
        if (author.StartsWith("u/", StringComparison.OrdinalIgnoreCase)) author = author[2..];

        var contentHtml = (item.Content as TextSyndicationContent)?.Text ?? "";
        var doc = new HtmlDocument();
        doc.LoadHtml(contentHtml);
        var mdNode = doc.DocumentNode.SelectSingleNode("//div[contains(@class,'md')]");
        var body = mdNode != null ? HtmlFragmentToText(mdNode.InnerHtml) : StripHtml(contentHtml);
        if (body.Length == 0) continue;

        comments.Add(new RedditComment(author, -1, body));
    }
    return comments;
}

// Shows a single reddit post: self posts render their own text, link posts fetch
// the linked article, and image/video posts just show the link.
static async Task ReadRedditPostAsync(string subreddit, RedditPost post)
{
    string body;
    if (post.IsSelf)
    {
        body = post.SelfText.Length > 0
            ? Markup.Escape(post.SelfText)
            : "[grey](no text — discussion is in the comments)[/]";
    }
    else if (Regex.IsMatch(post.Url, @"\.(jpe?g|png|gif|webp|mp4)(\?|$)", RegexOptions.IgnoreCase) ||
             post.Url.Contains("v.redd.it") || post.Url.Contains("i.redd.it") ||
             post.Url.Contains("reddit.com/gallery"))
    {
        body = "[grey](image/video post — open the link above in a browser)[/]";
    }
    else
    {
        body = await AnsiConsole.Status()
            .StartAsync("Fetching linked article...", async _ => await ScrapeArticleTextAsync(post.Url));
        if (body.Length == 0)
            body = "[yellow]Could not extract readable text from the linked page.[/]";
    }

    var commentsBlock = "";
    if (post.NumComments != 0)
    {
        try
        {
            var comments = await AnsiConsole.Status()
                .StartAsync("Fetching top comments...", async _ => await FetchTopCommentsAsync(post));
            if (comments.Count > 0)
            {
                var sb = new StringBuilder($"\n\n[bold]═══ Top comments ═══[/]\n\n");
                foreach (var comment in comments)
                {
                    var scorePart = comment.Score >= 0 ? $"  [green]▲ {comment.Score}[/]" : "";
                    sb.Append($"[bold yellow]u/{Markup.Escape(comment.Author)}[/]{scorePart}\n");
                    sb.Append($"{Markup.Escape(comment.Body)}\n\n");
                }
                commentsBlock = "\n" + sb.ToString().TrimEnd();
            }
        }
        catch
        {
            // Comments are best-effort; still show the post if they can't be fetched.
        }
    }

    var linkLine = post.IsSelf || post.Url == post.Permalink
        ? ""
        : $"[link]{Markup.Escape(post.Url)}[/]\n";

    var stats = post.Score >= 0 ? $" • ▲ {post.Score} • {post.NumComments} comments" : "";
    var panel = new Panel(new Markup(
        $"[bold]{Markup.Escape(post.Title)}[/]\n\n" +
        $"[dim]r/{Markup.Escape(subreddit)} • u/{Markup.Escape(post.Author)}{stats} • {post.Created.ToLocalTime():g}[/]\n" +
        linkLine +
        $"[link]{Markup.Escape(post.Permalink)}[/]\n\n" +
        body +
        commentsBlock))
    {
        Border = BoxBorder.Rounded,
        Padding = new Padding(1, 1, 1, 1),
        Expand = true
    };

    var primaryLinks = post.IsSelf || post.Url.Length == 0
        ? new List<(string, string)> { ("Reddit thread", post.Permalink) }
        : [("Linked page", post.Url), ("Reddit thread", post.Permalink)];
    ShowInPager(panel, BuildLinks(primaryLinks, post.SelfText, body, commentsBlock));
}

static async Task ReadArticleAsync(SyndicationItem article)
{
    AnsiConsole.Clear();
    var url = article.Links.FirstOrDefault()?.Uri?.ToString();

    if (string.IsNullOrEmpty(url))
    {
        AnsiConsole.MarkupLine("[red]No URL found for this article.[/]");
        AnsiConsole.Prompt(new TextPrompt<string>("[grey]Press Enter to return...[/]").AllowEmpty());
        return;
    }

    // Spectre.Console spinner while downloading and parsing the webpage
    var articleText = await AnsiConsole.Status()
        .StartAsync("Fetching full article...", async ctx =>
        {
            return await ScrapeArticleTextAsync(url);
        });

    // NYT's DataDome bot-block can't be beaten directly. Morning briefing editions
    // arrive as email, so those fall back to Gmail; for everything else the summary
    // plus "open in browser" (where the subscription works) is the best available.
    if (articleText == BlockedMessage())
    {
        if (url.Contains("/briefing/", StringComparison.OrdinalIgnoreCase))
        {
            var emailText = await AnsiConsole.Status()
                .StartAsync("NYT blocked the fetch — reading this edition from your Gmail instead...",
                    async _ => await TryFetchNewsletterTextFromGmailAsync(
                        "nytdirect@nytimes.com", "The Morning", article.PublishDate));
            articleText = emailText != null
                ? "[grey](NYT blocks direct article fetches — showing the full text " +
                  "from The Morning email in your Gmail.)[/]\n\n" + emailText
                : articleText + "\n[grey]The Gmail lookup for this edition found nothing — check that " +
                  "the edition's email is in the connected Gmail account, or use the " +
                  "'NYT: The Morning (from your Gmail)' source.[/]";
        }
        else
        {
            articleText += "\n[grey]The feed summary above is everything NYT will serve to a " +
                           "terminal app. Press O to open the full article in your logged-in browser.[/]";
        }
    }

    // If the site blocked the full-text fetch, at least show the feed's own summary.
    var summaryBlock = "";
    var summary = StripHtml(article.Summary?.Text);
    if (summary.Length > 0 && !articleText.Contains(summary, StringComparison.Ordinal))
    {
        summaryBlock = $"[italic]{summary.Replace("[", "[[").Replace("]", "]]")}[/]\n\n";
    }

    var panel = new Panel(new Markup($"[bold]{article.Title.Text}[/]\n\n" +
                                     $"[dim]Published: {article.PublishDate.DateTime.ToLocalTime():g}[/]\n" +
                                     $"[link]{url}[/]\n\n" +
                                     $"{summaryBlock}" +
                                     $"{articleText}"))
    {
        Border = BoxBorder.Rounded,
        Padding = new Padding(1, 1, 1, 1),
        Expand = true
    };

    ShowInPager(panel, BuildLinks([("Article page", url)], summaryBlock, articleText));
}

static async Task<string> ScrapeArticleTextAsync(string url)
{
    var (_, text) = await ScrapePageAsync(url);
    return text;
}

// Downloads a page and extracts (title, readable paragraph text).
static async Task<(string Title, string Text)> ScrapePageAsync(string url)
{
    // NYT article pages are DataDome-blocked over plain HTTP. If the user has
    // connected their NYT account (a warmed browser profile exists), read through
    // that signed-in browser session instead — DataDome accepts it.
    if (NytBrowser.IsConnected && NytBrowser.IsNytUrl(url))
    {
        var viaBrowser = await NytBrowser.TryReadAsync(url);
        if (viaBrowser != null) return viaBrowser.Value;
        // else fall through to the HTTP path, which yields the blocked message.
    }

    try
    {
        var html = await Web.GetStringAsync(url);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var title = WebUtility.HtmlDecode(
            doc.DocumentNode.SelectSingleNode("//title")?.InnerText.Trim() ?? url);

        var text = ExtractReadableText(doc);
        if (text.Length > 0) return (title, text);
        return (title, BlockedPageHint(html) ?? "[yellow]Could not extract readable text from this page.[/]");
    }
    catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
    {
        return (url, BlockedMessage());
    }
    catch (Exception ex)
    {
        return (url, $"[red]Failed to extract text:[/] {ex.Message}");
    }
}

// Recognizes NYT's bot-check / paywall shell pages and explains what to do.
static string? BlockedPageHint(string html)
{
    if (html.Length < 5000 && html.Contains("captcha", StringComparison.OrdinalIgnoreCase))
    {
        return BlockedMessage();
    }
    return null;
}

static string BlockedMessage() =>
    "[yellow]NYT's bot detection blocked the full-article fetch. It blocks all non-browser " +
    "apps, and login cookies cannot fix it — the block is tied to the browser's TLS fingerprint.[/]";

// Pulls readable paragraphs out of a parsed HTML document, ready for Spectre markup.
static string ExtractReadableText(HtmlDocument doc)
{
    var paragraphs = doc.DocumentNode.SelectNodes("//p");
    if (paragraphs == null || paragraphs.Count == 0) return "";

    var sb = new StringBuilder();
    foreach (var p in paragraphs)
    {
        // Decode HTML entities (like &quot; or &#39;)
        var text = WebUtility.HtmlDecode(p.InnerText.Trim());

        // Heuristic: If it's less than 60 characters, it's likely a menu link, photo caption, or ad block.
        if (text.Length > 60)
        {
            // Escape brackets so Spectre.Console doesn't try to parse article text as color markup
            text = text.Replace("[", "[[").Replace("]", "]]");
            sb.AppendLine(text);
            sb.AppendLine();
        }
    }
    return sb.ToString();
}

// Selection menu that supports going back: returns the chosen index, or -1 when the
// user presses Esc, Backspace, or Q. Items may contain Spectre markup. Draws in
// place below the current cursor so headers/messages above it stay visible.
static int PromptMenu(string titleMarkup, IReadOnlyList<string> items, int pageSize = 15, string backAction = "go back", int initialSelected = 0)
{
    pageSize = Math.Clamp(Math.Min(pageSize, items.Count), 1, Math.Max(1, Console.WindowHeight - 5));
    var frameHeight = pageSize + 3; // title + items + more-indicator + key hints

    // Reserve the frame's lines up front so drawing near the screen bottom
    // doesn't scroll the buffer mid-redraw.
    Console.Write(new string('\n', frameHeight));
    var startTop = Math.Max(0, Console.CursorTop - frameHeight);

    var selected = Math.Clamp(initialSelected, 0, items.Count - 1);
    var top = 0;
    while (true)
    {
        if (selected < top) top = selected;
        if (selected >= top + pageSize) top = selected - pageSize + 1;

        var width = Math.Max(20, Console.WindowWidth);
        var row = startTop;
        void WriteLine(string markup)
        {
            Console.SetCursorPosition(0, row++);
            AnsiConsole.Markup(markup);
            Console.Write("\x1b[K"); // erase leftovers from the previous frame
        }

        WriteLine(titleMarkup);
        for (var i = top; i < top + pageSize; i++)
        {
            if (i >= items.Count)
            {
                WriteLine("");
                continue;
            }
            var label = items[i];
            // Keep every item to one terminal line so the frame height stays fixed;
            // over-long labels drop their markup and get truncated.
            var plain = Markup.Remove(label);
            if (plain.Length > width - 4)
                label = Markup.Escape(plain[..(width - 5)]) + "…";
            WriteLine(i == selected ? $"[blue]> {label}[/]" : $"  {label}");
        }

        var more = (top > 0 ? "▲ more above  " : "") + (top + pageSize < items.Count ? "▼ more below" : "");
        WriteLine(more.Length > 0 ? $"[grey]{more}[/]" : "");
        WriteLine($"[grey]Up/Down move • Enter/→ select • ←/Esc/Backspace/Q {backAction}[/]");

        var key = Console.ReadKey(intercept: true);
        switch (key.Key)
        {
            case ConsoleKey.UpArrow or ConsoleKey.K: selected = (selected - 1 + items.Count) % items.Count; break;
            case ConsoleKey.DownArrow or ConsoleKey.J: selected = (selected + 1) % items.Count; break;
            case ConsoleKey.PageUp: selected = Math.Max(0, selected - pageSize); break;
            case ConsoleKey.PageDown: selected = Math.Min(items.Count - 1, selected + pageSize); break;
            case ConsoleKey.Home: selected = 0; break;
            case ConsoleKey.End: selected = items.Count - 1; break;
            case ConsoleKey.Enter or ConsoleKey.RightArrow:
                Console.SetCursorPosition(0, Math.Min(startTop + frameHeight, Console.BufferHeight - 1));
                return selected;
            case ConsoleKey.Escape or ConsoleKey.Backspace or ConsoleKey.Q or ConsoleKey.LeftArrow:
                Console.SetCursorPosition(0, Math.Min(startTop + frameHeight, Console.BufferHeight - 1));
                return -1;
        }
    }
}

// Shows content in a full-screen pager starting at the top. Up/Down scroll one line,
// PgUp/PgDn/Space scroll a screen, Home/End jump, Enter/Esc/Q return.
// Returns the action key that was pressed (e.g. R to reply, A to archive), or
// null on a normal exit.
static ConsoleKey? ShowInPager(IRenderable content, List<(string Label, string Url)>? links = null,
    (ConsoleKey Key, string Hint)[]? actions = null, bool startAtEnd = false,
    bool tryReadLinksInTerminal = false, string? startAtText = null)
{
    // With tryReadLinksInTerminal, O first attempts to read the link as an
    // article right here in the terminal (news-reader extraction), falling
    // back to the browser for pages with no readable text.
    void Open(string url)
    {
        if (tryReadLinksInTerminal) TryReadLinkInTerminal(url);
        else OpenInBrowser(url);
    }

    var lines = RenderToLines(content);

    var offset = startAtEnd ? int.MaxValue : 0; // clamped to the real end below
    if (startAtText != null)
    {
        // Open scrolled to the first line containing the marker text (with a
        // couple of lines of context above it), e.g. an unread divider.
        var at = lines.FindIndex(l => Regex.Replace(l, "\x1b\\[[0-9;]*m", "").Contains(startAtText));
        if (at >= 0) offset = Math.Max(0, at - 2);
    }
    while (true)
    {
        var height = Math.Max(1, Console.WindowHeight - 1); // bottom row holds the key hints
        var maxOffset = Math.Max(0, lines.Count - height);
        offset = Math.Clamp(offset, 0, maxOffset);

        AnsiConsole.Clear();
        var sb = new StringBuilder();
        for (var i = offset; i < Math.Min(offset + height, lines.Count); i++)
            sb.Append(lines[i]).Append('\n');
        sb.Append("\x1b[0m"); // don't let article styling bleed into the hint bar
        Console.Out.Write(sb.ToString());

        var position = maxOffset == 0 ? "All"
            : offset == 0 ? "Top"
            : offset == maxOffset ? "End"
            : $"{(int)Math.Round(100.0 * offset / maxOffset)}%";
        var openHint = links is { Count: 1 } ? ", O open in browser"
            : links is { Count: > 1 } ? $", O open a link ({links.Count})"
            : "";
        var extraHint = actions is { Length: > 0 } ? ", " + string.Join(", ", actions.Select(a => a.Hint)) : "";
        AnsiConsole.Markup($"[grey]{position} — Up/Down scroll, PgUp/PgDn page{openHint}{extraHint}, ←/Esc/Backspace/Q back[/]");

        var key = Console.ReadKey(intercept: true);
        if (actions != null && actions.Any(a => a.Key == key.Key))
        {
            AnsiConsole.Clear();
            return key.Key;
        }
        switch (key.Key)
        {
            case ConsoleKey.UpArrow or ConsoleKey.K: offset--; break;
            case ConsoleKey.DownArrow or ConsoleKey.J: offset++; break;
            case ConsoleKey.PageUp: offset -= height; break;
            case ConsoleKey.PageDown or ConsoleKey.Spacebar: offset += height; break;
            case ConsoleKey.Home: offset = 0; break;
            case ConsoleKey.End: offset = maxOffset; break;
            case ConsoleKey.O when links is { Count: > 0 }:
                if (links.Count == 1)
                {
                    Open(links[0].Url);
                }
                else
                {
                    AnsiConsole.Clear();
                    var linkOptions = links.Select(l =>
                    {
                        var host = Uri.TryCreate(l.Url, UriKind.Absolute, out var u) ? u.Host : "";
                        return l.Label == l.Url
                            ? $"[blue]{Markup.Escape(l.Url)}[/]"
                            : $"{Markup.Escape(l.Label)}  [grey]({Markup.Escape(host)})[/]";
                    }).ToList();
                    var pick = PromptMenu(tryReadLinksInTerminal ? "Open which link?" : "Open which link in the browser?",
                        linkOptions, 15, backAction: "cancel");
                    if (pick >= 0) Open(links[pick].Url);
                }
                break;
            case ConsoleKey.Enter or ConsoleKey.Escape or ConsoleKey.Q or ConsoleKey.Backspace or ConsoleKey.LeftArrow:
                AnsiConsole.Clear();
                return null;
        }
    }
}

// Fetches a link and, if it has readable article text, shows it in a nested
// pager (the same extraction the news reader uses); otherwise opens the
// browser. From inside the article view, O opens the page in the browser.
static void TryReadLinkInTerminal(string url)
{
    var (title, text) = AnsiConsole.Status().Start("Fetching page...",
        _ => ScrapePageAsync(url).GetAwaiter().GetResult());

    // ScrapePageAsync signals "nothing readable" (blocked, script-only, video,
    // image, error) with a [yellow]/[red] markup message — browser handles those.
    if (text.Length == 0 || text.StartsWith("[yellow]") || text.StartsWith("[red]"))
    {
        OpenInBrowser(url);
        return;
    }

    var panel = new Panel(new Markup(
        $"[bold]{Markup.Escape(title)}[/]\n\n" +
        $"[link]{Markup.Escape(url)}[/]\n\n" +
        text))
    {
        Border = BoxBorder.Rounded,
        Padding = new Padding(1, 1, 1, 1),
        Expand = true
    };
    ShowInPager(panel, BuildLinks([("Article page", url)], text));
}

// Reads one line of free text with the console's native line input instead of
// Spectre's TextPrompt — the native editor lets Backspace erase across wrapped
// lines, which matters for replies longer than the terminal is wide.
static string PromptReplyLine(string markupLabel)
{
    AnsiConsole.Markup(markupLabel + " ");
    return (Console.ReadLine() ?? "").Trim();
}

// Renders a Spectre renderable to individual terminal lines (with ANSI styling)
// at the current window width, so the pager can show any slice of them.
static List<string> RenderToLines(IRenderable content)
{
    var writer = new StringWriter();
    var console = AnsiConsole.Create(new AnsiConsoleSettings
    {
        Ansi = AnsiConsole.Profile.Capabilities.Ansi ? AnsiSupport.Yes : AnsiSupport.No,
        ColorSystem = (ColorSystemSupport)AnsiConsole.Profile.Capabilities.ColorSystem,
        Interactive = InteractionSupport.No,
        Out = new AnsiConsoleOutput(writer)
    });
    console.Profile.Width = AnsiConsole.Profile.Width;
    console.Write(content);
    return writer.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
}

static void OpenInBrowser(string url)
{
    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
    catch { /* no default browser handler — nothing sensible to do */ }
}

// Builds the pager's link list: named primary links first, then any literal URLs
// found in the given texts (deduplicated, in order of appearance).
static List<(string Label, string Url)> BuildLinks(List<(string Label, string Url)> primary, params string?[] texts)
{
    var links = new List<(string Label, string Url)>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var (label, url) in primary)
        if (!string.IsNullOrEmpty(url) && seen.Add(url))
            links.Add((label, url));

    foreach (var text in texts)
    {
        if (string.IsNullOrEmpty(text)) continue;
        foreach (Match m in Regex.Matches(text, @"https?://[^\s\[\]""'<>()]+"))
        {
            var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            if (url.Length > 10 && seen.Add(url)) links.Add((url, url));
        }
    }
    return links;
}

// Pulls hyperlinks out of an HTML document, labeled by their anchor text — used for
// emails, where links are behind words instead of visible as URLs.
static List<(string Label, string Url)> ExtractHtmlLinks(HtmlDocument doc, int max = 40)
{
    var links = new List<(string Label, string Url)>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var a in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
    {
        var href = a.GetAttributeValue("href", "");
        if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;

        var label = WebUtility.HtmlDecode(a.InnerText.Trim());
        label = Regex.Replace(label, @"\s+", " ");
        if (label.Length == 0) continue; // image-only / invisible links are just noise
        if (label.Length > 70) label = label[..70] + "…";

        if (seen.Add(href))
        {
            links.Add((label, href));
            if (links.Count >= max) break;
        }
    }
    return links;
}

// Converts an HTML fragment to plain text while keeping paragraph breaks,
// line breaks, and list structure (InnerText alone flattens everything into one blob).
static string HtmlFragmentToText(string html)
{
    html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
    var doc = new HtmlDocument();
    doc.LoadHtml(html);

    var blocks = doc.DocumentNode.SelectNodes("//p|//li|//pre|//h1|//h2|//h3|//h4");
    if (blocks == null) return WebUtility.HtmlDecode(doc.DocumentNode.InnerText).Trim();

    var sb = new StringBuilder();
    foreach (var block in blocks)
    {
        var text = WebUtility.HtmlDecode(block.InnerText).Trim();
        if (text.Length == 0) continue;
        if (block.Name == "li") sb.Append("• ").Append(text).Append('\n');
        else sb.Append(text).Append("\n\n");
    }
    return sb.ToString().Trim();
}

// Strips HTML tags/entities from feed summary text.
static string StripHtml(string? html)
{
    if (string.IsNullOrWhiteSpace(html)) return "";
    var doc = new HtmlDocument();
    doc.LoadHtml(html);
    return WebUtility.HtmlDecode(doc.DocumentNode.InnerText).Trim();
}

// Reads an email newsletter directly from the user's Gmail over IMAP and renders
// the email HTML. For sources like NYT this is the only reliable full-text path,
// since their article pages are bot-blocked for non-browser clients.
static async Task ShowNewsletterFromGmailAsync(EmailNewsletter newsletter)
{
    var creds = LoadGmailCredentials();
    if (creds == null)
    {
        AnsiConsole.MarkupLine(
            "[yellow]Gmail is not set up yet.[/] Open [bold]Settings > Gmail accounts[/] from the main menu and add two lines:\n" +
            "[grey]  1) your Gmail address\n" +
            "  2) a Google app password — generate one at myaccount.google.com > Security > 2-Step Verification > App passwords\n" +
            "  (requires 2-Step Verification; the 16-character password works only for this app and can be revoked anytime)[/]\n");
        PauseForKey();
        return;
    }

    try
    {
        using var imap = new ImapClient();

        var summaries = await AnsiConsole.Status()
            .StartAsync("Connecting to Gmail and finding newsletter editions...", async _ =>
            {
                await imap.ConnectAsync("imap.gmail.com", 993, SecureSocketOptions.SslOnConnect);
                await imap.AuthenticateAsync(creds.Value.Email, creds.Value.AppPassword);

                // "All Mail" also covers editions that were archived out of the inbox.
                var folder = imap.GetFolder(SpecialFolder.All) ?? imap.Inbox;
                await folder.OpenAsync(FolderAccess.ReadOnly);

                SearchQuery query = SearchQuery.FromContains(newsletter.FromContains);
                if (!string.IsNullOrEmpty(newsletter.SubjectContains))
                    query = query.And(SearchQuery.SubjectContains(newsletter.SubjectContains));
                var uids = await folder.SearchAsync(query);

                var recent = uids.TakeLast(14).ToList();
                var fetched = await folder.FetchAsync(recent,
                    MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId);
                return (Folder: folder, Items: fetched.OrderByDescending(m => m.Date).ToList());
            });

        if (summaries.Items.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]No matching emails found for '{Markup.Escape(newsletter.Label)}' " +
                $"(from contains '{Markup.Escape(newsletter.FromContains)}'" +
                (newsletter.SubjectContains != null ? $", subject contains '{Markup.Escape(newsletter.SubjectContains)}'" : "") +
                ").[/]\n[grey]Check the actual sender address in one of the emails, then adjust the " +
                "filters in Settings > Email newsletters (format: Label | from | subject).[/]\n");
            PauseForKey();
            return;
        }

        var lastIdx = 0;
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine($"[bold blue]{Markup.Escape(newsletter.Label)}[/]");

            var options = summaries.Items
                .Select(m => Markup.Escape($"{m.Date.ToLocalTime():ddd MM/dd}  {m.Envelope?.Subject ?? "(no subject)"}"))
                .ToList();
            options.Add("<= Back to News Menu");

            var idx = PromptMenu("Select an edition to read:", options, 15, initialSelected: lastIdx);
            if (idx < 0 || idx == options.Count - 1) break;

            lastIdx = idx;
            var summary = summaries.Items[idx];
            var body = await AnsiConsole.Status()
                .StartAsync("Downloading edition...", async _ =>
                    await summaries.Folder.GetMessageAsync(summary.UniqueId));

            var doc = new HtmlDocument();
            doc.LoadHtml(body.HtmlBody ?? body.TextBody ?? "");
            var text = ExtractReadableText(doc);
            if (text.Length == 0)
                text = "[yellow]Could not extract readable text from this email.[/]";

            var panel = new Panel(new Markup(
                $"[bold]{Markup.Escape(summary.Envelope?.Subject ?? newsletter.Label)}[/]\n" +
                $"[dim]{summary.Date.ToLocalTime():f}[/]\n\n" +
                text))
            {
                Border = BoxBorder.Rounded,
                Padding = new Padding(1, 1, 1, 1),
                Expand = true
            };
            ShowInPager(panel, BuildLinks(ExtractHtmlLinks(doc), text));
        }

        await imap.DisconnectAsync(true);
        AnsiConsole.Clear();
    }
    catch (AuthenticationException)
    {
        AnsiConsole.MarkupLine(
            "[red]Gmail sign-in failed.[/] [grey]Check Settings > Gmail accounts: line 1 must be your full Gmail address, " +
            "line 2 a 16-character app password (not your normal password). App passwords require " +
            "2-Step Verification to be enabled on the account.[/]\n");
        PauseForKey();
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Gmail error:[/] {Markup.Escape(ex.Message)}\n");
        PauseForKey();
    }
}

// Shows the coming days of calendar events (the agenda-days display setting,
// default 14) from private ICS feeds listed in
// the [calendar] config section (Google Calendar's "Secret address in iCal format" — no OAuth
// needed). One feed per line: "Label | url" or just the url.
static async Task ShowCalendarAgendaAsync()
{
    var feeds = LoadCalendarFeeds();
    if (feeds.Count == 0)
    {
        AnsiConsole.MarkupLine(
            "[yellow]No calendar is set up yet.[/] [grey]In Google Calendar (web): Settings > " +
            "your calendar > 'Integrate calendar' > copy the 'Secret address in iCal format' URL. " +
            "Put it in Settings > Calendar feeds — one calendar per line, either the URL " +
            "alone or 'Label | url'. Treat that URL like a password: anyone who has it can read " +
            "your calendar.[/]\n");
        PauseForKey();
        return;
    }

    try
    {
        var agendaDays = DisplayNumber("agenda-days", 14, 1, 90);
        var rangeStart = DateTime.Today;
        var rangeEnd = rangeStart.AddDays(agendaDays);
        var usedStaleCache = false;

        var raw = await AnsiConsole.Status().StartAsync("Fetching calendar...", async _ =>
        {
            var list = new List<(DateTime Start, DateTime End, bool AllDay, string Title, string? Location, string Feed)>();
            foreach (var (label, url) in feeds)
            {
                var (ics, stale) = await FetchCalendarIcsAsync(url);
                if (stale) usedStaleCache = true;
                var calendar = IcsCalendar.Load(ics);
                foreach (var occurrence in calendar.GetOccurrences(rangeStart, rangeEnd))
                {
                    if (occurrence.Source is not IcsEvent ev) continue;
                    var start = occurrence.Period.StartTime.AsSystemLocal;
                    var end = occurrence.Period.EndTime?.AsSystemLocal ?? start;
                    list.Add((start, end, ev.IsAllDay, ev.Summary ?? "(untitled)", ev.Location, label));
                }
            }
            return list;
        });

        // The same event often lives on several calendars — show it once, with all
        // of its calendar names joined in the tag.
        var events = raw
            .GroupBy(e => (e.Start, e.End, e.AllDay, Title: e.Title.Trim().ToLowerInvariant()))
            .Select(g => (
                g.Key.Start,
                g.Key.End,
                g.Key.AllDay,
                g.First().Title,
                Location: g.Select(x => x.Location).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)),
                Feeds: string.Join(", ", g.Select(x => x.Feed).Distinct())))
            .OrderBy(e => e.Start)
            .ToList();

        var sb = new StringBuilder();
        sb.Append($"[bold]Agenda[/] [dim]{rangeStart:MM/dd} – {rangeEnd.AddDays(-1):MM/dd}[/]\n");
        if (usedStaleCache)
            sb.Append("[grey](Google rate-limited the calendar fetch — showing the last downloaded copy.)[/]\n");
        if (events.Count == 0)
            sb.Append($"\n[grey]No events in the next {agendaDays} day{(agendaDays == 1 ? "" : "s")}.[/]");

        DateTime? currentDay = null;
        foreach (var e in events)
        {
            if (e.Start.Date != currentDay)
            {
                currentDay = e.Start.Date;
                var dayName = currentDay == DateTime.Today ? "Today"
                    : currentDay == DateTime.Today.AddDays(1) ? "Tomorrow"
                    : currentDay.Value.ToString("dddd");
                sb.Append($"\n[bold yellow]{dayName} {currentDay:MM/dd}[/]\n");
            }

            var time = e.AllDay
                ? "all day      "
                : $"{e.Start:h:mm tt} – {e.End:h:mm tt}".PadRight(20);
            var location = string.IsNullOrWhiteSpace(e.Location) ? "" : $"  [grey]{Markup.Escape(e.Location)}[/]";
            var feedTag = feeds.Count > 1 ? $"  [dim]({Markup.Escape(e.Feeds)})[/]" : "";
            sb.Append($"  [dim]{time}[/] {Markup.Escape(e.Title)}{location}{feedTag}\n");
        }

        var panel = new Panel(new Markup(sb.ToString().TrimEnd()))
        {
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 1, 1, 1),
            Expand = true
        };
        ShowInPager(panel);
    }
    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
    {
        AnsiConsole.MarkupLine(
            "[red]Calendar error:[/] Google is rate-limiting the secret calendar address (429).\n" +
            "[grey]This clears on its own — wait a few minutes and try again. Once a fetch succeeds, " +
            "the app caches it and won't hit the limit on repeat views.[/]\n");
        PauseForKey();
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine(
            $"[red]Calendar error:[/] {Markup.Escape(ex.Message)}\n" +
            "[grey]Check the URL in Settings > Calendar feeds — it must be the 'Secret address in iCal format' " +
            "(ends in .ics), not the public embed link.[/]\n");
        PauseForKey();
    }
}

// Fetches a calendar feed with a 15-minute disk cache. Google throttles the secret
// ICS address hard (429 on frequent fetches), so fresh cache is served without a
// request at all, and a stale cache beats a failed fetch (Stale = true then).
static async Task<(string Ics, bool Stale)> FetchCalendarIcsAsync(string url)
{
    var cachePath = Paths.Data(
        $"calendar-cache-{Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(url)))[..12]}.ics");

    if (File.Exists(cachePath) && DateTime.Now - File.GetLastWriteTime(cachePath) < TimeSpan.FromMinutes(15))
        return (await File.ReadAllTextAsync(cachePath), false);

    // A feed that just failed will fail again (Google's 429s are sticky AND slow to
    // answer) — don't re-hit it for 10 minutes.
    if (CalendarThrottle.Failures.TryGetValue(url, out var lastFailure) &&
        DateTime.Now - lastFailure < TimeSpan.FromMinutes(10))
    {
        if (File.Exists(cachePath)) return (await File.ReadAllTextAsync(cachePath), true);
        throw new HttpRequestException("Calendar feed was rate-limited recently; retrying later.",
            null, HttpStatusCode.TooManyRequests);
    }

    try
    {
        var ics = await Web.GetStringAsync(url);
        CalendarThrottle.Failures.TryRemove(url, out _);
        try { await File.WriteAllTextAsync(cachePath, ics); } catch { /* cache is best effort */ }
        return (ics, false);
    }
    catch (HttpRequestException)
    {
        CalendarThrottle.Failures[url] = DateTime.Now;
        if (File.Exists(cachePath)) return (await File.ReadAllTextAsync(cachePath), true);
        throw;
    }
}

static List<(string Label, string Url)> LoadCalendarFeeds()
{
    var feeds = new List<(string, string)>();
    foreach (var line in Config.Lines("calendar"))
    {
        var parts = line.Split('|', 2, StringSplitOptions.TrimEntries);
        var (label, url) = parts.Length == 2 ? (parts[0], parts[1]) : ("Calendar", parts[0]);
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            feeds.Add((label, url));
    }
    return feeds;
}

// Gmail inbox reader: lists recent messages (read and unread, ● marks unread),
// marks a message read when it's viewed, and supports replying (R in the
// message view) and composing new mail. Sending goes through Gmail SMTP with
// the same app-password credentials as IMAP; Gmail files sent mail into the
// Sent folder automatically.
static async Task ShowUnreadEmailAsync()
{
    var accounts = LoadGmailAccounts();
    if (accounts.Count == 0)
    {
        AnsiConsole.MarkupLine(
            "[yellow]Gmail is not set up yet.[/] [grey]Open Settings > Gmail accounts from the main menu: " +
            "line 1 your Gmail address, line 2 a Google app password " +
            "(myaccount.google.com > Security > 2-Step Verification > App passwords).[/]\n");
        PauseForKey();
        return;
    }

    // One account goes straight to its inbox; multiple get a picker.
    if (accounts.Count == 1)
    {
        await ShowGmailInboxAsync(accounts[0]);
        return;
    }

    var lastIdx = 0;
    while (true)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold blue]Email[/] [grey]— multiple accounts configured[/]");
        var options = accounts.Select(a => Markup.Escape(a.Email)).ToList();
        options.Add("<= Back to Main Menu");

        var idx = PromptMenu("Pick an inbox:", options, 15, initialSelected: lastIdx);
        if (idx < 0 || idx == options.Count - 1)
        {
            AnsiConsole.Clear();
            return;
        }
        lastIdx = idx;
        await ShowGmailInboxAsync(accounts[idx]);
    }
}

// The inbox reader for one Gmail account.
static async Task ShowGmailInboxAsync((string Email, string AppPassword) creds)
{
    try
    {
        using var imap = new ImapClient();

        async Task<List<IMessageSummary>> FetchInboxAsync()
        {
            if (!imap.IsConnected)
            {
                await imap.ConnectAsync("imap.gmail.com", 993, SecureSocketOptions.SslOnConnect);
                await imap.AuthenticateAsync(creds.Email, creds.AppPassword);
            }
            // ReadWrite so viewing a message can set its \Seen flag.
            if (!imap.Inbox.IsOpen || imap.Inbox.Access != FolderAccess.ReadWrite)
                await imap.Inbox.OpenAsync(FolderAccess.ReadWrite);

            var first = Math.Max(0, imap.Inbox.Count - 50);
            var fetched = imap.Inbox.Count == 0
                ? []
                : await imap.Inbox.FetchAsync(first, -1,
                    MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId | MessageSummaryItems.Flags);
            return fetched.OrderByDescending(m => m.Date).ToList();
        }

        var messages = await AnsiConsole.Status().StartAsync("Loading inbox...",
            async _ => await FetchInboxAsync());
        var readNow = new HashSet<uint>(); // uids marked \Seen in this session

        var lastIdx = 0;
        while (true)
        {
            AnsiConsole.Clear();
            var unreadCount = messages.Count(m => IsUnread(m) && !readNow.Contains(m.UniqueId.Id));
            AnsiConsole.MarkupLine(
                $"[bold blue]Email inbox[/] [grey]— {Markup.Escape(creds.Email)} — {messages.Count} recent message(s), " +
                $"{unreadCount} unread, newest first. Viewing marks a message read.[/]");

            const string compose = "== Compose a new message ==";
            const string refresh = "== Refresh ==";
            var options = messages.Select(m =>
            {
                var from = m.Envelope?.From?.Mailboxes?.FirstOrDefault();
                var who = from?.Name is { Length: > 0 } name ? name : from?.Address ?? "?";
                var dot = IsUnread(m) && !readNow.Contains(m.UniqueId.Id) ? "[bold cyan]●[/] " : "  ";
                return $"{dot}[bold]{m.Date.ToLocalTime():MM/dd HH:mm}[/]  {Markup.Escape(who)}  " +
                       $"[grey]{Markup.Escape(m.Envelope?.Subject ?? "(no subject)")}[/]";
            }).ToList();
            options.Add(compose);
            options.Add(refresh);
            options.Add("<= Back to Main Menu");

            var idx = PromptMenu("Select a message:", options, 15, initialSelected: lastIdx);
            if (idx < 0 || idx == options.Count - 1) break;
            lastIdx = idx;

            if (options[idx] == refresh)
            {
                messages = await AnsiConsole.Status().StartAsync("Refreshing...",
                    async _ => await FetchInboxAsync());
                continue;
            }
            if (options[idx] == compose)
            {
                await ComposeEmailAsync(creds);
                continue;
            }

            var summary = messages[idx];
            var message = await AnsiConsole.Status().StartAsync("Downloading message...", async _ =>
            {
                var msg = await imap.Inbox.GetMessageAsync(summary.UniqueId);
                // Mark read on view. Best-effort: a flag failure shouldn't block reading.
                try
                {
                    await imap.Inbox.AddFlagsAsync(summary.UniqueId, MessageFlags.Seen, silent: true);
                    readNow.Add(summary.UniqueId.Id);
                }
                catch (Exception ex) { AppLog.Debug("mark email read", ex); }
                return msg;
            });

            var bodyText = message.TextBody;
            if (string.IsNullOrWhiteSpace(bodyText))
                bodyText = HtmlFragmentToText(message.HtmlBody ?? "");
            if (string.IsNullOrWhiteSpace(bodyText))
                bodyText = "(no readable body)";

            var panel = new Panel(new Markup(
                $"[bold]{Markup.Escape(summary.Envelope?.Subject ?? "(no subject)")}[/]\n" +
                $"[dim]From: {Markup.Escape(summary.Envelope?.From?.ToString() ?? "?")}\n" +
                $"{summary.Date.ToLocalTime():f}[/]\n\n" +
                Markup.Escape(bodyText.Trim())))
            {
                Border = BoxBorder.Rounded,
                Padding = new Padding(1, 1, 1, 1),
                Expand = true
            };

            var emailDoc = new HtmlDocument();
            emailDoc.LoadHtml(message.HtmlBody ?? "");
            var links = BuildLinks(ExtractHtmlLinks(emailDoc), bodyText);

            while (true)
            {
                var action = ShowInPager(panel, links,
                    actions: [(ConsoleKey.R, "R reply"), (ConsoleKey.A, "A archive")]);
                if (action == null) break;

                if (action == ConsoleKey.R)
                {
                    await ReplyToEmailAsync(creds, message);
                    continue;
                }

                // Archive = move out of Inbox into All Mail, exactly what Gmail's
                // own Archive button does; the message stays findable in All Mail.
                var subjectLabel = summary.Envelope?.Subject ?? "(no subject)";
                if (!AnsiConsole.Confirm($"Archive \"{Markup.Escape(subjectLabel)}\"?", defaultValue: true))
                    continue;

                var archived = await AnsiConsole.Status().StartAsync("Archiving...", async _ =>
                {
                    try
                    {
                        var allMail = imap.GetFolder(SpecialFolder.All);
                        if (allMail == null) return false;
                        await imap.Inbox.MoveToAsync(summary.UniqueId, allMail);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Debug("archive email", ex);
                        return false;
                    }
                });
                if (archived)
                {
                    messages.Remove(summary);
                    break; // back to the list, which no longer shows this message
                }
                AnsiConsole.MarkupLine("[red]Could not archive — the message is unchanged.[/]\n");
                PauseForKey();
            }
        }

        if (imap.IsConnected) await imap.DisconnectAsync(true);
        AnsiConsole.Clear();
    }
    catch (AuthenticationException)
    {
        AnsiConsole.MarkupLine(
            $"[red]Gmail sign-in failed for {Markup.Escape(creds.Email)}.[/] [grey]Check Settings > Gmail accounts: " +
            "the address and its 16-character app password.[/]\n");
        PauseForKey();
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Gmail error:[/] {Markup.Escape(ex.Message)}\n");
        PauseForKey();
    }

    static bool IsUnread(IMessageSummary m) =>
        !(m.Flags?.HasFlag(MessageFlags.Seen) ?? false);
}

// Multi-line message-body input using native console lines (Backspace works
// across wrapped lines). A single '.' on its own line finishes, like classic
// sendmail; returns null if the body ends up empty.
static string? PromptEmailBody()
{
    AnsiConsole.MarkupLine(
        "[green]Message body[/] [grey](finish with a single '.' on its own line; finish with nothing typed to cancel):[/]");
    var lines = new List<string>();
    while (true)
    {
        var line = Console.ReadLine() ?? ".";
        if (line.Trim() == ".") break;
        lines.Add(line);
    }
    var body = string.Join("\n", lines).Trim();
    return body.Length == 0 ? null : body;
}

// Sends through Gmail SMTP with the same app-password credentials as IMAP.
static async Task SendGmailAsync((string Email, string AppPassword) creds, MimeMessage message)
{
    using var smtp = new SmtpClient();
    await smtp.ConnectAsync("smtp.gmail.com", 465, SecureSocketOptions.SslOnConnect);
    await smtp.AuthenticateAsync(creds.Email, creds.AppPassword);
    await smtp.SendAsync(message);
    await smtp.DisconnectAsync(true);
}

// Composes and sends a brand-new email: To, Subject, multi-line body, then an
// explicit confirmation before anything is sent.
static async Task ComposeEmailAsync((string Email, string AppPassword) creds)
{
    AnsiConsole.Clear();
    AnsiConsole.MarkupLine("[bold blue]Compose[/] [grey]— sent from " + Markup.Escape(creds.Email) + "[/]\n");

    AnsiConsole.Markup("[green]To[/] [grey](one or more addresses, comma-separated; blank cancels):[/] ");
    var toText = (Console.ReadLine() ?? "").Trim();
    if (toText.Length == 0) return;
    if (!InternetAddressList.TryParse(toText, out var to) || to.Mailboxes.Count() == 0)
    {
        AnsiConsole.MarkupLine("[red]That doesn't parse as an email address — nothing was sent.[/]\n");
        PauseForKey();
        return;
    }

    AnsiConsole.Markup("[green]Subject:[/] ");
    var subject = (Console.ReadLine() ?? "").Trim();

    var body = PromptEmailBody();
    if (body == null) return;

    var message = new MimeMessage();
    message.From.Add(new MailboxAddress("", creds.Email));
    message.To.AddRange(to);
    message.Subject = subject;
    message.Body = new TextPart("plain") { Text = body };

    if (!AnsiConsole.Confirm($"Send to {Markup.Escape(toText)}?", defaultValue: true)) return;
    await AnsiConsole.Status().StartAsync("Sending...", async _ => await SendGmailAsync(creds, message));
    AnsiConsole.MarkupLine("[green]Sent.[/]");
    PauseForKey();
}

// Replies to an open message: goes to Reply-To (or From), keeps the thread via
// In-Reply-To/References, quotes the original below the reply, and confirms
// before sending.
static async Task ReplyToEmailAsync((string Email, string AppPassword) creds, MimeMessage original)
{
    AnsiConsole.Clear();
    var to = original.ReplyTo.Count > 0 ? original.ReplyTo : original.From;
    AnsiConsole.MarkupLine($"[bold blue]Reply[/] [grey]— to {Markup.Escape(to.ToString())}[/]\n");

    var body = PromptEmailBody();
    if (body == null) return;

    var subject = original.Subject ?? "";
    if (!subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase))
        subject = "Re: " + subject;

    var quotedSource = original.TextBody ?? HtmlFragmentToText(original.HtmlBody ?? "");
    var quoted = string.Join("\n", (quotedSource ?? "").Trim().Split('\n').Select(l => "> " + l.TrimEnd()));

    var message = new MimeMessage();
    message.From.Add(new MailboxAddress("", creds.Email));
    message.To.AddRange(to);
    message.Subject = subject;
    if (!string.IsNullOrEmpty(original.MessageId))
    {
        message.InReplyTo = original.MessageId;
        foreach (var r in original.References) message.References.Add(r);
        message.References.Add(original.MessageId);
    }
    message.Body = new TextPart("plain")
    {
        Text = $"{body}\n\nOn {original.Date.ToLocalTime():f}, {original.From} wrote:\n{quoted}"
    };

    if (!AnsiConsole.Confirm($"Send reply to {Markup.Escape(to.ToString())}?", defaultValue: true)) return;
    await AnsiConsole.Status().StartAsync("Sending...", async _ => await SendGmailAsync(creds, message));
    AnsiConsole.MarkupLine("[green]Sent.[/]");
    PauseForKey();
}

// Silent Gmail lookup used as a fallback when a newsletter's website copy is
// bot-blocked: finds the edition nearest the given date and returns its readable
// text, or null if Gmail isn't set up or no matching email exists.
static async Task<string?> TryFetchNewsletterTextFromGmailAsync(
    string fromContains, string? subjectContains, DateTimeOffset editionDate)
{
    var creds = LoadGmailCredentials();
    if (creds == null) return null;
    if (editionDate.Year < 2000) editionDate = DateTimeOffset.Now;

    try
    {
        using var imap = new ImapClient();
        await imap.ConnectAsync("imap.gmail.com", 993, SecureSocketOptions.SslOnConnect);
        await imap.AuthenticateAsync(creds.Value.Email, creds.Value.AppPassword);

        var folder = imap.GetFolder(SpecialFolder.All) ?? imap.Inbox;
        await folder.OpenAsync(FolderAccess.ReadOnly);

        // IMAP date search is day-granular; a ±1 day window absorbs timezone skew.
        SearchQuery query = SearchQuery.FromContains(fromContains)
            .And(SearchQuery.DeliveredAfter(editionDate.Date.AddDays(-1)))
            .And(SearchQuery.DeliveredBefore(editionDate.Date.AddDays(2)));
        if (!string.IsNullOrEmpty(subjectContains))
            query = query.And(SearchQuery.SubjectContains(subjectContains));

        var uids = await folder.SearchAsync(query);
        if (uids.Count == 0) return null;

        var fetched = await folder.FetchAsync(uids,
            MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId);
        var best = fetched.OrderBy(m => Math.Abs((m.Date - editionDate).TotalHours)).First();
        var message = await folder.GetMessageAsync(best.UniqueId);
        await imap.DisconnectAsync(true);

        var doc = new HtmlDocument();
        doc.LoadHtml(message.HtmlBody ?? message.TextBody ?? "");
        var text = ExtractReadableText(doc);
        return text.Length > 0 ? text : null;
    }
    catch
    {
        // Fallback only — the caller shows the blocked-page message if this fails.
        return null;
    }
}

// Reads text messages by driving an embedded Microsoft Edge against Google Messages
// for Web (messages.google.com) — the site is a JS app with no scrapeable HTTP API.
// First use opens a visible window for QR pairing; the session lives in a
// 'gmessages-profile' folder next to the app, so later runs are invisible.
static async Task ShowTextMessagesAsync()
{
    var profileDir = Path.Combine(AppContext.BaseDirectory, "gmessages-profile");
    const string conversationsUrl = "https://messages.google.com/web/conversations";
    const string convItemSelector = "mws-conversation-list-item";

    IPlaywright? playwright = null;
    IPage? page = null;
    try
    {
        playwright = await Playwright.CreateAsync();

        var context = await AnsiConsole.Status().StartAsync("Starting embedded browser...",
            async _ => await LaunchMessagesBrowserAsync(playwright, profileDir, headless: true));
        page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();

        // A fresh profile lands on a welcome page; an unpaired-but-visited one lands on
        // the QR page; a paired one goes straight to the conversation list.
        const string landingSelector = $"{convItemSelector}, mw-qr-code, mw-welcome-page-container";
        var landed = await AnsiConsole.Status().StartAsync("Opening Google Messages...", async _ =>
        {
            await page!.GotoAsync(conversationsUrl);
            var el = await page.WaitForSelectorAsync(landingSelector,
                new PageWaitForSelectorOptions { Timeout = 45000 });
            return await el!.EvaluateAsync<string>("e => e.tagName.toLowerCase()");
        });

        if (landed != convItemSelector)
        {
            // Pairing must happen in a NORMAL browser: Google's sign-in rejects any
            // browser with an automation debugger attached (silent sign-in loop), so
            // launch plain Chrome on the same profile with no automation at all.
            // Reading the paired session afterwards with automation is fine.
            await context.DisposeAsync();
            AnsiConsole.MarkupLine(
                "[yellow]This computer isn't paired with your phone yet.[/]\n" +
                "[grey]A regular Chrome window will open (no automation, so Google sign-in works).\n" +
                "Pair with whichever method your phone offers:\n" +
                "  • QR code: Messages app > profile picture > Device pairing > scan the code, or\n" +
                "  • Google account: click 'Sign in', sign in, and confirm the image/emoji on your phone.\n" +
                "Keep 'Remember this computer' enabled. Wait until your conversations appear " +
                "in the browser, then close the browser window to continue here.[/]\n");
            PauseForKey();

            var pairingProc = Process.Start(new ProcessStartInfo(FindBrowserExe(),
                $"--user-data-dir=\"{profileDir}\" --no-first-run --new-window {conversationsUrl}")
            {
                UseShellExecute = false
            });
            AnsiConsole.MarkupLine("[grey]Waiting for you to pair and close the browser window...[/]");
            if (pairingProc != null) await pairingProc.WaitForExitAsync();

            context = await LaunchMessagesBrowserAsync(playwright, profileDir, headless: true);
            page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
            await page.GotoAsync(conversationsUrl);
            await page.WaitForSelectorAsync(convItemSelector,
                new PageWaitForSelectorOptions { Timeout = 60000 });
        }

        try
        {
            var lastIdx = 0;
            const string refresh = "== Refresh ==";
            while (true)
            {
                // The conversation-list pane stays loaded while a conversation is open,
                // so this is a fast in-DOM read (no page reload) — the spinner just
                // covers the brief scrape and any wait if the list isn't ready yet.
                var conversations = await AnsiConsole.Status().StartAsync("Loading conversations...",
                    async _ =>
                    {
                        await page.WaitForSelectorAsync(convItemSelector,
                            new PageWaitForSelectorOptions { Timeout = 30000 });
                        return await ScrapeConversationsAsync(page);
                    });

                AnsiConsole.Clear();
                AnsiConsole.MarkupLine("[bold blue]Text messages[/] [grey]— Google Messages[/]");
                if (conversations.Count == 0)
                    AnsiConsole.MarkupLine("[yellow]No conversations found — the page layout may have changed.[/]");

                var options = conversations.Select(c =>
                    $"{(c.Unread ? "[bold cyan]●[/] " : "  ")}[bold]{Markup.Escape(c.Name)}[/]  " +
                    $"[grey]{Markup.Escape(c.Snippet)}[/]").ToList();
                options.Add(refresh);
                options.Add("<= Back to Main Menu");

                var idx = PromptMenu("Select a conversation:", options, 15, initialSelected: lastIdx);
                if (idx < 0 || idx == options.Count - 1) break;

                if (options[idx] == refresh)
                {
                    // Explicit refresh does a full reload to pull in brand-new threads.
                    await AnsiConsole.Status().StartAsync("Refreshing...", async _ =>
                    {
                        await page.GotoAsync(conversationsUrl);
                        await page.WaitForSelectorAsync(convItemSelector,
                            new PageWaitForSelectorOptions { Timeout = 30000 });
                    });
                    continue;
                }
                lastIdx = idx;

                await ShowSmsConversationAsync(page, conversations[idx].Index, conversations[idx].Name);
                // No reload on return — the list pane is still loaded; the loop
                // re-scrapes it (reflecting any sent reply or archive) instantly.
            }
        }
        finally
        {
            await context.DisposeAsync();
        }
        AnsiConsole.Clear();
    }
    catch (PlaywrightException ex)
    {
        AnsiConsole.MarkupLine(
            $"[red]Browser automation error:[/] {Markup.Escape(ex.Message)}\n" +
            "[grey]If pairing broke or the page changed, delete the 'gmessages-profile' folder " +
            "next to the app and pair again.[/]\n");
        await DumpMessagesDiagnosticsAsync(page);
        PauseForKey();
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Text-message error:[/] {Markup.Escape(ex.Message)}\n");
        await DumpMessagesDiagnosticsAsync(page);
        PauseForKey();
    }
    finally
    {
        playwright?.Dispose();
    }
}

// Saves what the Google Messages page actually contained when something failed
// (URL, custom element names, visible text, screenshot), so the scraping
// selectors can be adapted when Google changes the app.
static async Task DumpMessagesDiagnosticsAsync(IPage? page)
{
    if (page == null) return;
    try
    {
        var tags = await page.EvaluateAsync<string[]>(
            "() => Array.from(new Set(Array.from(document.querySelectorAll('*'))" +
            ".map(e => e.tagName.toLowerCase()).filter(t => t.includes('-')))).slice(0, 80)");
        var text = await page.EvaluateAsync<string>(
            "() => (document.body.innerText || '').substring(0, 1000)");
        File.WriteAllText(Paths.Data("gmessages-debug.txt"),
            $"url: {page.Url}\n\ncustom elements on page:\n{string.Join("\n", tags)}\n\nvisible text:\n{text}\n");
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = Paths.Data("gmessages-debug.png")
        });
        AnsiConsole.MarkupLine(
            "[grey]Saved what the page looked like to data/gmessages-debug.txt and .png " +
            "— share those to get the selectors fixed.[/]\n");
    }
    catch
    {
        // Diagnostics are best-effort; the original error message is already shown.
    }
}

// Full path to the installed Chrome (preferred) or Edge, for launching a plain,
// non-automated pairing window.
static string FindBrowserExe()
{
    string[] candidates =
    [
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Google\Chrome\Application\chrome.exe"),
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    ];
    return candidates.FirstOrDefault(File.Exists)
        ?? throw new InvalidOperationException("Neither Chrome nor Edge was found on this machine.");
}

static async Task<IBrowserContext> LaunchMessagesBrowserAsync(IPlaywright playwright, string profileDir, bool headless)
{
    // Prefer the installed Chrome (Google's sign-in flow trusts it more than Edge),
    // fall back to Edge. Either way no separate browser download is needed.
    var chromeInstalled = FindBrowserExe().Contains("chrome.exe", StringComparison.OrdinalIgnoreCase);

    return await playwright.Chromium.LaunchPersistentContextAsync(profileDir,
        new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = headless,
            Channel = chromeInstalled ? "chrome" : "msedge",
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            // Hides navigator.webdriver — without this Google's sign-in shows
            // "This browser or app may not be secure" and refuses to proceed.
            Args = ["--disable-blink-features=AutomationControlled"],
            // Playwright passes --enable-automation by default; Google account
            // sign-in detects it and drops the session (endless sign-in loop).
            IgnoreDefaultArgs = ["--enable-automation"],
        });
}

// Scrapes the conversation list: name, last-message snippet, unread state.
static async Task<List<SmsConversation>> ScrapeConversationsAsync(IPage page)
{
    var json = await page.EvaluateAsync<string>(
        @"() => JSON.stringify(Array.from(document.querySelectorAll('mws-conversation-list-item')).map((e, i) => {
            const lines = (e.innerText || '').split('\n').map(s => s.trim()).filter(s => s.length > 0);
            const aria = (e.getAttribute('aria-label') || '') +
                         (e.querySelector('[aria-label]')?.getAttribute('aria-label') || '');
            return {
                index: i,
                name: lines[0] || '(unknown)',
                snippet: lines.slice(1).join(' · '),
                unread: !!e.querySelector('[class*=unread]') || aria.toLowerCase().includes('unread')
            };
        }))");

    var list = new List<SmsConversation>();
    using var doc = JsonDocument.Parse(json);
    foreach (var e in doc.RootElement.EnumerateArray())
    {
        list.Add(new SmsConversation(
            e.GetProperty("index").GetInt32(),
            e.GetProperty("name").GetString() ?? "?",
            e.GetProperty("snippet").GetString() ?? "",
            e.GetProperty("unread").GetBoolean()));
    }
    return list;
}

// Opens one conversation, shows its messages in the pager, and lets the user
// reply with R, react to a message with E, quote-reply to a message with T;
// the thread refreshes after each action.
static async Task ShowSmsConversationAsync(IPage page, int index, string name)
{
    await AnsiConsole.Status().StartAsync("Opening conversation...", async _ =>
    {
        await page.Locator("mws-conversation-list-item").Nth(index).ClickAsync();
        await page.WaitForSelectorAsync("mws-message-wrapper",
            new PageWaitForSelectorOptions { Timeout = 20000 });
        await page.WaitForTimeoutAsync(800); // let the thread finish rendering
    });

    while (true)
    {
        var messages = await ScrapeThreadMessagesAsync(page);

        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            sb.Append(m.Incoming
                ? $"[bold cyan]{Markup.Escape(name)}[/]\n"
                : "[bold green]You[/]\n");
            sb.Append(Markup.Escape(m.Text)).Append("\n\n");
        }
        if (sb.Length == 0)
            sb.Append("[yellow]Could not read messages from this conversation — the page layout may have changed.[/]");

        var panel = new Panel(new Markup($"[bold]{Markup.Escape(name)}[/]\n\n" + sb.ToString().TrimEnd()))
        {
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 1, 1, 1),
            Expand = true
        };

        var action = ShowInPager(panel, BuildLinks([], sb.ToString()),
            actions:
            [
                (ConsoleKey.R, "R reply"), (ConsoleKey.E, "E react"),
                (ConsoleKey.T, "T reply-to"), (ConsoleKey.A, "A archive")
            ], startAtEnd: true, tryReadLinksInTerminal: true);
        if (action == null) return;

        if (action == ConsoleKey.E)
        {
            await ReactToMessageFlowAsync(page, name, messages);
            continue;
        }

        if (action == ConsoleKey.T)
        {
            await QuoteReplyFlowAsync(page, name, messages);
            continue;
        }

        if (action == ConsoleKey.A)
        {
            if (!AnsiConsole.Confirm($"Archive the conversation with {Markup.Escape(name)}?", defaultValue: true))
                continue; // declined — back to the thread

            var archived = await AnsiConsole.Status().StartAsync("Archiving...",
                async _ => await TryArchiveConversationAsync(page, index));
            if (archived) return; // back to the conversation list, which refreshes

            AnsiConsole.MarkupLine(
                "[red]Could not find the conversation menu — the page layout may have changed. " +
                "Nothing was archived.[/]\n");
            PauseForKey();
            continue;
        }

        var reply = PromptReplyLine(
            $"[green]Reply to {Markup.Escape(name)}[/] [grey](leave blank to cancel):[/]");
        if (reply.Length > 0)
        {
            var sent = await AnsiConsole.Status().StartAsync("Sending...",
                async _ => await TrySendReplyAsync(page, reply));
            if (sent)
            {
                await page.WaitForTimeoutAsync(1500); // let the sent message render
            }
            else
            {
                AnsiConsole.MarkupLine(
                    "[red]Could not find the compose box — the page layout may have changed. " +
                    "Nothing was sent.[/]\n");
                PauseForKey();
            }
        }
        // Loop: rescrape the thread (now including the reply) and show it again.
    }
}

// Archives a conversation via its LIST entry's hover menu — the verified path:
// hover the item, click its "Options for <name>" button, click Archive. (The
// thread header's own menu button does not lead to the archive entry.) Archiving
// is reversible from Google Messages' "Archived" section.
static async Task<bool> TryArchiveConversationAsync(IPage page, int index)
{
    try
    {
        var item = page.Locator("mws-conversation-list-item").Nth(index);
        await item.HoverAsync(new LocatorHoverOptions { Timeout = 5000 });
        await item.Locator("[data-e2e-conversation-list-item-menu]").First
            .ClickAsync(new LocatorClickOptions { Timeout = 5000 });
        await page.Locator("[data-e2e-conversation-menu-archive]").First
            .ClickAsync(new LocatorClickOptions { Timeout = 5000 });
        await page.WaitForTimeoutAsync(800); // let the archive apply before the list rescrape
        return true;
    }
    catch (Exception)
    {
        // Includes Playwright timeouts, which don't derive from PlaywrightException.
        return false;
    }
}

// Types the reply into the compose box and sends it. Enter is Google Messages'
// send key; if the box didn't empty (layout change?), fall back to a send button.
static async Task<bool> TrySendReplyAsync(IPage page, string text)
{
    foreach (var selector in new[] { "mws-message-compose textarea", "textarea[aria-label*='message' i]", "textarea" })
    {
        var boxes = page.Locator(selector);
        try
        {
            if (await boxes.CountAsync() == 0) continue;
            var box = boxes.First;
            await box.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
            await box.FillAsync(text, new LocatorFillOptions { Timeout = 3000 });
            await box.PressAsync("Enter", new LocatorPressOptions { Timeout = 3000 });

            await page.WaitForTimeoutAsync(500);
            if (string.IsNullOrEmpty(await box.InputValueAsync())) return true;

            foreach (var btnSelector in new[]
                     { "mws-message-send-button button", "button[data-e2e-send-text-button]", "button[aria-label*='send' i]" })
            {
                var buttons = page.Locator(btnSelector);
                if (await buttons.CountAsync() == 0) continue;
                await buttons.First.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
                await page.WaitForTimeoutAsync(500);
                if (string.IsNullOrEmpty(await box.InputValueAsync())) return true;
            }
            return false;
        }
        catch (Exception)
        {
            // Try the next compose-box candidate. (Playwright timeouts don't derive
            // from PlaywrightException, so catch broadly here.)
        }
    }
    return false;
}

// Scrapes the open thread's message bubbles. Direction lives in the is-outgoing
// attribute (and an 'outgoing' class). Note every wrapper also has an
// 'ng-trigger-incomingMessage' animation class, so matching on the word
// 'incoming' is a trap.
static async Task<List<SmsMessage>> ScrapeThreadMessagesAsync(IPage page)
{
    var json = await page.EvaluateAsync<string>(
        @"() => JSON.stringify(Array.from(document.querySelectorAll('mws-message-wrapper')).map((e, i) => ({
            index: i,
            incoming: !(e.getAttribute('is-outgoing') === 'true' || e.classList.contains('outgoing')),
            text: (e.innerText || '').split('\n').map(s => s.trim()).filter(s => s.length > 0).join('\n')
        })))");

    var list = new List<SmsMessage>();
    using var doc = JsonDocument.Parse(json);
    foreach (var m in doc.RootElement.EnumerateArray())
    {
        var text = m.GetProperty("text").GetString() ?? "";
        if (text.Length == 0) continue;
        list.Add(new SmsMessage(
            m.GetProperty("index").GetInt32(),
            m.GetProperty("incoming").GetBoolean(),
            text));
    }
    return list;
}

// Menu over the most recent messages so the user can pick one to act on.
// Returns null if they cancel.
static SmsMessage? PickThreadMessage(string name, List<SmsMessage> messages, string title)
{
    if (messages.Count == 0) return null;
    var recent = messages.Skip(Math.Max(0, messages.Count - 15)).ToList();

    var options = recent.Select(m =>
    {
        var snippet = m.Text.ReplaceLineEndings(" ");
        if (snippet.Length > 60) snippet = snippet[..57] + "...";
        return (m.Incoming ? $"[bold cyan]{Markup.Escape(name)}:[/] " : "[bold green]You:[/] ")
               + Markup.Escape(snippet);
    }).ToList();
    options.Add("<= Cancel");

    var idx = PromptMenu(title, options, 15, initialSelected: options.Count - 2);
    if (idx < 0 || idx == options.Count - 1) return null;
    return recent[idx];
}

// Hovers a message bubble so its hover-action buttons become clickable, and
// returns the bubble's wrapper locator (null on failure). Each bubble carries an
// mws-message-actions element with up to three buttons — reactions selector
// (data-e2e-message-reactions-menu), quote-reply (data-e2e-reply-to-message-button),
// and a three-dot menu — but they only accept clicks while the message PART
// (mws-message-part-router) is hovered; hovering the wrapper isn't enough.
static async Task<ILocator?> HoverMessageActionsAsync(IPage page, int msgIndex)
{
    try
    {
        var wrapper = page.Locator("mws-message-wrapper").Nth(msgIndex);
        await wrapper.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions { Timeout = 5000 });
        await wrapper.Locator("mws-message-part-router").First
            .HoverAsync(new LocatorHoverOptions { Timeout = 5000 });
        await page.WaitForTimeoutAsync(300); // let the hover actions arm
        return wrapper;
    }
    catch (Exception)
    {
        // Includes Playwright timeouts, which don't derive from PlaywrightException.
        return null;
    }
}

// Full react flow: pick a message, open its reaction picker, pick an emoji, click it.
static async Task ReactToMessageFlowAsync(IPage page, string name, List<SmsMessage> messages)
{
    var msg = PickThreadMessage(name, messages, "React to which message?");
    if (msg == null) return;

    var choices = await AnsiConsole.Status().StartAsync("Opening reaction picker...", async _ =>
    {
        var wrapper = await HoverMessageActionsAsync(page, msg.Index);
        if (wrapper == null) return null;
        try
        {
            await wrapper.Locator("button[data-e2e-message-reactions-menu]").First
                .ClickAsync(new LocatorClickOptions { Timeout = 5000 });
            // The picker is an mw-message-reactions-selector strip next to the bubble.
            await page.WaitForSelectorAsync("button[data-e2e-message-reaction]",
                new PageWaitForSelectorOptions { Timeout = 5000 });
            return await ScrapeReactionChoicesAsync(page);
        }
        catch (Exception)
        {
            return null;
        }
    });

    if (choices == null || choices.Count == 0)
    {
        await page.Keyboard.PressAsync("Escape"); // close anything half-opened
        AnsiConsole.MarkupLine(
            "[red]Could not open the reaction picker — this message may not support reactions, " +
            "or the page layout may have changed.[/]\n");
        PauseForKey();
        return;
    }

    const string moreEmojis = "\U0001f50e Search all emojis...";
    var options = choices.Select(c =>
        $"{c.Emoji}  [grey]{Markup.Escape(c.Label)}[/]" +
        (c.Mine ? "  [yellow](already yours — choosing it again removes it)[/]" : "")).ToList();
    options.Add(moreEmojis);
    options.Add("<= Cancel");
    var idx = PromptMenu("React with:", options, 15);
    if (idx < 0 || idx == options.Count - 1)
    {
        await page.Keyboard.PressAsync("Escape"); // close the picker without reacting
        return;
    }
    if (options[idx] == moreEmojis)
    {
        await FullEmojiReactFlowAsync(page);
        return;
    }

    var clicked = await AnsiConsole.Status().StartAsync("Reacting...", async _ =>
    {
        try
        {
            await page.Locator($"button[data-e2e-message-reaction='{choices[idx].Emoji}']").First
                .ClickAsync(new LocatorClickOptions { Timeout = 5000 });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    });
    if (clicked)
    {
        await page.WaitForTimeoutAsync(1000); // let the reaction render before rescrape
    }
    else
    {
        await page.Keyboard.PressAsync("Escape");
        AnsiConsole.MarkupLine("[red]Could not click that emoji — nothing was sent.[/]\n");
        PauseForKey();
    }
}

// Full-palette react: from the open reaction strip, opens Google's "React with
// any emoji" picker (mws-emoji-picker-v2) and drives its search box — the
// palette is thousands of emojis, so the terminal flow is search-then-pick.
// Each result cell is a span with the emoji in data-emoji-info-emoji. Clicking
// a cell sends the reaction immediately.
static async Task FullEmojiReactFlowAsync(IPage page)
{
    var opened = await AnsiConsole.Status().StartAsync("Opening emoji palette...", async _ =>
    {
        try
        {
            await page.Locator("button[data-e2e-message-emoji-reaction]").First
                .ClickAsync(new LocatorClickOptions { Timeout = 5000 });
            await page.WaitForSelectorAsync("input[aria-label='Search emoji'], mws-picker-search-bar input",
                new PageWaitForSelectorOptions { Timeout = 5000 });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    });
    if (!opened)
    {
        await CloseReactionOverlaysAsync(page);
        AnsiConsole.MarkupLine(
            "[red]Could not open the full emoji palette — the page layout may have changed.[/]\n");
        PauseForKey();
        return;
    }

    while (true)
    {
        var query = AnsiConsole.Prompt(new TextPrompt<string>(
                "[green]Search emoji[/] [grey](e.g. \"fire\", \"party\"; leave blank to cancel):[/]")
            .AllowEmpty()).Trim();
        if (query.Length == 0)
        {
            await CloseReactionOverlaysAsync(page);
            return;
        }

        var emojis = await AnsiConsole.Status().StartAsync("Searching...", async _ =>
        {
            try
            {
                await page.Locator("input[aria-label='Search emoji'], mws-picker-search-bar input").First
                    .FillAsync(query, new LocatorFillOptions { Timeout = 3000 });
                await page.WaitForTimeoutAsync(900); // let the results filter
                var json = await page.EvaluateAsync<string>(
                    @"() => JSON.stringify(Array.from(document.querySelectorAll('span[data-emoji-info-emoji]'))
                        .filter(e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; })
                        .map(e => e.getAttribute('data-emoji-info-emoji') || '')
                        .filter(s => s.length > 0)
                        .slice(0, 30))");
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.EnumerateArray()
                    .Select(e => e.GetString() ?? "").Where(s => s.Length > 0).Distinct().ToList();
            }
            catch (Exception)
            {
                return [];
            }
        });

        if (emojis.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No emoji matched that search — try another word.[/]");
            continue;
        }

        const string searchAgain = "== Search again ==";
        var options = emojis.Select(Markup.Escape).ToList();
        options.Add(searchAgain);
        options.Add("<= Cancel");
        var idx = PromptMenu($"React with ({emojis.Count} matches for \"{Markup.Escape(query)}\"):", options, 15);
        if (idx < 0 || idx == options.Count - 1)
        {
            await CloseReactionOverlaysAsync(page);
            return;
        }
        if (options[idx] == searchAgain) continue;

        var clicked = await AnsiConsole.Status().StartAsync("Reacting...", async _ =>
        {
            try
            {
                // The palette keeps hidden duplicates of an emoji on other category
                // pages, so restrict to the visible one.
                await page.Locator($"span[data-emoji-info-emoji='{emojis[idx]}']:visible").First
                    .ClickAsync(new LocatorClickOptions { Timeout = 5000 });
                await page.WaitForTimeoutAsync(1000); // let the reaction render before rescrape
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        });
        if (!clicked)
        {
            await CloseReactionOverlaysAsync(page);
            AnsiConsole.MarkupLine("[red]Could not click that emoji — nothing was sent.[/]\n");
            PauseForKey();
        }
        return;
    }
}

// Closes whatever reaction UI is open (the emoji palette and/or the reaction
// strip) — each Escape dismisses one layer.
static async Task CloseReactionOverlaysAsync(IPage page)
{
    await page.Keyboard.PressAsync("Escape");
    await page.WaitForTimeoutAsync(200);
    await page.Keyboard.PressAsync("Escape");
}

// Reads the choices out of the open reaction picker. Each button carries the
// emoji in its data-e2e-message-reaction attribute, a name ('like', 'love', ...)
// in aria-label, and aria-pressed=true on a reaction you've already sent.
static async Task<List<(string Emoji, string Label, bool Mine)>> ScrapeReactionChoicesAsync(IPage page)
{
    var json = await page.EvaluateAsync<string>(
        @"() => JSON.stringify(Array.from(document.querySelectorAll('button[data-e2e-message-reaction]')).map(b => ({
            emoji: b.getAttribute('data-e2e-message-reaction') || '',
            label: b.getAttribute('aria-label') || '',
            mine: b.getAttribute('aria-pressed') === 'true'
        })))");

    var list = new List<(string, string, bool)>();
    using var doc = JsonDocument.Parse(json);
    foreach (var e in doc.RootElement.EnumerateArray())
    {
        var emoji = e.GetProperty("emoji").GetString() ?? "";
        if (emoji.Length == 0) continue;
        list.Add((emoji, e.GetProperty("label").GetString() ?? "", e.GetProperty("mine").GetBoolean()));
    }
    return list;
}

// Full quote-reply flow: pick a message, click its hover Reply button (which puts
// the compose box into reply mode with the quoted message above it), then type
// and send. Quote-reply is an RCS feature — SMS bubbles have no Reply button.
static async Task QuoteReplyFlowAsync(IPage page, string name, List<SmsMessage> messages)
{
    var msg = PickThreadMessage(name, messages, "Reply to which message?");
    if (msg == null) return;

    var entered = await AnsiConsole.Status().StartAsync("Opening reply...", async _ =>
    {
        var wrapper = await HoverMessageActionsAsync(page, msg.Index);
        if (wrapper == null) return false;
        try
        {
            var replyBtn = wrapper.Locator("button[data-e2e-reply-to-message-button]");
            if (await replyBtn.CountAsync() == 0) return false; // SMS bubble — no quote-reply
            await replyBtn.First.ClickAsync(new LocatorClickOptions { Timeout = 5000 });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    });

    if (!entered)
    {
        await page.Keyboard.PressAsync("Escape");
        AnsiConsole.MarkupLine(
            "[red]Reply isn't available for that message — quote-replies need RCS " +
            "(SMS threads don't support them), or the page layout may have changed.[/]\n");
        PauseForKey();
        return;
    }

    var snippet = msg.Text.ReplaceLineEndings(" ");
    if (snippet.Length > 40) snippet = snippet[..37] + "...";
    var reply = PromptReplyLine(
        $"[green]Reply to \"{Markup.Escape(snippet)}\"[/] [grey](leave blank to cancel):[/]");
    if (reply.Length == 0)
    {
        await page.Keyboard.PressAsync("Escape"); // leave reply mode
        return;
    }

    var sent = await AnsiConsole.Status().StartAsync("Sending...",
        async _ => await TrySendReplyAsync(page, reply));
    if (sent)
    {
        await page.WaitForTimeoutAsync(1500); // let the sent message render
    }
    else
    {
        await page.Keyboard.PressAsync("Escape");
        AnsiConsole.MarkupLine(
            "[red]Could not find the compose box — the page layout may have changed. " +
            "Nothing was sent.[/]\n");
        PauseForKey();
    }
}

// Settings menu: view and edit each config.txt section in-app, or open the
// whole file in Notepad for free-form editing.
static void ShowSettings()
{
    var lastIdx = 0;
    while (true)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold blue]Settings[/] [grey]— stored in config.txt next to the app[/]");

        var options = Config.Sections.Select(s =>
        {
            var count = Config.Lines(s.Name).Length;
            var status = count == 0 ? "[grey]not set[/]" : $"[green]{count} line{(count == 1 ? "" : "s")}[/]";
            return $"[bold]{s.Title}[/]  {status}";
        }).ToList();
        const string openFile = "Open config.txt in Notepad";
        options.Add(openFile);
        options.Add("<= Back to Main Menu");

        var idx = PromptMenu("Pick a setting:", options, 15, initialSelected: lastIdx);
        if (idx < 0 || idx == options.Count - 1)
        {
            AnsiConsole.Clear();
            return;
        }
        lastIdx = idx;

        if (options[idx] == openFile)
        {
            try
            {
                Process.Start("notepad.exe", Config.FilePath);
                AnsiConsole.MarkupLine("[grey]Opened in Notepad. Edits are picked up when you save the file.[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Could not open Notepad:[/] {Markup.Escape(ex.Message)}");
            }
            PauseForKey();
            continue;
        }

        EditConfigSection(Config.Sections[idx]);
    }
}

// Line-based editor for one config section — the sections are all short
// line-oriented lists, so add/edit/delete-a-line covers everything.
static void EditConfigSection(Config.Section section)
{
    while (true)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine($"[bold blue]{Markup.Escape(section.Title)}[/] [grey]— [[{section.Name}]] in config.txt[/]");
        foreach (var h in section.Help)
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(h)}[/]");
        if (section.ReloadNote)
            AnsiConsole.MarkupLine("[yellow]Changes to this setting take effect the next time the app starts.[/]");
        AnsiConsole.WriteLine();

        var lines = Config.Lines(section.Name).ToList();
        if (lines.Count == 0)
            AnsiConsole.MarkupLine("[grey](not set)[/]");
        for (var i = 0; i < lines.Count; i++)
            AnsiConsole.MarkupLine($"[grey]{i + 1}.[/] {Markup.Escape(lines[i])}");
        AnsiConsole.WriteLine();

        const string add = "Add a line";
        const string edit = "Edit a line";
        const string del = "Delete a line";
        const string clear = "Clear this setting";
        const string back = "<= Back to Settings";
        var options = new List<string> { add };
        if (lines.Count > 0) options.AddRange([edit, del, clear]);
        options.Add(back);

        var idx = PromptMenu("Action:", options, 15);
        if (idx < 0 || options[idx] == back) return;

        if (options[idx] == add)
        {
            var value = AnsiConsole.Prompt(
                new TextPrompt<string>("[green]New line[/] [grey](leave blank to cancel):[/]").AllowEmpty()).Trim();
            if (value.Length > 0)
            {
                lines.Add(value);
                Config.SetLines(section.Name, lines);
            }
        }
        else if (options[idx] == edit || options[idx] == del)
        {
            var deleting = options[idx] == del;
            var lineOptions = lines.Select(Markup.Escape).ToList();
            lineOptions.Add("<= Cancel");
            var pick = PromptMenu(deleting ? "Delete which line?" : "Edit which line?", lineOptions, 15);
            if (pick < 0 || pick >= lines.Count) continue;

            if (deleting)
            {
                lines.RemoveAt(pick);
            }
            else
            {
                var value = AnsiConsole.Prompt(
                    new TextPrompt<string>("[green]New value[/] [grey](leave blank to keep current):[/]").AllowEmpty()).Trim();
                if (value.Length == 0) continue;
                lines[pick] = value;
            }
            Config.SetLines(section.Name, lines);
        }
        else if (options[idx] == clear)
        {
            if (AnsiConsole.Confirm($"Clear all lines in {Markup.Escape(section.Title)}?", defaultValue: false))
                Config.SetLines(section.Name, []);
        }
    }
}

// Loads the email-newsletter list from the [newsletters] section of config.txt;
// entries there replace the built-in defaults. One newsletter per line:
//   Label | text the From address must contain | text the Subject must contain (optional)
static List<EmailNewsletter> LoadEmailNewsletters()
{
    var list = Config.Lines("newsletters")
        .Select(l => l.Split('|'))
        .Where(parts => parts.Length >= 2 && parts[0].Trim().Length > 0 && parts[1].Trim().Length > 0)
        .Select(parts => new EmailNewsletter(
            parts[0].Trim(),
            parts[1].Trim(),
            parts.Length >= 3 && parts[2].Trim().Length > 0 ? parts[2].Trim() : null))
        .ToList();
    if (list.Count > 0) return list;

    return
    [
        new EmailNewsletter("NYT: The Morning (from your Gmail)", "nytdirect@nytimes.com", "The Morning"),
        new EmailNewsletter("STLPR: The Gateway (from your Gmail)", "stlpublicradio", null),
    ];
}

// All Gmail accounts from the [gmail-imap] section of config.txt. Two lines
// per account (address, then app password), or a single "address | password"
// line per account — both forms can be mixed.
static List<(string Email, string AppPassword)> LoadGmailAccounts()
{
    var accounts = new List<(string, string)>();
    string? pendingEmail = null;
    foreach (var line in Config.Lines("gmail-imap"))
    {
        var parts = line.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && pendingEmail == null)
        {
            accounts.Add((parts[0], parts[1].Replace(" ", "")));
        }
        else if (pendingEmail == null)
        {
            pendingEmail = line;
        }
        else
        {
            accounts.Add((pendingEmail, line.Replace(" ", "")));
            pendingEmail = null;
        }
    }
    return accounts;
}

// A channel counts as unread the same way the Discord client shows it: it has
// messages newer than the last one the account acked, and isn't muted.
static bool DiscordUnread(DiscordState state, DiscordChannel ch) =>
    !ch.Muted && ch.LastMessageId > 0 &&
    (!state.ReadStates.TryGetValue(ch.Id, out var rs) || rs.LastAcked < ch.LastMessageId);

// Discord section: servers -> channels (with unread/mention badges) -> messages.
// All state comes from one gateway snapshot per visit (see DiscordApi); marking
// a channel read updates both Discord and the local snapshot.
static async Task ShowDiscordAsync()
{
    if (DiscordApi.Token == null)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine(
            "[yellow]No Discord token configured.[/]\n\n" +
            "[grey]Add your user token under Settings > Discord (instructions are shown there).\n" +
            "Heads up: Discord's terms forbid automating a user account. This section only\n" +
            "reads messages, marks channels seen, and posts what you type — but use it\n" +
            "at your own discretion.[/]\n");
        PauseForKey();
        return;
    }

    async Task<DiscordState?> ConnectAsync(string label)
    {
        try
        {
            return await AnsiConsole.Status().StartAsync(label,
                async _ => await DiscordApi.FetchStateAsync());
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Discord error:[/] {Markup.Escape(ex.Message)}\n");
            PauseForKey();
            return null;
        }
    }

    var state = await ConnectAsync("Connecting to Discord...");
    if (state == null) return;

    var lastIdx = 0;
    const string refresh = "== Refresh ==";
    while (true)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold blue]Discord[/] [grey]— servers[/]");

        var options = state.Guilds.Select(g =>
        {
            var unread = g.Channels.Count(c => DiscordUnread(state, c));
            var mentions = g.Channels.Sum(c => state.ReadStates.TryGetValue(c.Id, out var rs) ? rs.Mentions : 0);
            var badge = (unread > 0 ? $"  [cyan]{unread} unread[/]" : "") +
                        (mentions > 0 ? $"  [red]@{mentions}[/]" : "");
            return $"{(unread > 0 || mentions > 0 ? "[bold cyan]●[/] " : "  ")}[bold]{Markup.Escape(g.Name)}[/]{badge}";
        }).ToList();
        options.Add(refresh);
        options.Add("<= Back to Main Menu");

        var idx = PromptMenu("Select a server:", options, 15, initialSelected: lastIdx);
        if (idx < 0 || idx == options.Count - 1) break;

        if (options[idx] == refresh)
        {
            state = await ConnectAsync("Refreshing...") ?? state;
            continue;
        }

        lastIdx = idx;
        await ShowDiscordChannelsAsync(state, state.Guilds[idx]);
    }
    AnsiConsole.Clear();
}

static async Task ShowDiscordChannelsAsync(DiscordState state, DiscordGuild guild)
{
    // DM "channels" are people, not #channels.
    var prefix = guild.Id.Length == 0 ? "" : "#";

    var lastIdx = 0;
    while (true)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine($"[bold blue]{Markup.Escape(guild.Name)}[/] [grey]— channels[/]");
        if (guild.Channels.Count == 0)
            AnsiConsole.MarkupLine("[yellow]No text channels visible here.[/]");

        var options = guild.Channels.Select(c =>
        {
            var unread = DiscordUnread(state, c);
            var mentions = state.ReadStates.TryGetValue(c.Id, out var rs) ? rs.Mentions : 0;
            var name = c.Muted
                ? $"[grey]{prefix}{Markup.Escape(c.Name)} (muted)[/]"
                : unread ? $"[bold]{prefix}{Markup.Escape(c.Name)}[/]" : $"{prefix}{Markup.Escape(c.Name)}";
            return $"{(unread ? "[bold cyan]●[/] " : "  ")}{name}" +
                   (mentions > 0 ? $" [red]@{mentions}[/]" : "") +
                   (c.Category.Length > 0 ? $"  [grey]{Markup.Escape(c.Category)}[/]" : "");
        }).ToList();
        options.Add("<= Back to Servers");

        var idx = PromptMenu("Select a channel:", options, 15, initialSelected: lastIdx);
        if (idx < 0 || idx == options.Count - 1) return;
        lastIdx = idx;
        await ShowDiscordChannelAsync(state, guild, guild.Channels[idx]);
    }
}

static async Task ShowDiscordChannelAsync(DiscordState state, DiscordGuild guild, DiscordChannel ch)
{
    var lastAcked = state.ReadStates.TryGetValue(ch.Id, out var rs) ? rs.LastAcked : 0;

    async Task<List<DiscordMessage>?> FetchAsync(string label, ulong before = 0)
    {
        try
        {
            return await AnsiConsole.Status().StartAsync(label,
                async _ => await DiscordApi.GetMessagesAsync(ch.Id, before));
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Discord error:[/] {Markup.Escape(ex.Message)}\n");
            PauseForKey();
            return null;
        }
    }

    var messages = await FetchAsync("Loading messages...");
    if (messages == null) return;

    while (true)
    {
        // The marker doubles as the pager's scroll-to target; the box-drawing
        // dashes keep it from colliding with ordinary chat text.
        const string divider = "────────────  NEW MESSAGES  ────────────";
        var sb = new StringBuilder();
        var links = new List<(string Label, string Url)>();
        var dividerShown = false;

        if (messages.Count == 0) sb.Append("[grey]No messages here yet.[/]\n");
        foreach (var m in messages)
        {
            if (!dividerShown && m.Id > lastAcked)
            {
                sb.Append($"[red]{divider}[/]\n\n");
                dividerShown = true;
            }
            sb.Append($"[bold cyan]{Markup.Escape(m.Author)}[/]  [grey]{m.Timestamp:MMM d, h:mm tt}[/]\n");
            sb.Append(Markup.Escape(m.Text)).Append("\n\n");
            links.AddRange(m.Links);
        }

        var title = guild.Id.Length == 0 ? ch.Name : $"#{ch.Name} — {guild.Name}";
        var panel = new Panel(new Markup(sb.ToString().TrimEnd('\n')))
        {
            Header = new PanelHeader($" {Markup.Escape(title)} "),
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 0, 1, 0),
            Expand = true
        };

        var actions = new List<(ConsoleKey Key, string Hint)>
        {
            (ConsoleKey.R, "R post"), (ConsoleKey.L, "L older")
        };
        if (dividerShown) actions.Add((ConsoleKey.M, "M mark read"));

        var key = ShowInPager(panel, links, actions.ToArray(),
            startAtEnd: !dividerShown, startAtText: dividerShown ? "NEW MESSAGES" : null);

        if (key == ConsoleKey.R)
        {
            var post = PromptReplyLine(
                $"[green]Post in {Markup.Escape(title)}[/] [grey](leave blank to cancel):[/]");
            if (post.Length == 0) continue;
            try
            {
                var postedId = await AnsiConsole.Status().StartAsync("Posting...",
                    async _ => await DiscordApi.SendMessageAsync(ch.Id, post));

                // Match the official client: your own post acks the channel.
                if (postedId > 0)
                {
                    try { await DiscordApi.AckAsync(ch.Id, postedId); }
                    catch (Exception ex) { AppLog.Debug("discord ack after post", ex); }
                    lastAcked = postedId;
                    state.ReadStates[ch.Id] = (lastAcked, 0);
                }
                // Re-fetch so the new post (and anything that arrived since) shows.
                messages = await FetchAsync("Refreshing...") ?? messages;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Couldn't post:[/] {Markup.Escape(ex.Message)}\n");
                PauseForKey();
            }
            continue;
        }

        if (key == ConsoleKey.L && messages.Count > 0)
        {
            var older = await FetchAsync("Loading older messages...", before: messages[0].Id);
            if (older is { Count: > 0 }) messages.InsertRange(0, older);
            continue;
        }

        if (key == ConsoleKey.M && messages.Count > 0)
        {
            try
            {
                await AnsiConsole.Status().StartAsync("Marking read...",
                    async _ => await DiscordApi.AckAsync(ch.Id, messages[^1].Id));
                lastAcked = messages[^1].Id;
                state.ReadStates[ch.Id] = (lastAcked, 0);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Couldn't mark read:[/] {Markup.Escape(ex.Message)}\n");
                PauseForKey();
            }
            continue;
        }

        return;
    }
}

// The primary Gmail account (the first configured one) — used for newsletter
// lookups and anywhere a single account is enough.
static (string Email, string AppPassword)? LoadGmailCredentials()
{
    var accounts = LoadGmailAccounts();
    return accounts.Count > 0 ? accounts[0] : null;
}

// Reads NYT articles through a real, signed-in browser session — the only way past
// NYT's DataDome bot-block. The user connects once (plain Chrome, they sign in);
// after that a single headless context is reused for the whole session to read
// article bodies. Profile lives in nyt-profile next to the app.
static class NytBrowser
{
    static readonly string ProfileDir = Path.Combine(AppContext.BaseDirectory, "nyt-profile");
    static IPlaywright? _pw;
    static IBrowserContext? _ctx;
    static IPage? _page;
    static readonly SemaphoreSlim _gate = new(1, 1);

    // NYT's games-state API silently drops saves that arrive in a sub-second burst
    // (only the first of a burst is applied — the rest return 201 but do nothing).
    // Space every state write ≥2.5s apart so each one actually persists.
    static DateTime _lastStatePostUtc = DateTime.MinValue;
    static async Task ThrottleStateWriteAsync()
    {
        var since = DateTime.UtcNow - _lastStatePostUtc;
        var minGap = TimeSpan.FromSeconds(2.5);
        if (since < minGap) await Task.Delay(minGap - since);
        _lastStatePostUtc = DateTime.UtcNow;
    }

    // Runs a state-write POST with throttling and one retry: if the save is dropped
    // (a burst-limited no-op, or a cold-start hiccup), wait and try once more, so a
    // transient failure doesn't surface as "sync failing". `js` returns true on a
    // real save (player populated).
    static async Task<bool> PostStateWithRetryAsync(IPage page, string js, object payload)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await ThrottleStateWriteAsync();
            try { if (await page.EvaluateAsync<bool>(js, payload)) return true; }
            catch { /* navigation/eval hiccup — retry once */ }
        }
        return false;
    }

    public static bool IsConnected => Directory.Exists(ProfileDir);

    public static bool IsNytUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Host.Equals("nytimes.com", StringComparison.OrdinalIgnoreCase) ||
         u.Host.EndsWith(".nytimes.com", StringComparison.OrdinalIgnoreCase));

    // Reads an article, returning (title, body) or null if blocked/unreadable so the
    // caller can fall back. Serialized: one shared page handles reads in turn.
    public static async Task<(string Title, string Text)?> TryReadAsync(string url)
    {
        await _gate.WaitAsync();
        try
        {
            IPage? page;
            try { page = await EnsurePageAsync(); }
            catch (Exception ex) { AppLog.Debug("returned null", ex); return null; } // profile locked / browser launch failed → fall back
            if (page == null) return null;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await page.GotoAsync(url,
                        new PageGotoOptions { Timeout = 25000, WaitUntil = WaitUntilState.DOMContentLoaded });
                    await page.WaitForTimeoutAsync(2500);

                    var title = (await page.TitleAsync()).Trim();
                    if (title.Equals("nytimes.com", StringComparison.OrdinalIgnoreCase) || title.Length == 0)
                        return null; // DataDome block shell

                    var paras = await page.EvaluateAsync<string[]>(
                        "() => Array.from(document.querySelectorAll('article p, section[name=articleBody] p'))" +
                        ".map(p => p.innerText.trim()).filter(t => t.length > 40)");
                    if (paras.Length == 0) return null;

                    var sb = new StringBuilder();
                    foreach (var p in paras)
                    {
                        sb.AppendLine(p.Replace("[", "[[").Replace("]", "]]"));
                        sb.AppendLine();
                    }

                    // Trim NYT's " - The New York Times" title suffix.
                    var dash = title.LastIndexOf(" - The New York Times", StringComparison.Ordinal);
                    if (dash > 0) title = title[..dash];
                    return (WebUtility.HtmlDecode(title), sb.ToString().Trim());
                }
                catch (Exception)
                {
                    // Retry once (NYT reads occasionally time out); then give up → fallback.
                }
            }
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    static async Task<IPage?> EnsurePageAsync()
    {
        if (_page != null) return _page;
        if (!IsConnected) return null;

        _pw ??= await Playwright.CreateAsync();

        // A plain Chrome from the connect step can linger in the background and hold
        // the profile lock right after connecting, which fails the launch. Kill any
        // lock-holder and retry, so the first read/sync self-heals that race.
        for (var attempt = 0; ; attempt++)
        {
            KillProfileChrome();
            if (attempt > 0) await Task.Delay(1200); // let the OS release the lock
            try
            {
                _ctx = await _pw.Chromium.LaunchPersistentContextAsync(ProfileDir,
                    new BrowserTypeLaunchPersistentContextOptions
                    {
                        // Headless — no visible window. The game-state sync uses the
                        // /svc/ JSON API, which isn't DataDome-gated, so headless is
                        // fully reliable there. (Article-body reads are more often
                        // blocked headless, but those are best-effort and fall back.)
                        Headless = true,
                        Channel = ChromeExe().Contains("chrome.exe", StringComparison.OrdinalIgnoreCase) ? "chrome" : "msedge",
                        ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
                        Args = ["--disable-blink-features=AutomationControlled"],
                        IgnoreDefaultArgs = ["--enable-automation"],
                    });
                break;
            }
            catch when (attempt < 2)
            {
                // profile still locked — kill again, wait, retry
            }
        }
        _page = _ctx!.Pages.FirstOrDefault() ?? await _ctx.NewPageAsync();
        return _page;
    }

    // Reads the user's saved Spelling Bee found-words from NYT (null on any failure).
    public static async Task<HashSet<string>?> GetSpellingBeeFoundAsync(string puzzleId)
    {
        await _gate.WaitAsync();
        try
        {
            IPage? page;
            try { page = await EnsurePageAsync(); } catch (Exception ex) { AppLog.Debug("returned null", ex); return null; }
            if (page == null) return null;
            await EnsureNytOriginAsync(page);

            var json = await page.EvaluateAsync<string>(@"async (id) => {
                try {
                    const r = await fetch('https://www.nytimes.com/svc/games/state/spelling_bee/latest?puzzle_id=' + id, { credentials: 'include' });
                    if (!r.ok) return '';
                    const j = await r.json();
                    return JSON.stringify((j.game_data && j.game_data.answers) || []);
                } catch (e) { return ''; }
            }", puzzleId);

            if (string.IsNullOrEmpty(json)) return null;
            var arr = JsonSerializer.Deserialize<string[]>(json) ?? [];
            return new HashSet<string>(arr, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) { AppLog.Debug("returned null", ex); return null; }
        finally { _gate.Release(); }
    }

    // Pushes the found-words + rank back to NYT (POST /svc/games/state). Matches the
    // real NYT client request: it needs the x-games-* headers plus timestamp/user_id/
    // print_date in the body, or the server 201s but silently discards the write
    // (response "player":null). Returns true only on a real, associated save.
    public static async Task<bool> SaveSpellingBeeFoundAsync(string puzzleId, string printDate,
        IEnumerable<string> words, string rank)
    {
        var wordArr = words.ToArray();
        AppLog.Debug($"bee save: enter id={puzzleId} date={printDate} words={wordArr.Length} connected={IsConnected}");
        await _gate.WaitAsync();
        try
        {
            IPage? page;
            try { page = await EnsurePageAsync(); }
            catch (Exception ex) { AppLog.Debug("bee save: EnsurePage", ex); return false; }
            if (page == null) { AppLog.Debug("bee save: page null (not connected?)"); return false; }
            await EnsureNytOriginAsync(page);

            // Pass the object directly — Playwright serializes it into a JS object.
            // (Serializing to a string here makes `p` a string in the JS, so p.field
            // is undefined and the POST goes out malformed → HTTP 400.)
            var payload = new { puzzle_id = puzzleId, print_date = printDate, words = wordArr, rank };

            // The JS returns a diagnostic string so a debug log shows exactly why a
            // push did or didn't persist ("ok:N", "player-null:N", "http-<status>").
            const string js = @"async (p) => {
                try {
                    const cur = await (await fetch('https://www.nytimes.com/svc/games/state/spelling_bee/latest?puzzle_id=' + p.puzzle_id, { credentials: 'include' })).json();
                    const serverWords = (cur.game_data && cur.game_data.answers) || [];
                    const merged = Array.from(new Set(serverWords.concat(p.words)));
                    const body = JSON.stringify({
                        game: 'spelling_bee',
                        game_data: { answers: merged, isRevealed: false, rank: p.rank, isPlayingArchive: false },
                        puzzle_id: p.puzzle_id, print_date: p.print_date, schema_version: '0.50.0',
                        timestamp: Math.floor(Date.now() / 1000), user_id: cur.user_id
                    });
                    const r = await fetch('https://www.nytimes.com/svc/games/state', {
                        method: 'POST', credentials: 'include',
                        headers: { 'Content-Type': 'application/json', 'x-games-client-time': new Date().toISOString(),
                                   'x-games-user-type': 'sub', 'x-games-save-trigger': 'moogle/forceSave' },
                        body });
                    if (!r.ok) return 'http-' + r.status;
                    const res = await r.json();
                    return (res && res.player != null) ? ('ok:' + merged.length) : ('player-null:merged' + merged.length);
                } catch (e) { return 'err:' + (e && e.message ? e.message : e); }
            }";

            for (var attempt = 0; attempt < 2; attempt++)
            {
                await ThrottleStateWriteAsync();
                string diag;
                try { diag = await page.EvaluateAsync<string>(js, payload); }
                catch (Exception ex) { diag = "eval-threw:" + ex.Message.Split('\n')[0]; }
                AppLog.Debug($"bee save: attempt {attempt + 1} -> {diag}");
                if (diag.StartsWith("ok:")) return true;
            }
            return false;
        }
        catch (Exception ex) { AppLog.Debug("bee save: outer", ex); return false; }
        finally { _gate.Release(); }
    }

    // Reads the user's Wordle state from NYT: the list of non-empty guesses and the
    // status (WIN/FAIL/IN_PROGRESS). Null on any failure.
    public static async Task<(List<string> Guesses, string Status)?> GetWordleStateAsync(string puzzleId)
    {
        await _gate.WaitAsync();
        try
        {
            IPage? page;
            try { page = await EnsurePageAsync(); } catch (Exception ex) { AppLog.Debug("returned null", ex); return null; }
            if (page == null) return null;
            await EnsureNytOriginAsync(page);

            var json = await page.EvaluateAsync<string>(@"async (id) => {
                try {
                    const r = await fetch('https://www.nytimes.com/svc/games/state/wordleV2/latest?puzzle_id=' + id, { credentials: 'include' });
                    if (!r.ok) return '';
                    const j = await r.json();
                    const gd = j.game_data || {};
                    return JSON.stringify({ board: gd.boardState || [], status: gd.status || 'IN_PROGRESS' });
                } catch (e) { return ''; }
            }", puzzleId);

            if (string.IsNullOrEmpty(json)) return null;
            using var doc = JsonDocument.Parse(json);
            var guesses = doc.RootElement.GetProperty("board").EnumerateArray()
                .Select(e => (e.GetString() ?? "").ToLowerInvariant())
                .Where(s => s.Length == 5).ToList();
            var status = doc.RootElement.GetProperty("status").GetString() ?? "IN_PROGRESS";
            return (guesses, status);
        }
        catch (Exception ex) { AppLog.Debug("returned null", ex); return null; }
        finally { _gate.Release(); }
    }

    // Pushes Wordle guesses/status to NYT. Guards against regressing a server game
    // that is further along (more guesses), so it never erases phone progress.
    public static async Task<bool> SaveWordleStateAsync(string puzzleId, string printDate,
        List<string> guesses, string status)
    {
        await _gate.WaitAsync();
        try
        {
            IPage? page;
            try { page = await EnsurePageAsync(); } catch (Exception ex) { AppLog.Debug("EnsurePage", ex); return false; }
            if (page == null) return false;
            await EnsureNytOriginAsync(page);

            // Pad the board to 6 slots as NYT stores it.
            var board = new List<string>(guesses);
            while (board.Count < 6) board.Add("");
            // Pass the object directly (not a serialized string) so p.field works in JS.
            var payload = new
            {
                puzzle_id = puzzleId, print_date = printDate,
                board = board.ToArray(), rowIndex = guesses.Count, status
            };
            return await PostStateWithRetryAsync(page, @"async (p) => {
                try {
                    const cur = await (await fetch('https://www.nytimes.com/svc/games/state/wordleV2/latest?puzzle_id=' + p.puzzle_id, { credentials: 'include' })).json();
                    const serverBoard = (cur.game_data && cur.game_data.boardState) || [];
                    const serverCount = serverBoard.filter(s => s && s.length === 5).length;
                    if (serverCount > p.rowIndex) return true; // server is further along — don't regress
                    const body = JSON.stringify({
                        game: 'wordleV2',
                        game_data: { boardState: p.board, currentRowIndex: p.rowIndex,
                                     hardMode: false, isPlayingArchive: false, status: p.status },
                        puzzle_id: p.puzzle_id, print_date: p.print_date, schema_version: '0.50.0',
                        timestamp: Math.floor(Date.now() / 1000), user_id: cur.user_id
                    });
                    const r = await fetch('https://www.nytimes.com/svc/games/state', {
                        method: 'POST', credentials: 'include',
                        headers: { 'Content-Type': 'application/json',
                                   'x-games-client-time': new Date().toISOString(),
                                   'x-games-user-type': 'sub', 'x-games-save-trigger': 'moogle/forceSave' },
                        body });
                    if (!r.ok) return false;
                    const res = await r.json();
                    return res && res.player != null;
                } catch (e) { return false; }
            }", payload);
        }
        catch (Exception ex) { AppLog.Debug("returned false", ex); return false; }
        finally { _gate.Release(); }
    }

    // Generic games-state read: returns the raw game_data JSON for a game/puzzle, or
    // null. (Spelling Bee/Wordle have their own typed readers; this serves the games
    // whose game_data the caller parses itself — Connections, Strands.)
    public static async Task<string?> GetGameStateAsync(string game, string puzzleId)
    {
        await _gate.WaitAsync();
        try
        {
            IPage? page;
            try { page = await EnsurePageAsync(); } catch (Exception ex) { AppLog.Debug("returned null", ex); return null; }
            if (page == null) return null;
            await EnsureNytOriginAsync(page);

            var json = await page.EvaluateAsync<string>(@"async (a) => {
                try {
                    const r = await fetch('https://www.nytimes.com/svc/games/state/' + a[0] + '/latest?puzzle_id=' + a[1], { credentials: 'include' });
                    if (!r.ok) return '';
                    const j = await r.json();
                    return JSON.stringify(j.game_data || {});
                } catch (e) { return ''; }
            }", new[] { game, puzzleId });
            return string.IsNullOrEmpty(json) ? null : json;
        }
        catch (Exception ex) { AppLog.Debug("returned null", ex); return null; }
        finally { _gate.Release(); }
    }

    // Generic games-state write: POSTs the given game_data (raw JSON object string)
    // with the NYT client headers/envelope. Returns true only on a real save.
    public static async Task<bool> SaveGameStateAsync(string game, string puzzleId, string printDate, string gameDataJson)
    {
        await _gate.WaitAsync();
        try
        {
            IPage? page;
            try { page = await EnsurePageAsync(); } catch (Exception ex) { AppLog.Debug("EnsurePage", ex); return false; }
            if (page == null) return false;
            await EnsureNytOriginAsync(page);

            // Pass the object directly (not a serialized string) so p.field works in JS.
            var payload = new { game, puzzle_id = puzzleId, print_date = printDate, gameData = gameDataJson };
            return await PostStateWithRetryAsync(page, @"async (p) => {
                try {
                    const cur = await (await fetch('https://www.nytimes.com/svc/games/state/' + p.game + '/latest?puzzle_id=' + p.puzzle_id, { credentials: 'include' })).json();
                    const body = JSON.stringify({
                        game: p.game, game_data: JSON.parse(p.gameData),
                        puzzle_id: p.puzzle_id, print_date: p.print_date, schema_version: '0.50.0',
                        timestamp: Math.floor(Date.now() / 1000), user_id: cur.user_id
                    });
                    const r = await fetch('https://www.nytimes.com/svc/games/state', {
                        method: 'POST', credentials: 'include',
                        headers: { 'Content-Type': 'application/json', 'x-games-client-time': new Date().toISOString(),
                                   'x-games-user-type': 'sub', 'x-games-save-trigger': 'moogle/forceSave' },
                        body });
                    if (!r.ok) return false;
                    const res = await r.json();
                    return res && res.player != null;
                } catch (e) { return false; }
            }", payload);
        }
        catch (Exception ex) { AppLog.Debug("returned false", ex); return false; }
        finally { _gate.Release(); }
    }

    // Authenticated GET of a nytimes.com JSON endpoint (e.g. crossword puzzles that
    // 403 without a login). Returns the response body, or null on failure.
    public static async Task<string?> FetchJsonAsync(string url)
    {
        await _gate.WaitAsync();
        try
        {
            IPage? page;
            try { page = await EnsurePageAsync(); } catch (Exception ex) { AppLog.Debug("returned null", ex); return null; }
            if (page == null) return null;
            await EnsureNytOriginAsync(page);

            var json = await page.EvaluateAsync<string>(@"async (u) => {
                try { const r = await fetch(u, { credentials: 'include' }); return r.ok ? await r.text() : ''; }
                catch (e) { return ''; }
            }", url);
            return string.IsNullOrEmpty(json) ? null : json;
        }
        catch (Exception ex) { AppLog.Debug("returned null", ex); return null; }
        finally { _gate.Release(); }
    }

    // Makes sure the shared page is on a nytimes.com origin so same-origin fetches
    // to /svc/ carry the session cookies.
    static async Task EnsureNytOriginAsync(IPage page)
    {
        if (!page.Url.Contains("nytimes.com", StringComparison.OrdinalIgnoreCase))
            await page.GotoAsync("https://www.nytimes.com/puzzles/spelling-bee",
                new PageGotoOptions { Timeout = 30000, WaitUntil = WaitUntilState.DOMContentLoaded });
    }

    // Closes the shared reading context (so a plain-Chrome sign-in window can take
    // over the profile — only one browser may hold a profile at a time).
    public static async Task CloseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_ctx != null) await _ctx.DisposeAsync();
            _ctx = null;
            _page = null;
        }
        catch (Exception ex) { AppLog.Debug("NytBrowser close", ex); }
        finally { _gate.Release(); }
    }

    // Disposes the browser context and Playwright driver at app exit.
    public static async Task ShutdownAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_ctx != null) await _ctx.DisposeAsync();
            _pw?.Dispose();
        }
        catch (Exception ex) { AppLog.Debug("NytBrowser shutdown", ex); }
        finally { _ctx = null; _page = null; _pw = null; _gate.Release(); }
    }

    // One-time setup: opens plain (non-automated) Chrome so the user can sign in.
    // Signing in and simply loading an article warms the profile with both the NYT
    // auth cookie and the DataDome clearance cookie, which headless reads reuse.
    public static async Task ConnectAsync()
    {
        await CloseAsync(); // release the profile if a read context is open

        AnsiConsole.MarkupLine(
            "[yellow]Connect your NYT account[/]\n" +
            "[grey]A regular Chrome window will open at nytimes.com. Sign in with your NYT " +
            "subscriber account, then open any article to confirm it loads for you. " +
            "When you're done, close the browser window to return here.\n" +
            "This is a one-time step — after it, the app reads NYT articles directly in the terminal.[/]\n");
        Pause();

        Directory.CreateDirectory(ProfileDir);
        var proc = Process.Start(new ProcessStartInfo(ChromeExe(),
            $"--user-data-dir=\"{ProfileDir}\" --no-first-run --no-default-browser-check --new-window https://www.nytimes.com/")
        {
            UseShellExecute = false
        });

        AnsiConsole.MarkupLine("[grey]Waiting for you to sign in and close the browser window...[/]");
        if (proc != null) await proc.WaitForExitAsync();

        // Chrome often keeps a background process alive after its window closes; that
        // process holds the profile lock and would block the headless reader. Give it
        // a moment to settle, then terminate anything still bound to this profile.
        await Task.Delay(1500);
        KillProfileChrome();

        // Verify the sign-in actually populated the profile — a Cookies db under
        // Default is the reliable signal. If it's missing, tell the user plainly
        // instead of falsely reporting success.
        var cookiesDb = Path.Combine(ProfileDir, "Default", "Network", "Cookies");
        if (File.Exists(cookiesDb))
            AnsiConsole.MarkupLine("[green]NYT connected.[/] [grey]NYT articles will now open with full text where available.[/]\n");
        else
            AnsiConsole.MarkupLine(
                "[yellow]Hmm — no signed-in session was saved.[/] [grey]The browser may have handed off to an " +
                "already-open Chrome window instead of using its own profile. Try again after fully closing " +
                "Chrome, or tell me and I'll adjust.[/]\n");
        Pause();
    }

    static void Pause()
    {
        AnsiConsole.MarkupLine("[grey]Press any key to continue...[/]");
        Console.ReadKey(intercept: true);
    }

    // Terminates ONLY Chrome/Edge processes whose command line references this app's
    // nyt-profile directory — never the user's own browser. Used to release the
    // profile lock left by a lingering connect-window process.
    static void KillProfileChrome()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'chrome.exe' OR Name = 'msedge.exe'");
            foreach (var o in searcher.Get())
            {
                var cmd = o["CommandLine"] as string;
                if (cmd == null || !cmd.Contains(ProfileDir, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var p = Process.GetProcessById(Convert.ToInt32(o["ProcessId"]));
                    p.Kill();
                }
                catch { /* already gone */ }
            }
        }
        catch
        {
            // WMI unavailable or query failed — the launch will just retry/fall back.
        }
    }

    static string ChromeExe()
    {
        string[] candidates =
        [
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Google\Chrome\Application\chrome.exe"),
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        ];
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("Neither Chrome nor Edge was found on this machine.");
    }
}

