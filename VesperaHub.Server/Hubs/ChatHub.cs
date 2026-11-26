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
/// Conversations are addressed as "room:&lt;name&gt;" or "dm:&lt;username&gt;".
///
/// Client → server: JoinRoom, GetHistory, SendMessage, EditMessage, DeleteMessage, ToggleReaction,
///                  Search, GetConversations, Typing.
/// Server → client: MessageReceived, MessageUpdated, PresenceChanged, OnlineUsersChanged,
///                  UserTyping, RoomCreated.
///
/// Every connection is subscribed to every room's group, so clients get unread counts for all rooms.
/// "Viewing" a room (JoinRoom) only affects presence and typing indicators.
/// </summary>
[Authorize]
public class ChatHub(
    VesperaDbContext db,
    EncryptionService crypto,
    MessageMapper mapper,
    PresenceTracker presence,
    VoicePresenceTracker voicePresence,
    ChatRateLimiter rateLimiter) : Hub
{
    public const int MaxMessageLength = 4000;
    private const int PageSize = 50;
    private const int SearchScanLimit = 5000;
    private const int SearchResultLimit = 30;

    public static string RoomGroup(string room) => $"room:{room}";
    public static string VoiceGroup(string room, string channel) => $"voice:{room}:{channel}";

    private string Username => Context.User!.Identity!.Name!;
    private int UserId => int.Parse(Context.User!.FindFirst(TokenService.UserIdClaim)!.Value);

    // ---------- Connection lifecycle ----------

    public override async Task OnConnectedAsync()
    {
        presence.Connect(Context.ConnectionId, Username);
        foreach (string room in await db.Rooms.Select(r => r.Name).ToListAsync())
            await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup(room));

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

    // ---------- Reading ----------

    /// <summary>Marks this connection as viewing a room and returns its members and latest messages.</summary>
    public async Task<JoinRoomResult> JoinRoom(string roomName)
    {
        var room = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Name == roomName)
            ?? throw new HubException($"Room '{roomName}' does not exist.");

        string? previous = presence.SetRoom(Context.ConnectionId, Username, room.Name);
        if (previous is not null && previous != room.Name)
            await BroadcastPresence(previous);
        await BroadcastPresence(room.Name);

        var history = await LoadPage(new Conversation(room, null), beforeId: null);
        return new JoinRoomResult(room.Name, presence.UsersInRoom(room.Name), history);
    }

    /// <summary>Marks this connection as not viewing any room (e.g. while it has a DM open).</summary>
    public async Task LeaveRoom()
    {
        string? previous = presence.SetRoom(Context.ConnectionId, Username, null);
        if (previous is not null)
            await BroadcastPresence(previous);
    }

    /// <summary>Returns a page of messages, newest last. Pass the oldest loaded id as beforeId to page back.</summary>
    public async Task<HistoryPage> GetHistory(string conversation, long? beforeId)
    {
        var conv = await ResolveConversation(conversation);
        return await LoadPage(conv, beforeId);
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

    /// <summary>
    /// Case-insensitive search across all rooms and the caller's direct messages.
    /// Content is encrypted at rest, so recent messages are decrypted and matched in memory.
    /// </summary>
    public async Task<IReadOnlyList<MessageDto>> Search(string query)
    {
        query = (query ?? "").Trim();
        if (query.Length < 2)
            throw new HubException("Search for at least 2 characters.");
        if (query.Length > 100)
            query = query[..100];

        int me = UserId;
        var candidates = await db.Messages.AsNoTracking()
            .Where(m => m.DeletedAt == null && (m.RoomId != null || m.SenderId == me || m.RecipientId == me))
            .OrderByDescending(m => m.Id)
            .Take(SearchScanLimit)
            .Select(m => new { m.Id, m.EncryptedContent })
            .ToListAsync();

        var ids = candidates
            .Where(c => crypto.Decrypt(c.EncryptedContent).Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(SearchResultLimit)
            .Select(c => c.Id)
            .ToList();

        var messages = await MessageMapper.WithDetails(db.Messages.Where(m => ids.Contains(m.Id)))
            .OrderByDescending(m => m.Id)
            .ToListAsync();
        return messages.Select(mapper.ToDto).ToList();
    }

    // ---------- Writing ----------

    /// <summary>Sends a message (optionally a reply and/or with an uploaded attachment) to a room or user.</summary>
    public async Task<MessageDto> SendMessage(SendMessageRequest request)
    {
        EnsureNotFlooding();
        var conv = await ResolveConversation(request.Conversation);
        int me = UserId;

        string text = (request.Content ?? "").Trim();
        if (text.Length > MaxMessageLength)
            throw new HubException($"Message is too long (max {MaxMessageLength} characters).");

        if (request.AttachmentId is Guid attachmentId &&
            !await db.Attachments.AnyAsync(a => a.Id == attachmentId && a.UploaderId == me))
            throw new HubException("Attachment not found.");

        if (text.Length == 0 && request.AttachmentId is null)
            throw new HubException("Message can't be empty.");

        if (request.ReplyToId is long replyId &&
            !await ConversationMessages(conv).AnyAsync(m => m.Id == replyId))
            throw new HubException("The message you're replying to isn't in this conversation.");

        var message = new Message
        {
            RoomId = conv.Room?.Id,
            RecipientId = conv.Other?.Id,
            SenderId = me,
            EncryptedContent = crypto.Encrypt(text),
            SentAt = DateTime.UtcNow,
            ReplyToId = request.ReplyToId,
            AttachmentId = request.AttachmentId,
        };
        db.Messages.Add(message);
        await db.SaveChangesAsync();

        var dto = await LoadDto(message.Id);
        await Broadcast("MessageReceived", dto);
        return dto;
    }

    public async Task EditMessage(long messageId, string content)
    {
        EnsureNotFlooding();
        var message = await FindOwnMessage(messageId, "edit");

        string text = (content ?? "").Trim();
        if (text.Length == 0 && message.AttachmentId is null)
            throw new HubException("Message can't be empty. Delete it instead.");
        if (text.Length > MaxMessageLength)
            throw new HubException($"Message is too long (max {MaxMessageLength} characters).");

        message.EncryptedContent = crypto.Encrypt(text);
        message.EditedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await Broadcast("MessageUpdated", await LoadDto(messageId));
    }

    public async Task DeleteMessage(long messageId)
    {
        var message = await FindOwnMessage(messageId, "delete");

        message.DeletedAt = DateTime.UtcNow;
        message.EncryptedContent = crypto.Encrypt("");
        message.AttachmentId = null;
        await db.Reactions.Where(r => r.MessageId == messageId).ExecuteDeleteAsync();
        await db.SaveChangesAsync();

        await Broadcast("MessageUpdated", await LoadDto(messageId));
    }

    /// <summary>Adds the caller's reaction to a message, or removes it if it's already there.</summary>
    public async Task ToggleReaction(long messageId, string emoji)
    {
        EnsureNotFlooding();
        if (!MessageMapper.AllowedReactions.Contains(emoji))
            throw new HubException("That reaction isn't supported.");

        var message = await FindAccessibleMessage(messageId);
        if (message.DeletedAt is not null)
            throw new HubException("You can't react to a deleted message.");

        int me = UserId;
        var existing = await db.Reactions.FindAsync(messageId, me, emoji);
        if (existing is null)
            db.Reactions.Add(new Reaction { MessageId = messageId, UserId = me, Emoji = emoji });
        else
            db.Reactions.Remove(existing);
        await db.SaveChangesAsync();

        await Broadcast("MessageUpdated", await LoadDto(messageId));
    }

    /// <summary>Tells the other side of a conversation that the caller started or stopped typing.</summary>
    public async Task Typing(string conversation, bool isTyping)
    {
        if (conversation.StartsWith("room:", StringComparison.Ordinal))
        {
            string room = conversation[5..];
            var targets = presence.ConnectionsInRoom(room).Where(c => c != Context.ConnectionId).ToList();
            if (targets.Count > 0)
                await Clients.Clients(targets).SendAsync("UserTyping", conversation, Username, isTyping);
        }
        else if (conversation.StartsWith("dm:", StringComparison.Ordinal))
        {
            string other = conversation[3..];
            if (!other.Equals(Username, StringComparison.OrdinalIgnoreCase))
                await Clients.User(other).SendAsync("UserTyping", $"dm:{Username}", Username, isTyping);
        }
    }

    // ---------- Helpers ----------

    private sealed record Conversation(Room? Room, User? Other);

    private async Task<Conversation> ResolveConversation(string key)
    {
        key ??= "";
        if (key.StartsWith("room:", StringComparison.Ordinal))
        {
            string name = key[5..];
            var room = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Name == name)
                ?? throw new HubException($"Room '{name}' does not exist.");
            return new Conversation(room, null);
        }

        if (key.StartsWith("dm:", StringComparison.Ordinal))
        {
            string normalized = key[3..].Trim().ToUpperInvariant();
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedUsername == normalized)
                ?? throw new HubException($"User '{key[3..]}' does not exist.");
            if (user.Id == UserId)
                throw new HubException("You can't send direct messages to yourself.");
            return new Conversation(null, user);
        }

        throw new HubException("Unknown conversation.");
    }

    private IQueryable<Message> ConversationMessages(Conversation conv)
    {
        if (conv.Room is { } room)
            return db.Messages.Where(m => m.RoomId == room.Id);

        int me = UserId, other = conv.Other!.Id;
        return db.Messages.Where(m => m.RoomId == null &&
            ((m.SenderId == me && m.RecipientId == other) || (m.SenderId == other && m.RecipientId == me)));
    }

    private async Task<HistoryPage> LoadPage(Conversation conv, long? beforeId)
    {
        var query = ConversationMessages(conv);
        if (beforeId is long before)
            query = query.Where(m => m.Id < before);

        var page = await MessageMapper.WithDetails(query)
            .OrderByDescending(m => m.Id)
            .Take(PageSize + 1)
            .ToListAsync();

        bool hasMore = page.Count > PageSize;
        var messages = page.Take(PageSize).Reverse().Select(mapper.ToDto).ToList();
        return new HistoryPage(messages, hasMore);
    }

    private async Task<MessageDto> LoadDto(long id) =>
        mapper.ToDto(await MessageMapper.WithDetails(db.Messages.Where(m => m.Id == id)).FirstAsync());

    private async Task<Message> FindAccessibleMessage(long id)
    {
        int me = UserId;
        return await db.Messages.FirstOrDefaultAsync(m =>
                   m.Id == id && (m.RoomId != null || m.SenderId == me || m.RecipientId == me))
               ?? throw new HubException("Message not found.");
    }

    private async Task<Message> FindOwnMessage(long id, string action)
    {
        var message = await FindAccessibleMessage(id);
        if (message.SenderId != UserId)
            throw new HubException($"You can only {action} your own messages.");
        if (message.DeletedAt is not null)
            throw new HubException("That message was deleted.");
        return message;
    }

    private Task Broadcast(string eventName, MessageDto dto) => dto.Room is not null
        ? Clients.Group(RoomGroup(dto.Room)).SendAsync(eventName, dto)
        : Clients.Users(dto.Sender, dto.Recipient!).SendAsync(eventName, dto);

    private Task BroadcastPresence(string room) =>
        Clients.All.SendAsync("PresenceChanged", room, presence.UsersInRoom(room));

    private void EnsureNotFlooding()
    {
        if (!rateLimiter.TryAcquire(Username))
            throw new HubException("You're sending too fast. Wait a moment and try again.");
    }
}
