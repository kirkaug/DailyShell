// Data models shared across features.

// An email newsletter readable from Gmail: menu label plus IMAP search filters.
record EmailNewsletter(string Label, string FromContains, string? SubjectContains);

// One subreddit post from Reddit's JSON API.
record RedditPost(string Title, string Author, int Score, int NumComments,
                  string Permalink, string Url, string SelfText, bool IsSelf, DateTimeOffset Created);

// A single reddit comment (Score is -1 when the RSS fallback was used).
record RedditComment(string Author, int Score, string Body);

// A geocoded weather location.
record WeatherPlace(string Display, double Lat, double Lon);

// A day's Spelling Bee puzzle: the hive letters plus the full answer/pangram lists.
record SpellingBeePuzzle(string CenterLetter, string[] OuterLetters, string[] ValidLetters,
    List<string> Answers, List<string> Pangrams, string DisplayDate, string PrintDate, string Id);

// A day's Wordle: the solution plus identifying date/id.
record WordlePuzzle(string Solution, string PrintDate, string Id);

// A crossword cell: black squares have IsBlack=true and no answer.
class CrossCell
{
    public bool IsBlack;
    public string Answer = "";   // uppercase single letter (or rebus, rare)
    public string Label = "";    // clue number shown in the cell, if any
    public int AcrossClue = -1;  // index into the clue list, or -1
    public int DownClue = -1;
}

record CrossClue(string Direction, string Label, string Text, int[] Cells);

record Crossword(string Id, string PrintDate, string Title, int Width, int Height,
    CrossCell[] Cells, List<CrossClue> Clues);

// Connections: 4 categories of 4 words each; Words[pos] gives the 4x4 layout.
record ConnectionsCategory(string Title, string[] Words);
record ConnectionsPuzzle(string Id, string PrintDate, string[] WordByPosition, List<ConnectionsCategory> Categories);

// Strands: theme clue, board, theme words + spangram, and each word's cell path.
record StrandsPuzzle(string Id, string PrintDate, string Clue, string Spangram, List<string> ThemeWords,
    string[] Board, Dictionary<string, List<(int Row, int Col)>> Coords);

// One conversation row scraped from Google Messages for Web.
record SmsConversation(int Index, string Name, string Snippet, bool Unread);

// One message bubble in an open Google Messages thread. Index is the message's
// position among the thread's mws-message-wrapper elements, used to target
// per-message hover actions (react, reply).
record SmsMessage(int Index, bool Incoming, string Text);

// Discord: everything one gateway READY snapshot gives the UI. ReadStates maps
// channel id -> (last message id the user has acked, unread mention count) and
// is updated locally when a channel is marked read from the app.
class DiscordState
{
    public List<DiscordGuild> Guilds = [];
    public Dictionary<string, (ulong LastAcked, int Mentions)> ReadStates = [];
}

// One server; Channels is pre-filtered to readable text channels in sidebar
// order. Id is "" for the synthetic "Direct messages" entry.
record DiscordGuild(string Id, string Name, List<DiscordChannel> Channels);

// One text channel (or DM conversation). LastMessageId is 0 when the channel
// has never had a message; comparing it to the acked id gives unread state.
record DiscordChannel(string Id, string Name, string Category, ulong LastMessageId, bool Muted);

// One chat message, flattened for terminal display (reply context, attachment
// and embed summaries are folded into Text; their URLs go to Links).
record DiscordMessage(ulong Id, string Author, DateTimeOffset Timestamp, string Text,
    List<(string Label, string Url)> Links);
