using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using VesperaHub.Server.Data;
using VesperaHub.Server.Models;
using VesperaHub.Server.Services;

namespace VesperaHub.Server.Hubs;

/// <summary>
/// Real-time chat hub.
///
/// Client → server: JoinRoom, SendMessage, SendDirectMessage, GetDirectHistory, GetConversations, Typing.
/// Server → client: MessageReceived, DirectMessageReceived, PresenceChanged, OnlineUsersChanged,
///                  UserTyping, RoomCreated.
/// </summary>
[Authorize]
public class ChatHub(VesperaDbContext db, EncryptionService crypto, PresenceTracker presence) : Hub
{
    public const int MaxMessageLength = 2000;
    private const int HistorySize = 50;

    private string Username => Context.User!.Identity!.Name!;
    private int UserId => int.Parse(Context.User!.FindFirst(TokenService.UserIdClaim)!.Value);

    public override async Task OnConnectedAsync()
    {
        presence.Connect(Context.ConnectionId, Username);
        await Clients.All.SendAsync("OnlineUsersChanged", presence.OnlineUsers());
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        string? room = presence.Disconnect(Context.ConnectionId);
        if (room is not null)
            await BroadcastPresence(room);
        await Clients.All.SendAsync("OnlineUsersChanged", presence.OnlineUsers());
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Moves this connection into a room and returns its recent history and members.</summary>
    public async Task<JoinRoomResult> JoinRoom(string roomName)
    {
        var room = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Name == roomName)
            ?? throw new HubException($"Room '{roomName}' does not exist.");

        string? previous = presence.SetRoom(Context.ConnectionId, Username, room.Name);
        if (previous is not null && previous != room.Name)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, previous);
            await BroadcastPresence(previous);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, room.Name);
        await BroadcastPresence(room.Name);

        var recent = await db.Messages.AsNoTracking()
            .Where(m => m.RoomId == room.Id)
            .OrderByDescending(m => m.Id)
            .Take(HistorySize)
            .Select(m => new { m.Id, Sender = m.Sender.Username, m.EncryptedContent, m.SentAt })
            .ToListAsync();

        var history = recent
            .AsEnumerable()
            .Reverse()
            .Select(m => new MessageDto(m.Id, room.Name, m.Sender, crypto.Decrypt(m.EncryptedContent), AsUtc(m.SentAt)))
            .ToList();

        return new JoinRoomResult(room.Name, history, presence.UsersInRoom(room.Name));
    }

    /// <summary>Sends a message to the room this connection is currently in.</summary>
    public async Task SendMessage(string content)
    {
        string roomName = presence.GetRoom(Context.ConnectionId)
            ?? throw new HubException("Join a room before sending messages.");
        string text = ValidateContent(content);

        int roomId = await db.Rooms.Where(r => r.Name == roomName).Select(r => r.Id).FirstAsync();
        var message = new Message
        {
            RoomId = roomId,
            SenderId = UserId,
            EncryptedContent = crypto.Encrypt(text),
            SentAt = DateTime.UtcNow,
        };
        db.Messages.Add(message);
        await db.SaveChangesAsync();

        await Clients.Group(roomName).SendAsync("MessageReceived",
            new MessageDto(message.Id, roomName, Username, text, message.SentAt));
    }

    /// <summary>Sends a private message to another user (delivered to all of their connections).</summary>
    public async Task SendDirectMessage(string toUser, string content)
    {
        string text = ValidateContent(content);
        var recipient = await FindUser(toUser);
        if (recipient.Id == UserId)
            throw new HubException("You can't send a direct message to yourself.");

        var message = new Message
        {
            SenderId = UserId,
            RecipientId = recipient.Id,
            EncryptedContent = crypto.Encrypt(text),
            SentAt = DateTime.UtcNow,
        };
        db.Messages.Add(message);
        await db.SaveChangesAsync();

        var dto = new DirectMessageDto(message.Id, Username, recipient.Username, text, message.SentAt);
        await Clients.Users(Username, recipient.Username).SendAsync("DirectMessageReceived", dto);
    }

    /// <summary>Returns the recent direct messages between the caller and another user.</summary>
    public async Task<IReadOnlyList<DirectMessageDto>> GetDirectHistory(string otherUser)
    {
        var other = await FindUser(otherUser);
        int me = UserId;

        var recent = await db.Messages.AsNoTracking()
            .Where(m => m.RoomId == null &&
                        ((m.SenderId == me && m.RecipientId == other.Id) ||
                         (m.SenderId == other.Id && m.RecipientId == me)))
            .OrderByDescending(m => m.Id)
            .Take(HistorySize)
            .Select(m => new { m.Id, Sender = m.Sender.Username, Recipient = m.Recipient!.Username, m.EncryptedContent, m.SentAt })
            .ToListAsync();

        return recent
            .AsEnumerable()
            .Reverse()
            .Select(m => new DirectMessageDto(m.Id, m.Sender, m.Recipient, crypto.Decrypt(m.EncryptedContent), AsUtc(m.SentAt)))
            .ToList();
    }

    /// <summary>Returns the usernames the caller has exchanged direct messages with.</summary>
    public async Task<IReadOnlyList<string>> GetConversations()
    {
        int me = UserId;
        var sent = db.Messages.Where(m => m.RoomId == null && m.SenderId == me).Select(m => m.Recipient!.Username);
        var received = db.Messages.Where(m => m.RoomId == null && m.RecipientId == me).Select(m => m.Sender.Username);
        var names = await sent.Union(received).ToListAsync();
        return names.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Tells the other people in the caller's room that the caller started or stopped typing.</summary>
    public async Task Typing(bool isTyping)
    {
        string? room = presence.GetRoom(Context.ConnectionId);
        if (room is not null)
            await Clients.OthersInGroup(room).SendAsync("UserTyping", room, Username, isTyping);
    }

    private Task BroadcastPresence(string room) =>
        Clients.Group(room).SendAsync("PresenceChanged", room, presence.UsersInRoom(room));

    private async Task<User> FindUser(string username)
    {
        string normalized = (username ?? "").Trim().ToUpperInvariant();
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedUsername == normalized)
            ?? throw new HubException($"User '{username}' does not exist.");
    }

    private static string ValidateContent(string content)
    {
        string text = (content ?? "").Trim();
        if (text.Length == 0)
            throw new HubException("Message can't be empty.");
        if (text.Length > MaxMessageLength)
            throw new HubException($"Message is too long (max {MaxMessageLength} characters).");
        return text;
    }

    // SQLite doesn't store DateTimeKind, so mark values read back as UTC.
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
