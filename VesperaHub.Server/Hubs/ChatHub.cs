using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using VesperaHub.Server.Models;
using VesperaHub.Server.Services;

namespace VesperaHub.Server.Hubs;

[Authorize]
public class ChatHub : Hub
{
    private readonly MessageHistoryService _historyService;
    private readonly EncryptionService _encryptionService;

    public ChatHub(MessageHistoryService historyService, EncryptionService encryptionService)
    {
        _historyService = historyService;
        _encryptionService = encryptionService;
    }

    public override async Task OnConnectedAsync()
    {
        string username = Context.User?.Identity?.Name ?? "Unknown";

        // Send encrypted message history to the newly connected user
        var history = _historyService.GetRecentMessages();
        await Clients.Caller.SendAsync("LoadHistory", history);

        // Notify others
        string joinNotice = _encryptionService.Encrypt($"{username} joined the channel.");
        await Clients.Others.SendAsync("ReceiveMessage", new ChatMessage(
            Guid.NewGuid(), "System", joinNotice, DateTime.UtcNow));

        await base.OnConnectedAsync();
    }

    public async Task SendMessage(string encryptedPayload)
    {
        string username = Context.User?.Identity?.Name ?? "Anonymous";

        // Validate that the payload can be decrypted (integrity check)
        _ = _encryptionService.Decrypt(encryptedPayload);

        var chatMessage = new ChatMessage(
            Id: Guid.NewGuid(),
            Sender: username,
            EncryptedContent: encryptedPayload,
            Timestamp: DateTime.UtcNow
        );

        _historyService.AddMessage(chatMessage);

        // Broadcast encrypted message to all connected clients
        await Clients.All.SendAsync("ReceiveMessage", chatMessage);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        string username = Context.User?.Identity?.Name ?? "Unknown";
        string leaveNotice = _encryptionService.Encrypt($"{username} left the channel.");
        await Clients.Others.SendAsync("ReceiveMessage", new ChatMessage(
            Guid.NewGuid(), "System", leaveNotice, DateTime.UtcNow));

        await base.OnDisconnectedAsync(exception);
    }
}