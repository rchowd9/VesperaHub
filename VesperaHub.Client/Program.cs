using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace VesperaHub.Client;

/// <summary>
/// Console client for Vespera Hub.
/// Usage: dotnet run --project VesperaHub.Client [-- https://server:port]
/// </summary>
class Program
{
    private const string DefaultServerUrl = "https://localhost:5001";
    private static readonly object ConsoleLock = new();
    private static string _me = "";
    private static string _room = "";

    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        string serverUrl = ResolveServerUrl(args);
        using var http = new HttpClient { BaseAddress = new Uri(serverUrl) };

        Console.WriteLine($"Vespera Hub console client — {serverUrl}");
        var auth = await AuthenticateAsync(http);
        if (auth is null) return 1;

        _me = auth.Username;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

        var connection = new HubConnectionBuilder()
            .WithUrl($"{serverUrl}/chathub", o => o.AccessTokenProvider = () => Task.FromResult<string?>(auth.Token))
            .WithAutomaticReconnect()
            .Build();

        connection.On<MessageDto>("MessageReceived", m =>
        {
            if (m.Recipient is not null)
            {
                string label = IsMe(m.Sender) ? $"you → {m.Recipient}" : $"{m.Sender} → you";
                PrintMessage(m, $"[DM] {label}", ConsoleColor.Magenta);
            }
            else if (string.Equals(m.Room, _room, StringComparison.OrdinalIgnoreCase))
            {
                PrintMessage(m, m.Sender, ConsoleColor.Cyan);
            }
            else if (!IsMe(m.Sender) && m.Content.Contains($"@{_me}", StringComparison.OrdinalIgnoreCase))
            {
                PrintInfo($"{m.Sender} mentioned you in #{m.Room}: {m.Content}");
            }
        });
        connection.On<MessageDto>("MessageUpdated", m =>
        {
            bool visible = m.Recipient is not null || string.Equals(m.Room, _room, StringComparison.OrdinalIgnoreCase);
            if (!visible) return;
            if (m.Deleted)
                PrintInfo($"{m.Sender} deleted a message.");
            else if (m.EditedAt is { } edited && DateTime.UtcNow - edited < TimeSpan.FromSeconds(10))
                PrintInfo($"{m.Sender} edited a message: {m.Content}");
        });
        connection.On<string, List<string>>("PresenceChanged", (room, users) =>
        {
            if (string.Equals(room, _room, StringComparison.OrdinalIgnoreCase))
                PrintInfo($"Viewing #{room}: {string.Join(", ", users)}");
        });
        connection.On<RoomDto>("RoomCreated", r => PrintInfo($"New room created: #{r.Name}"));
        connection.Reconnecting += _ => { PrintInfo("Connection lost, reconnecting…"); return Task.CompletedTask; };
        connection.Reconnected += async _ =>
        {
            PrintInfo("Reconnected.");
            if (_room != "") await JoinAsync(connection, _room);
        };

        try
        {
            await connection.StartAsync();
        }
        catch (Exception ex)
        {
            PrintError($"Couldn't connect to the hub: {ex.Message}");
            return 1;
        }

        PrintInfo($"Connected as {_me}. Type /help for commands.");
        try
        {
            await JoinAsync(connection, "general");
        }
        catch (HubException ex) when (ex.Message.Contains("Method does not exist") || ex.Message.Contains("arguments"))
        {
            PrintError("The server is running a different version of Vespera Hub. Stop it, then start it again with: dotnet run --project VesperaHub.Server");
            await connection.StopAsync();
            return 1;
        }

