using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using VesperaHub.Client.Services;

namespace VesperaHub.Client;

class Program
{
    private const string ServerUrl = "https://localhost:5001";

    static async Task Main(string[] args)
    {
        var encryption = new EncryptionService();

        Console.Write("Username: ");
        string username = Console.ReadLine()?.Trim() ?? "User";

        Console.Write("Password (default: vespera123): ");
        string password = Console.ReadLine()?.Trim() ?? "";

        // 1. Authenticate and retrieve JWT
        using var http = new HttpClient();
        var loginResponse = await http.PostAsJsonAsync($"{ServerUrl}/api/auth/login", new { Username = username, Password = password });

        if (!loginResponse.IsSuccessStatusCode)
        {
            Console.WriteLine("[!] Authentication failed. Invalid credentials.");
            return;
        }

        var authData = await loginResponse.Content.ReadFromJsonAsync<AuthResponse>();
        string token = authData!.Token;

        // 2. Build SignalR connection with Bearer Token
        var connection = new HubConnectionBuilder()
            .WithUrl($"{ServerUrl}/chathub", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .WithAutomaticReconnect()
            .Build();

        // 3. Handle incoming encrypted message history
        connection.On<List<ChatMessageDto>>("LoadHistory", history =>
        {
            Console.WriteLine("\n--- Encrypted Message History ---");
            foreach (var msg in history)
            {
                string plainText = encryption.Decrypt(msg.EncryptedContent);
                Console.WriteLine($"[{msg.Timestamp.ToLocalTime():HH:mm:ss}] {msg.Sender}: {plainText}");
            }
            Console.WriteLine("---------------------------------\n");
        });

        // 4. Handle incoming real-time encrypted messages
        connection.On<ChatMessageDto>("ReceiveMessage", msg =>
        {
            string plainText = encryption.Decrypt(msg.EncryptedContent);
            Console.WriteLine($"[{msg.Timestamp.ToLocalTime():HH:mm:ss}] {msg.Sender}: {plainText}");
        });

        await connection.StartAsync();
        Console.WriteLine("Connected securely to Vespera Hub! Type messages below (/quit to exit):\n");

        // 5. Read user input, encrypt locally, and invoke Hub method
        while (true)
        {
            string? input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) continue;
            if (input.Equals("/quit", StringComparison.OrdinalIgnoreCase)) break;

            string encryptedPayload = encryption.Encrypt(input);
            await connection.InvokeAsync("SendMessage", encryptedPayload);
        }

        await connection.StopAsync();
    }

    record AuthResponse(string Token);
    record ChatMessageDto(Guid Id, string Sender, string EncryptedContent, DateTime Timestamp);
}