namespace VesperaHub.Server.Models;

public record AuthRequest(string Username, string Password);
public record AuthResponse(string Token, string Username, DateTime ExpiresAt);

public record RoomDto(string Name, string Description, int Online);
public record CreateRoomRequest(string Name, string? Description);

public record MessageDto(long Id, string Room, string Sender, string Content, DateTime SentAt);
public record DirectMessageDto(long Id, string Sender, string Recipient, string Content, DateTime SentAt);
public record JoinRoomResult(string Room, IReadOnlyList<MessageDto> Messages, IReadOnlyList<string> Members);
