# DailyShell

A personal "morning dashboard" that lives entirely in the terminal: news, email
newsletters, weather, calendar, Google Tasks, email, text messages, Discord,
Webex, Obsidian notes, and the NYT games — one keyboard-driven app built on
.NET and [Spectre.Console](https://spectreconsole.net/).

## Features

**Main menu** — a live header shows current weather conditions and your next
few calendar events (fetched in the background so the menu never waits).

**News & newsletters**
- Preloaded RSS feeds (NYT Top Stories, NYT U.S., BBC, NPR, AP,
  Webster-Kirkwood Times) plus any custom RSS URL. Entering a plain website
  (e.g. `seedsing.com`) scans it for feeds — autodiscovery tags, common feed
  paths, and Squarespace's `?format=rss` — and lets you pick one. After
  reading a custom or discovered feed, the app offers to add it to your
  sources permanently.
- Email newsletters read in full text straight from Gmail over IMAP
  (e.g. NYT's The Morning, STLPR's The Gateway) — configurable.
- Subreddits: type `r/anything` as a custom source. With free Reddit API
  credentials configured, posts include scores and comments.

**Weather** — location search via Open-Meteo geocoding, forecasts from
weather.gov with Open-Meteo as a fallback/supplement.

**Calendar agenda** — any iCal feeds (e.g. Google Calendar's secret iCal
address), merged into one upcoming-events view.

**Google Tasks** — your task lists through the official API: browse tasks
sorted by due date (subtasks nested, dates color-coded), check them off with
a keypress, add new tasks with natural due dates ("tomorrow", "fri", "9/15"),
rename, reschedule, and delete. Dates only — Google's API drops due times in
both directions, so times of day set in Google's apps can't be shown here.

**Email inbox** — unread Gmail across one or more accounts; read, reply,
star/unstar, and archive without leaving the terminal.

**Text messages** — reads Google Messages for Web through an embedded browser.
One-time pairing with your phone, then: browse conversations (unread markers),
read threads, reply, react, and archive.

**Discord** — see your servers and DMs with unread/mention badges, open a
channel to read messages starting from a NEW MESSAGES divider (or the bottom if
you're caught up), load older history, post messages, and mark channels read
(read state syncs back to your other Discord clients). Uses your own account
token; see the caution below.

**Webex** — your Webex spaces and 1:1 chats over the official REST API: browse
spaces with local unread markers and last-activity times, read messages from a
NEW MESSAGES divider, load older history, and post replies. One-time OAuth
sign-in via a free personal integration from
[developer.webex.com](https://developer.webex.com/my-apps); tokens refresh
themselves afterwards. (Webex's public API has no read-state endpoint, so
unread markers are this app's own and don't sync with other Webex clients.)

**Gemini** — chat with Google Gemini through an embedded browser signed into
your own Google account (one-time sign-in window, like the text-message
pairing): browse your real gemini.google.com conversation history, resume a
conversation, or start a new one — using your Google AI Pro plan's limits.
With an API key configured ([AI Studio](https://aistudio.google.com/app/apikey)),
a "Local API-key chats" mode is also available (official API, conversations
saved in `data/gemini/`).

**Obsidian notes** — your Obsidian vault, read straight from disk (no plugin
or API; the vault is auto-detected from Obsidian's settings): recent notes,
folder browsing, full-text search, and notes rendered in the terminal with
headings, task checkboxes, and `[[wikilinks]]` (which `O` follows into the
Obsidian app). Today's daily note doubles as a quick-capture inbox — `A`
appends a timestamped line, creating the note from your daily-note template if
it doesn't exist yet. `E` opens any note in Obsidian itself.

**Time clock** — punch the Paylocity time clock from the terminal (first
pass): Paylocity ends web sessions on browser close, so the app signs in
fresh each visit — via the portal's Single Sign-On flow by default (one-time
browser sign-in to your company's identity provider with "Stay signed in";
later visits are silent, no password stored), or via Paylocity's own login
form with credentials from `config.txt`. It then discovers the punch buttons
on the page and clicks the one you pick after a confirmation, re-reading the
page after every punch so the state shown is real. A "Save page diagnostics"
option captures the portal layout for tuning the selectors. See the cautions
below.

**Games** — the NYT games, playable at the keyboard: Wordle, The Mini/The
Midi/full-size Daily crossword, Connections, Strands, and Spelling Bee.
Connect your NYT account from this menu (embedded browser sign-in) and
progress syncs to it in the background.

## Requirements

- Windows
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Google Chrome or Microsoft Edge installed (used by the text-message and NYT
  sign-in features — no separate browser download needed)

## Running

```bash
dotnet run
```

Or build a standalone binary with `dotnet publish` and run `DailyShell.exe`.

## Configuration

Everything lives in a single `config.txt` next to the exe, split into
`[sections]`. Edit it by hand or from the app's **Settings** menu (each section
shows inline help). Every feature works unconfigured — sections only unlock the
integrations you want.

| Section | Unlocks | What goes in it |
| --- | --- | --- |
| `[gmail-imap]` | Newsletters, email inbox | Gmail address + [app password](https://myaccount.google.com/apppasswords), one or more accounts |
| `[newsletters]` | Custom newsletter list | `Label \| from-address filter \| subject filter (optional)` per line |
| `[news-sources]` | Preloaded News menu feeds | `Name \| RSS URL` per line |
| `[calendar]` | Agenda + menu header | iCal URLs, optionally `Label \| URL` |
| `[google-tasks]` | Google Tasks section | Client ID + secret of an OAuth "Desktop app" client from [console.cloud.google.com](https://console.cloud.google.com) with the Tasks API enabled (steps in Settings); browser sign-in on first use |
| `[reddit-oauth]` | Reddit scores/comments | Client id + secret from a free 'script' app at reddit.com/prefs/apps |
| `[nyt-cookies]` | Full NYT article text | Your nytimes.com Cookie header (or use the in-app "Connect NYT account") |
| `[discord]` | Discord section | Your Discord user token (instructions in Settings) |
| `[webex]` | Webex section | Client ID + secret of a free integration from [developer.webex.com](https://developer.webex.com/my-apps) (redirect URI `http://localhost:8442/webex`, scope `spark:all`); browser sign-in on first use |
| `[gemini]` | Gemini chat | Nothing needed for the browser mode (sign in on first use); optional API key from [AI Studio](https://aistudio.google.com/app/apikey) + `model = ...` for local API chats |
| `[obsidian]` | Obsidian notes | Nothing needed (vaults auto-detect); optionally a vault path or `Label \| path` per line |
| `[paylocity]` | Time clock | `company = ...` (always); SSO companies need nothing else — non-SSO also add `username`/`password` lines; optional `url = ...` |
| `[display]` | Menu header tweaks | `clock`, `weather`, `agenda`, `agenda-items`, `agenda-days` toggles; `agenda-hide-times` hides events starting at listed times/ranges (e.g. `8:00 AM, 12 PM - 1 PM`); `agenda-hide-events` hides events whose title contains a listed name |

Files the app generates (caches, game progress, logs, browser profiles) are
kept in a `data/` subfolder.

## Keyboard

Arrow keys (or `j`/`k`) and Enter everywhere; `←`/`Esc`/`Backspace`/`Q` go
back. In readers: PgUp/PgDn/Space to page, `O` to open links, plus per-view
actions shown in the bottom hint bar (e.g. `R` reply, `A` archive, `M` mark
read).

## Cautions

- **Discord**: the Discord section authenticates with your personal user
  token. Discord's Terms of Service forbid automating a user account; this app
  keeps usage minimal (read messages, send read-receipts, post what you type),
  but use it at your own discretion.
- **Gemini**: the browser mode automates the gemini.google.com web app with
  your signed-in Google session — an unofficial, scraping-based integration
  that can break when Google changes the page, and automating a consumer
  Google service may be against its terms. Use at your own discretion.
- **Paylocity**: the Time clock section signs into the Paylocity web portal on
  every visit (Paylocity's session dies when the browser closes) — via SSO
  with no stored password, or, for non-SSO companies, with credentials kept in
  `config.txt` in plain text. Punches made through it are real timecard
  entries — the app confirms before clicking and re-reads the page after, but
  verify your timecard in Paylocity until you trust it. Scraping-based, so it
  can break when Paylocity changes the page.
- **Credentials** in `config.txt` (app passwords, cookies, tokens) are stored
  in plain text next to the exe — keep the folder private and don't commit it
  anywhere.

## Troubleshooting

- Set `DAILYSHELL_DEBUG=1` (or create `data/debug.on`) to write diagnostics to
  `data/debug.log`.
- If Google Messages scraping breaks, the app saves `data/gmessages-debug.txt`
  and a screenshot to help fix the selectors; delete the `gmessages-profile`
  folder to re-pair from scratch.
- If the Discord server list fails to parse, the raw payload is saved to
  `data/discord-ready-debug.json`.
- If Gemini scraping breaks, the app saves `data/gemini-debug.txt` and a
  screenshot to help fix the selectors; delete the `gemini-profile` folder to
  sign in from scratch.
