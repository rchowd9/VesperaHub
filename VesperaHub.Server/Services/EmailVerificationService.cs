using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VesperaHub.Server.Data;
using VesperaHub.Server.Models;
using VesperaHub.Server.Options;

namespace VesperaHub.Server.Services;

public sealed class EmailVerificationService(
    IOptions<EmailOptions> options,
    ILogger<EmailVerificationService> logger)
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);
    private readonly EmailOptions _email = options.Value;

    public bool IsConfigured => _email.IsConfigured;

    public async Task SendAsync(User user, VesperaDbContext db, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Email delivery is not configured.");

        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        user.EmailVerificationTokenHash = HashToken(token);
        user.EmailVerificationExpiresAt = DateTime.UtcNow.Add(TokenLifetime);
        await db.SaveChangesAsync(cancellationToken);

        var publicUri = new Uri(_email.PublicUrl, UriKind.Absolute);
        string verificationUrl = new Uri(publicUri, $"/api/auth/verify?token={Uri.EscapeDataString(token)}").AbsoluteUri;
        using var message = new MailMessage
        {
            From = new MailAddress(_email.FromAddress, _email.FromName),
            Subject = "Verify your Vespera Hub email",
            Body = $"""
                <p>Verify your email address to finish creating your Vespera Hub account.</p>
                <p><a href="{WebUtility.HtmlEncode(verificationUrl)}">Verify email address</a></p>
                <p>This link expires in 24 hours. If you did not request this, you can ignore this message.</p>
                """,
            IsBodyHtml = true,
        };
        message.To.Add(new MailAddress(user.Email!));

        try
        {
            using var client = new SmtpClient(_email.SmtpHost, _email.SmtpPort)
            {
                EnableSsl = true,
                Credentials = string.IsNullOrWhiteSpace(_email.Username)
                    ? null
                    : new NetworkCredential(_email.Username, _email.Password),
            };
            await client.SendMailAsync(message, cancellationToken);
        }
        catch (SmtpException ex)
        {
            logger.LogError(ex, "Unable to deliver email verification for user {UserId}.", user.Id);
            throw;
        }
    }

    public async Task<bool> VerifyAsync(string token, VesperaDbContext db, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
            return false;

        string hash = HashToken(token);
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.EmailVerificationTokenHash == hash &&
                 u.EmailVerificationExpiresAt > DateTime.UtcNow,
            cancellationToken);
        if (user is null)
            return false;

        user.EmailVerified = true;
        user.EmailVerificationTokenHash = null;
        user.EmailVerificationExpiresAt = null;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
