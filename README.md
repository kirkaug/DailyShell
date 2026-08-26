# DailyShell

A personal "morning dashboard" that lives entirely in the terminal: news, email
newsletters, weather, calendar, email, text messages, Discord, and the NYT
games — one keyboard-driven app built on .NET and
[Spectre.Console](https://spectreconsole.net/).

## Features

**Main menu** — a live header shows current weather conditions and your next
few calendar events (fetched in the background so the menu never waits).

**News & newsletters**
- Preloaded RSS feeds (NYT Top Stories, NYT U.S., BBC, NPR, AP,
  Webster-Kirkwood Times) plus any custom RSS URL.
- Email newsletters read in full text straight from Gmail over IMAP
  (e.g. NYT's The Morning, STLPR's The Gateway) — configurable.
- Subreddits: type `r/anything` as a custom source. With free Reddit API
  credentials configured, posts include scores and comments.
- Connect your NYT account (embedded browser sign-in) to read full subscriber
  articles in the terminal.

**Weather** — location search via Open-Meteo geocoding, forecasts from
weather.gov with Open-Meteo as a fallback/supplement.

**Calendar agenda** — any iCal feeds (e.g. Google Calendar's secret iCal
address), merged into one upcoming-events view.

**Email inbox** — unread Gmail across one or more accounts; read, reply, and
archive without leaving the terminal.

**Text messages** — reads Google Messages for Web through an embedded browser.
One-time pairing with your phone, then: browse conversations (unread markers),
read threads, reply, react, and archive.

**Discord** — see your servers and DMs with unread/mention badges, open a
channel to read messages starting from a NEW MESSAGES divider (or the bottom if
you're caught up), load older history, and mark channels read (syncs back to
your other Discord clients). Uses your own account token; see the caution
below.

**Games** — the NYT games, playable at the keyboard: Wordle, The Mini/The
Midi/full-size Daily crossword, Connections, Strands, and Spelling Bee.
Progress syncs to your NYT account in the background when connected.

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
| `[calendar]` | Agenda + menu header | iCal URLs, optionally `Label \| URL` |
| `[reddit-oauth]` | Reddit scores/comments | Client id + secret from a free 'script' app at reddit.com/prefs/apps |
| `[nyt-cookies]` | Full NYT article text | Your nytimes.com Cookie header (or use the in-app "Connect NYT account") |
| `[discord]` | Discord section | Your Discord user token (instructions in Settings) |
| `[display]` | Menu header tweaks | `weather`, `agenda`, `agenda-items`, `agenda-days` toggles |

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
  keeps usage minimal (read messages, send read-receipts), but use it at your
  own discretion.
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
