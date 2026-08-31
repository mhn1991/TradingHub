using System.Text.Json;
using Networking.Notifications;

// Standalone probe for Telegram delivery. Its whole purpose is to separate "the credentials or
// the group are wrong" from "the agent code is wrong", BEFORE any agent is wired up - so it
// deliberately shares the real TelegramSignalNotifier rather than reimplementing the call.

string? token = Argument(args, "--token") ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
string? chatId = Argument(args, "--chat") ?? Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID");
bool discover = args.Contains("--discover", StringComparer.Ordinal);
bool help = args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal);

if (help)
{
    PrintUsage();
    return 0;
}

if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("No bot token. Pass --token or set TELEGRAM_BOT_TOKEN.");
    Console.Error.WriteLine();
    PrintUsage();
    return 2;
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

// Always confirm the token itself first. getMe failing means the token is wrong, which is a
// different fix from the chat being wrong - and the error Telegram returns for the two is easy
// to confuse when they surface together at send time.
Console.WriteLine($"Bot token: {Mask(token)}");
(bool tokenOk, string botLabel) = await CheckTokenAsync(http, token);
if (!tokenOk)
{
    Console.Error.WriteLine($"  token rejected: {botLabel}");
    Console.Error.WriteLine("  Check the token with @BotFather; it looks like <botid>:<hash>.");
    return 3;
}

Console.WriteLine($"  ok - authenticated as {botLabel}");

if (discover)
    return await DiscoverChatsAsync(http, token);

if (string.IsNullOrWhiteSpace(chatId))
{
    Console.Error.WriteLine("No chat id. Pass --chat, set TELEGRAM_CHAT_ID, or run --discover to find it.");
    return 2;
}

Console.WriteLine($"Chat id:   {chatId}");

var options = new TelegramNotifierOptions
{
    Enabled = true,
    BotToken = token,
    ChatId = chatId,
    // A probe must not be silently swallowed by the dedupe window when run twice in a row.
    DeduplicationMinutes = 0
};

string? failure = null;
using var notifier = new TelegramSignalNotifier(options, http)
{
    OnError = (message, exception) => failure = exception is null ? message : $"{message} ({exception.Message})"
};

var probe = new SignalNotification
{
    Instrument = "METAL:XAU/USD",
    Side = SignalSide.Buy,
    Strategy = "Notification probe",
    DecisionTime = DateTimeOffset.UtcNow,
    Interval = "5m",
    ReferencePrice = 4642.30m,
    StopLossPrice = 4638.32m,
    TakeProfitPrice = 4650.26m,
    Confidence = 72m,
    Reason = "Test message from the TradingHub notification probe. No trade was taken."
};

Console.WriteLine("Sending test message...");
bool delivered = await notifier.NotifyAsync(probe);

if (delivered)
{
    Console.WriteLine("  ok - delivered. Check the group; the message is a sample BUY signal.");
    return 0;
}

Console.Error.WriteLine($"  FAILED: {failure ?? "no detail reported"}");
Console.Error.WriteLine();
Console.Error.WriteLine("Common causes:");
Console.Error.WriteLine("  \"chat not found\"    - wrong chat id, or the bot was never added to the group.");
Console.Error.WriteLine("                        Group ids are NEGATIVE (-100...). Run --discover.");
Console.Error.WriteLine("  \"bot was blocked\"   - the bot is blocked, or was removed from the group.");
Console.Error.WriteLine("  \"not enough rights\" - the bot cannot post; check group permissions.");
return 1;

static async Task<(bool Ok, string Label)> CheckTokenAsync(HttpClient http, string token)
{
    try
    {
        using HttpResponseMessage response = await http.GetAsync($"https://api.telegram.org/bot{token}/getMe");
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        if (!response.IsSuccessStatusCode)
            return (false, Description(document) ?? $"HTTP {(int)response.StatusCode}");
        JsonElement result = document.RootElement.GetProperty("result");
        string username = result.TryGetProperty("username", out JsonElement name) ? name.GetString() ?? "?" : "?";
        return (true, $"@{username}");
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
    {
        return (false, exception.Message);
    }
}

static async Task<int> DiscoverChatsAsync(HttpClient http, string token)
{
    Console.WriteLine();
    Console.WriteLine("Looking for chats the bot can see (getUpdates)...");
    try
    {
        using HttpResponseMessage response = await http.GetAsync($"https://api.telegram.org/bot{token}/getUpdates");
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"  getUpdates failed: {Description(document) ?? $"HTTP {(int)response.StatusCode}"}");
            return 3;
        }

        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonElement update in document.RootElement.GetProperty("result").EnumerateArray())
        {
            foreach (string key in new[] { "message", "channel_post", "my_chat_member" })
            {
                if (!update.TryGetProperty(key, out JsonElement envelope)) continue;
                if (!envelope.TryGetProperty("chat", out JsonElement chat)) continue;
                string id = chat.GetProperty("id").ToString();
                string title = chat.TryGetProperty("title", out JsonElement t) ? t.GetString() ?? "" :
                    chat.TryGetProperty("username", out JsonElement u) ? "@" + u.GetString() : "(direct message)";
                string type = chat.TryGetProperty("type", out JsonElement ty) ? ty.GetString() ?? "?" : "?";
                seen[id] = $"{title} [{type}]";
            }
        }

        if (seen.Count == 0)
        {
            Console.WriteLine("  none found.");
            Console.WriteLine();
            Console.WriteLine("  Telegram only reports chats with RECENT activity the bot can see. Add the bot");
            Console.WriteLine("  to the group, post any message there, then run --discover again.");
            Console.WriteLine("  Note: if the bot has privacy mode on (@BotFather > Group Privacy), it cannot");
            Console.WriteLine("  see ordinary group messages - disable it, or make the bot an admin.");
            return 4;
        }

        Console.WriteLine();
        foreach ((string id, string label) in seen)
            Console.WriteLine($"  {id,-16} {label}");
        Console.WriteLine();
        Console.WriteLine("  Group ids are negative. Re-run with --chat <id> to send a test message.");
        return 0;
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
    {
        Console.Error.WriteLine($"  getUpdates failed: {exception.Message}");
        return 3;
    }
}

static string? Description(JsonDocument document) =>
    document.RootElement.TryGetProperty("description", out JsonElement value) ? value.GetString() : null;

static string? Argument(string[] args, string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

// Never print a full token: this output gets pasted into issues and chat logs.
static string Mask(string token)
{
    int colon = token.IndexOf(':', StringComparison.Ordinal);
    string id = colon > 0 ? token[..colon] : "?";
    return $"{id}:***{(token.Length > 4 ? token[^4..] : string.Empty)}";
}

static void PrintUsage()
{
    Console.WriteLine("""
        Sends one test message through the same TelegramSignalNotifier the agents use.

          dotnet run --project Notifications.Cli -- --discover
          dotnet run --project Notifications.Cli -- --chat -1001234567890

        Options:
          --token <token>   Bot token. Defaults to $TELEGRAM_BOT_TOKEN.
          --chat  <id>      Target chat. Defaults to $TELEGRAM_CHAT_ID. Group ids are negative.
          --discover        List chats the bot can see, to find the group id.
          --help, -h        This text.

        Prefer the environment variables: an argument lands in your shell history.

          export TELEGRAM_BOT_TOKEN='123456:AA...'

        Exit codes: 0 ok, 1 send failed, 2 bad usage, 3 token/API error, 4 no chats found.
        """);
}