        while (true)
        {
            string? input = Console.ReadLine();
            if (input is null) break;
            input = input.Trim();
            if (input.Length == 0) continue;

            try
            {
                if (!input.StartsWith('/'))
                {
                    await SendAsync(connection, $"room:{_room}", input);
                    continue;
                }

                string[] parts = input.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                switch (parts[0].ToLowerInvariant())
                {
                    case "/quit":
                    case "/exit":
                        await connection.StopAsync();
                        return 0;

                    case "/help":
                        PrintHelp();
                        break;

                    case "/rooms":
                        var rooms = await http.GetFromJsonAsync<List<RoomDto>>("/api/rooms") ?? [];
                        foreach (var r in rooms)
                            PrintInfo($"#{r.Name,-16} {r.Online} viewing   {r.Description}");
                        break;

                    case "/join" when parts.Length >= 2:
                        await JoinAsync(connection, parts[1].ToLowerInvariant());
                        break;

                    case "/create" when parts.Length >= 2:
                        var response = await http.PostAsJsonAsync("/api/rooms", new { Name = parts[1], Description = parts.ElementAtOrDefault(2) });
                        if (response.IsSuccessStatusCode)
                            await JoinAsync(connection, parts[1].ToLowerInvariant());
                        else
                            PrintError(await ReadErrorAsync(response));
                        break;

                    case "/dm" when parts.Length == 3:
                        await SendAsync(connection, $"dm:{parts[1]}", parts[2]);
                        break;

                    case "/history" when parts.Length >= 2:
                        var page = await connection.InvokeAsync<HistoryPage>("GetHistory", $"dm:{parts[1]}", null);
                        PrintInfo($"--- Direct messages with {parts[1]} ---");
                        foreach (var m in page.Messages)
                            PrintMessage(m, m.Sender, ConsoleColor.Magenta);
                        break;

                    case "/search" when parts.Length >= 2:
                        string query = input[(parts[0].Length + 1)..].Trim();
                        var results = await connection.InvokeAsync<List<MessageDto>>("Search", query);
                        PrintInfo(results.Count == 0 ? $"No messages match \"{query}\"." : $"--- {results.Count} result(s) for \"{query}\" ---");
                        foreach (var m in results.AsEnumerable().Reverse())
                            PrintMessage(m, m.Room is not null ? $"#{m.Room} {m.Sender}" : $"[DM] {m.Sender}", ConsoleColor.Cyan);
                        break;

                    default:
                        PrintError("Unknown command or missing arguments. Type /help.");
                        break;
                }
            }
            catch (HubException ex)
            {
                PrintError(CleanHubError(ex.Message));
            }
            catch (Exception ex)
            {
                PrintError(ex.Message);
            }
        }

