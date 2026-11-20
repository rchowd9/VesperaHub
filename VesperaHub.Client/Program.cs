using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace VesperaHub.Client;

/// <summary>
/// Console client for Vespera Hub. Usage: dotnet run --project VesperaHub.Client [serverUrl]
/// </summary>
class Program
{
    private static readonly object ConsoleLock = new();
    private static string _me = "";
    private static string _room = "";

    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string serverUrl = (args.FirstOrDefault() ?? Environment.GetEnvironmentVariable("VESPERA_URL") ?? "https://localhost:5001").TrimEnd('/');
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
            if (string.Equals(m.Room, _room, StringComparison.OrdinalIgnoreCase))
                PrintMessage(m.SentAt, m.Sender, m.Content, ConsoleColor.Cyan);
        });
        connection.On<DirectMessageDto>("DirectMessageReceived", m =>
        {
            string label = m.Sender.Equals(_me, StringComparison.OrdinalIgnoreCase) ? $"you → {m.Recipient}" : $"{m.Sender} → you";
            PrintMessage(m.SentAt, $"[DM] {label}", m.Content, ConsoleColor.Magenta);
        });
        connection.On<string, List<string>>("PresenceChanged", (room, users) =>
        {
            if (string.Equals(room, _room, StringComparison.OrdinalIgnoreCase))
                PrintInfo($"In #{room}: {string.Join(", ", users)}");
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
        catch (HubException ex) when (ex.Message.Contains("Method does not exist"))
        {
            PrintError("The server is running an older version of Vespera Hub. Stop it, then start it again with: dotnet run --project VesperaHub.Server");
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
                    await connection.InvokeAsync("SendMessage", input);
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
                            PrintInfo($"#{r.Name,-16} {r.Online} online   {r.Description}");
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
                        await connection.InvokeAsync("SendDirectMessage", parts[1], parts[2]);
                        break;

                    case "/history" when parts.Length >= 2:
                        var dms = await connection.InvokeAsync<List<DirectMessageDto>>("GetDirectHistory", parts[1]);
                        PrintInfo($"--- Direct messages with {parts[1]} ---");
                        foreach (var m in dms)
                            PrintMessage(m.SentAt, m.Sender, m.Content, ConsoleColor.Magenta);
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
        PrintInfo($"--- #{result.Room} ({result.Members.Count} online: {string.Join(", ", result.Members)}) ---");
        foreach (var m in result.Messages)
            PrintMessage(m.SentAt, m.Sender, m.Content, ConsoleColor.Cyan);
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

    private static void PrintHelp() => PrintInfo("""
        Commands:
          <text>                 send a message to the current room
          /rooms                 list rooms
          /join <room>           switch to a room
          /create <room> [desc]  create a room and join it
          /dm <user> <message>   send a direct message
          /history <user>        show direct messages with a user
          /help                  show this help
          /quit                  exit
        """);

    private static void PrintMessage(DateTime sentAt, string sender, string content, ConsoleColor color)
    {
        lock (ConsoleLock)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{sentAt.ToLocalTime():HH:mm}] ");
            Console.ForegroundColor = color;
            Console.Write(sender);
            Console.ResetColor();
            Console.WriteLine($": {content}");
        }
    }

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
    record MessageDto(long Id, string Room, string Sender, string Content, DateTime SentAt);
    record DirectMessageDto(long Id, string Sender, string Recipient, string Content, DateTime SentAt);
    record JoinRoomResult(string Room, List<MessageDto> Messages, List<string> Members);
}
