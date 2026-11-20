namespace VesperaHub.Server.Models;

public record ChatMessage(
    Guid Id,
    string Sender,
    string EncryptedContent,
    DateTime Timestamp
);