        await connection.StopAsync();
        return 0;
    }

    /// <summary>
    /// Uses the first argument or VESPERA_URL if it's a valid http(s) URL, otherwise the default.
    /// (Running "dotnet run VesperaHub.Client" passes "VesperaHub.Client" as an argument, which is not a URL.)
    /// </summary>
    private static string ResolveServerUrl(string[] args)
    {
        foreach (string? candidate in new[] { args.FirstOrDefault(), Environment.GetEnvironmentVariable("VESPERA_URL") })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                return uri.GetLeftPart(UriPartial.Authority);
            WriteColored($"Ignoring \"{candidate}\": not a server URL (expected something like {DefaultServerUrl}).", ConsoleColor.DarkYellow);
        }
        return DefaultServerUrl;
    }

    private static Task SendAsync(HubConnection connection, string conversation, string content) =>
        connection.InvokeAsync<MessageDto>("SendMessage", new SendMessageRequest(conversation, content, null, null));

    private static async Task<AuthResponse?> AuthenticateAsync(HttpClient http)
    {
        while (true)
        {
            Console.Write("Do you want to (l)og in or (r)egister? [l]: ");
            string mode = Console.ReadLine()?.Trim().ToLowerInvariant() == "r" ? "register" : "login";

            Console.Write("Username: ");
            string username = Console.ReadLine()?.Trim() ?? "";
            Console.Write("Password: ");
            string password = ReadPassword();

            HttpResponseMessage response;
            try
            {
                response = await http.PostAsJsonAsync($"/api/auth/{mode}", new { Username = username, Password = password });
            }
            catch (HttpRequestException ex)
            {
                PrintError($"Couldn't reach the server: {ex.Message}");
                PrintError("Is it running? Start it with: dotnet run --project VesperaHub.Server");
                return null;
            }

            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<AuthResponse>();

            PrintError(await ReadErrorAsync(response));
        }
    }

    private static async Task JoinAsync(HubConnection connection, string room)
    {
        var result = await connection.InvokeAsync<JoinRoomResult>("JoinRoom", room);
        _room = result.Room;
        PrintInfo($"--- #{result.Room} ({result.Members.Count} viewing: {string.Join(", ", result.Members)}) ---");
        foreach (var m in result.History.Messages)
            PrintMessage(m, m.Sender, ConsoleColor.Cyan);
    }

    private static string ReadPassword()
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? "";

        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0) { chars.RemoveAt(chars.Count - 1); Console.Write("\b \b"); }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                chars.Add(key.KeyChar);
                Console.Write('*');
            }
        }
        Console.WriteLine();
        return new string(chars.ToArray());
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return "Too many attempts. Wait a minute and try again.";
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
            if (!string.IsNullOrEmpty(body?.Error)) return body.Error;
        }
        catch { /* no JSON body */ }
        return $"Request failed ({(int)response.StatusCode} {response.ReasonPhrase}).";
    }

    private static string CleanHubError(string message)
    {
        int i = message.IndexOf("HubException: ", StringComparison.Ordinal);
        return i >= 0 ? message[(i + "HubException: ".Length)..] : message;
    }

    private static bool IsMe(string name) => string.Equals(name, _me, StringComparison.OrdinalIgnoreCase);

    private static void PrintHelp() => PrintInfo("""
        Commands:
          <text>                 send a message to the current room
          /rooms                 list rooms
          /join <room>           switch to a room
          /create <room> [desc]  create a room and join it
          /dm <user> <message>   send a direct message
          /history <user>        show direct messages with a user
          /search <text>         search messages
          /help                  show this help
          /quit                  exit
        Replies, reactions, edits, uploads and more are available in the web app.
        """);

    private static void PrintMessage(MessageDto m, string label, ConsoleColor color)
    {
        string text = m.Deleted ? "(deleted)" : m.Content;
        if (m.Attachment is { } a) text += $" [📎 {a.FileName}]";
        if (m.EditedAt is not null && !m.Deleted) text += " (edited)";
        if (m.Reactions.Count > 0) text += "  " + string.Join(" ", m.Reactions.Select(r => $"{r.Emoji}{r.Users.Count}"));

        lock (ConsoleLock)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{m.SentAt.ToLocalTime():HH:mm}] ");
            if (m.ReplyTo is { } reply)
                Console.Write($"(↱ {reply.Sender}: {Truncate(reply.Snippet, 30)}) ");
            Console.ForegroundColor = color;
            Console.Write(label);
            Console.ResetColor();
            Console.WriteLine($": {text}");
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static void PrintInfo(string text) => WriteColored(text, ConsoleColor.DarkYellow);
    private static void PrintError(string text) => WriteColored($"[!] {text}", ConsoleColor.Red);

    private static void WriteColored(string text, ConsoleColor color)
    {
        lock (ConsoleLock)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ResetColor();
        }
    }

    record AuthResponse(string Token, string Username, DateTime ExpiresAt);
    record ErrorResponse(string? Error);
    record RoomDto(string Name, string Description, int Online);
    record AttachmentDto(Guid Id, string Url, string FileName, string ContentType, long Size, bool IsImage);
    record ReplyPreviewDto(long Id, string Sender, string Snippet);
    record ReactionDto(string Emoji, List<string> Users);
    record MessageDto(long Id, string? Room, string Sender, string? Recipient, string Content, DateTime SentAt,
        DateTime? EditedAt, bool Deleted, ReplyPreviewDto? ReplyTo, AttachmentDto? Attachment, List<ReactionDto> Reactions);
    record HistoryPage(List<MessageDto> Messages, bool HasMore);
    record JoinRoomResult(string Room, List<string> Members, HistoryPage History);
    record SendMessageRequest(string Conversation, string? Content, long? ReplyToId, Guid? AttachmentId);
}
