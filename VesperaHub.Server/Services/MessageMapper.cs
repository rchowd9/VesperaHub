using Microsoft.EntityFrameworkCore;
using VesperaHub.Server.Models;

namespace VesperaHub.Server.Services;

/// <summary>Loads messages with everything the UI needs and turns them into decrypted DTOs.</summary>
public sealed class MessageMapper(EncryptionService crypto)
{
    /// <summary>The reactions people can add, in display order.</summary>
    public static readonly IReadOnlyList<string> AllowedReactions = ["👍", "❤️", "😂", "🎉", "😮", "😢", "🔥", "👀"];

    private const int SnippetLength = 90;

    public static IQueryable<Message> WithDetails(IQueryable<Message> query) => query
        .AsNoTracking()
        .Include(m => m.Room)
        .Include(m => m.Sender)
        .Include(m => m.Recipient)
        .Include(m => m.Attachment)
        .Include(m => m.ReplyTo).ThenInclude(r => r!.Sender)
        .Include(m => m.Reactions).ThenInclude(r => r.User)
        .AsSplitQuery();

    public MessageDto ToDto(Message m)
    {
        bool deleted = m.DeletedAt is not null;

        ReplyPreviewDto? reply = null;
        if (m.ReplyTo is { } r)
        {
            string snippet = r.DeletedAt is not null ? "Message deleted"
                : Snippet(crypto.Decrypt(r.EncryptedContent)) is { Length: > 0 } s ? s
                : r.AttachmentId is not null ? "📎 Attachment" : "";
            reply = new ReplyPreviewDto(r.Id, r.Sender.Username, snippet);
        }

        var reactions = deleted ? [] : m.Reactions
            .GroupBy(x => x.Emoji)
            .OrderBy(g => IndexOfReaction(g.Key))
            .Select(g => new ReactionDto(g.Key, g.Select(x => x.User.Username).Order(StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();

        return new MessageDto(
            m.Id,
            m.Room?.Name,
            m.Sender.Username,
            m.Recipient?.Username,
            deleted ? "" : crypto.Decrypt(m.EncryptedContent),
            AsUtc(m.SentAt),
            m.EditedAt is { } edited ? AsUtc(edited) : null,
            deleted,
            reply,
            deleted || m.Attachment is null ? null : ToDto(m.Attachment),
            reactions);
    }

    public static AttachmentDto ToDto(Attachment a) =>
        new(a.Id, $"/api/uploads/{a.Id:N}", a.FileName, a.ContentType, a.Size, a.ContentType.StartsWith("image/", StringComparison.Ordinal));

    private static int IndexOfReaction(string emoji)
    {
        for (int i = 0; i < AllowedReactions.Count; i++)
            if (AllowedReactions[i] == emoji) return i;
        return int.MaxValue;
    }

    private static string Snippet(string text)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= SnippetLength ? text : text[..SnippetLength] + "…";
    }

    // SQLite doesn't store DateTimeKind, so mark values read back as UTC.
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
