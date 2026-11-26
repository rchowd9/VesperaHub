namespace VesperaHub.Server.Models;

public record AuthRequest(string Username, string Password);
public record RegisterRequest(string Username, string Password, string Email);
public record AuthResponse(string Token, string Username, DateTime ExpiresAt);
public record EmailRequest(string Email);
public record GoogleExchangeRequest(string Code);
public record ProfileDto(string Username, string? Email, bool EmailVerified, string? AvatarUrl);
public record AvatarRequest(Guid AttachmentId);
public record PublicProfileDto(string Username, string? AvatarUrl);

public record RoomDto(string Name, string Description, int Online);
public record CreateRoomRequest(string Name, string? Description);
public record VoiceChannelDto(int Id, int RoomId, string Name);
public record CreateVoiceChannelRequest(string RoomName, string Name);
public record VoicePeerDto(string ConnectionId, string User, bool Muted);

public record AttachmentDto(Guid Id, string Url, string FileName, string ContentType, long Size, bool IsImage);
public record ReplyPreviewDto(long Id, string Sender, string Snippet);
public record ReactionDto(string Emoji, IReadOnlyList<string> Users);

/// <summary>
/// A message in a room (<see cref="Room"/> set) or a direct message (<see cref="Recipient"/> set).
/// </summary>
public record MessageDto(
    long Id,
    string? Room,
    string Sender,
    string? Recipient,
    string Content,
    DateTime SentAt,
    DateTime? EditedAt,
    bool Deleted,
    ReplyPreviewDto? ReplyTo,
    AttachmentDto? Attachment,
    IReadOnlyList<ReactionDto> Reactions);

public record HistoryPage(IReadOnlyList<MessageDto> Messages, bool HasMore);
public record JoinRoomResult(string Room, IReadOnlyList<string> Members, HistoryPage History);

/// <summary>
/// <see cref="Conversation"/> is "room:&lt;name&gt;" or "dm:&lt;username&gt;".
/// </summary>
public record SendMessageRequest(string Conversation, string? Content, long? ReplyToId, Guid? AttachmentId);
