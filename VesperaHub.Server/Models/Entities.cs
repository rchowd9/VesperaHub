namespace VesperaHub.Server.Models;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string NormalizedUsername { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string? Email { get; set; }
    public string? NormalizedEmail { get; set; }
    public bool EmailVerified { get; set; }
    public string? EmailVerificationTokenHash { get; set; }
    public DateTime? EmailVerificationExpiresAt { get; set; }
    public string? GoogleSubject { get; set; }
    public string? GooglePictureUrl { get; set; }
    public Guid? ProfilePictureId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Room
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class VoiceChannel
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public Room Room { get; set; } = null!;
    public string Name { get; set; } = "";
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
    public DateTime? EditedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public long? ReplyToId { get; set; }
    public Message? ReplyTo { get; set; }

    public Guid? AttachmentId { get; set; }
    public Attachment? Attachment { get; set; }

    public List<Reaction> Reactions { get; set; } = [];
}

/// <summary>An uploaded file. Stored on disk under App_Data/uploads with a random name.</summary>
public class Attachment
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    public string StoredName { get; set; } = "";
    public int UploaderId { get; set; }
    public User Uploader { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}

public class Reaction
{
    public long MessageId { get; set; }
    public Message Message { get; set; } = null!;
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public string Emoji { get; set; } = "";
}
