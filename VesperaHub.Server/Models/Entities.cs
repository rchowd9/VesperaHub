namespace VesperaHub.Server.Models;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string NormalizedUsername { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class Room
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A chat message. Room messages have <see cref="RoomId"/> set; direct messages have
/// <see cref="RecipientId"/> set instead. Content is always stored encrypted.
/// </summary>
public class Message
{
    public long Id { get; set; }

    public int? RoomId { get; set; }
    public Room? Room { get; set; }

    public int SenderId { get; set; }
    public User Sender { get; set; } = null!;

    public int? RecipientId { get; set; }
    public User? Recipient { get; set; }

    public string EncryptedContent { get; set; } = "";
    public DateTime SentAt { get; set; }
}
