using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.EntityFrameworkCore;
using VesperaHub.Server.Data;
using VesperaHub.Server.Models;
using VesperaHub.Server.Services;

namespace VesperaHub.Server.Endpoints;

public static partial class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";
    private const int MinPasswordLength = 8;
    private const string ExternalScheme = "External";

    [GeneratedRegex("^[A-Za-z0-9_]{3,20}$")]
    private static partial Regex UsernamePattern();

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").RequireRateLimiting(RateLimitPolicy);

        group.MapGet("/options", (IConfiguration configuration, EmailVerificationService email) =>
            Results.Ok(new
            {
                googleEnabled = !string.IsNullOrWhiteSpace(configuration["Authentication:Google:ClientId"]) &&
                    !string.IsNullOrWhiteSpace(configuration["Authentication:Google:ClientSecret"]),
                emailEnabled = email.IsConfigured,
            }));

        group.MapPost("/register", async (
            RegisterRequest req,
            VesperaDbContext db,
            EmailVerificationService verification,
            ILoggerFactory loggerFactory) =>
        {
            string username = req.Username?.Trim() ?? "";
            string email = NormalizeEmail(req.Email);
            if (!UsernamePattern().IsMatch(username))
                return Results.BadRequest(new { error = "Username must be 3–20 letters, numbers or underscores." });
            if (string.IsNullOrEmpty(req.Password) || req.Password.Length < MinPasswordLength)
                return Results.BadRequest(new { error = $"Password must be at least {MinPasswordLength} characters." });
            if (!IsValidEmail(email))
                return Results.BadRequest(new { error = "Enter a valid email address." });
            if (!verification.IsConfigured)
                return Results.Problem(
                    "Email verification is not configured on this server.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            string normalizedUsername = username.ToUpperInvariant();
            string normalizedEmail = email.ToUpperInvariant();
            if (await db.Users.AnyAsync(u => u.NormalizedUsername == normalizedUsername))
                return Results.Conflict(new { error = "That username is already taken." });
            if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalizedEmail))
                return Results.Conflict(new { error = "That email is already in use." });

            var user = new User
            {
                Username = username,
                NormalizedUsername = normalizedUsername,
                PasswordHash = PasswordHasher.Hash(req.Password),
                Email = email,
                NormalizedEmail = normalizedEmail,
                EmailVerified = false,
                CreatedAt = DateTime.UtcNow,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();

            try
            {
                await verification.SendAsync(user, db);
            }
            catch (Exception ex) when (ex is System.Net.Mail.SmtpException or InvalidOperationException)
            {
                loggerFactory.CreateLogger("Authentication").LogError(ex, "Email verification could not be sent.");
                return Results.Problem(
                    "Your account was created, but the verification email could not be sent. Use Resend verification email to try again.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Accepted(value: new { message = "Check your inbox for a verification link before signing in." });
        });

        group.MapPost("/login", async (AuthRequest req, VesperaDbContext db, TokenService tokens) =>
        {
            string normalized = req.Username?.Trim().ToUpperInvariant() ?? "";
            var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUsername == normalized);

            if (user is null || string.IsNullOrEmpty(user.PasswordHash) ||
                !PasswordHasher.Verify(req.Password ?? "", user.PasswordHash))
                return Results.Json(new { error = "Wrong username or password." }, statusCode: StatusCodes.Status401Unauthorized);
            if (!user.EmailVerified)
                return Results.Json(
                    new { error = "Verify your email address before signing in." },
                    statusCode: StatusCodes.Status403Forbidden);

            return Results.Ok(tokens.CreateToken(user));
        });

        group.MapPost("/resend-verification", async (
            EmailRequest req,
            VesperaDbContext db,
            EmailVerificationService verification,
            ILoggerFactory loggerFactory) =>
        {
            if (!verification.IsConfigured)
                return Results.Problem("Email verification is not configured on this server.", statusCode: StatusCodes.Status503ServiceUnavailable);

            string normalizedEmail = NormalizeEmail(req.Email).ToUpperInvariant();
            var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail);
            if (user is not null && !user.EmailVerified)
            {
                try
                {
                    await verification.SendAsync(user, db);
                }
                catch (Exception ex) when (ex is System.Net.Mail.SmtpException or InvalidOperationException)
                {
                    loggerFactory.CreateLogger("Authentication").LogError(ex, "Email verification could not be resent.");
                    return Results.Problem("The verification email could not be sent. Try again later.", statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }

            return Results.Ok(new { message = "If that address has an unverified account, a new link has been sent." });
        });

        group.MapGet("/verify", (string? token) =>
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
                return Results.BadRequest("Invalid verification link.");

            string safeToken = System.Net.WebUtility.HtmlEncode(token);
            string html = $"""
                <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
                <title>Verify email · Vespera Hub</title>
                <body style="font:16px system-ui;max-width:480px;margin:12vh auto;padding:24px;color:#242638">
                <h1>Verify your email</h1><p>Confirm this address to activate your Vespera Hub account.</p>
                <form method="post" action="/api/auth/verify"><input type="hidden" name="token" value="{safeToken}">
                <button style="padding:10px 16px">Verify email address</button></form></body></html>
                """;
            return Results.Content(html, "text/html");
        });

        group.MapPost("/verify", async (
            string token,
            VesperaDbContext db,
            EmailVerificationService verification) =>
        {
            if (!await verification.VerifyAsync(token, db))
                return Results.Content("<h1>Link expired or invalid</h1><p>Request a new verification email from Vespera Hub.</p>", "text/html", statusCode: StatusCodes.Status400BadRequest);
            return Results.Content("<h1>Email verified</h1><p>Your account is ready. You can close this page and sign in to Vespera Hub.</p>", "text/html");
        });

        group.MapGet("/google", (HttpContext context, IConfiguration configuration) =>
        {
            if (!GoogleIsEnabled(configuration))
                return Results.Problem("Google sign-in is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);

            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = "/api/auth/google/callback" },
                [GoogleDefaults.AuthenticationScheme]);
        });

        group.MapGet("/google/callback", async (
            HttpContext context,
            VesperaDbContext db,
            TokenService tokens,
            GoogleLoginHandoff handoff,
            ILoggerFactory loggerFactory) =>
        {
            var external = await context.AuthenticateAsync(ExternalScheme);
            await context.SignOutAsync(ExternalScheme);
            if (!external.Succeeded || external.Principal is null)
                return Results.Redirect("/#google-error=sign-in");

            var claims = external.Principal;
            string? email = claims.FindFirstValue(ClaimTypes.Email);
            string? subject = claims.FindFirstValue(ClaimTypes.NameIdentifier);
            bool emailVerified = bool.TryParse(claims.FindFirstValue("email_verified"), out bool verified) && verified;
            if (!emailVerified || !IsValidEmail(email) || string.IsNullOrWhiteSpace(subject))
                return Results.Redirect("/#google-error=unverified-email");

            email = NormalizeEmail(email);
            string normalizedEmail = email.ToUpperInvariant();
            var user = await db.Users.FirstOrDefaultAsync(u => u.GoogleSubject == subject);
            user ??= await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail);

            if (user is null)
            {
                string username = await CreateGoogleUsername(email, db);
                user = new User
                {
                    Username = username,
                    NormalizedUsername = username.ToUpperInvariant(),
                    PasswordHash = "",
                    Email = email,
                    NormalizedEmail = normalizedEmail,
                    EmailVerified = true,
                    GoogleSubject = subject,
                    CreatedAt = DateTime.UtcNow,
                };
                db.Users.Add(user);
            }
            else
            {
                if (user.NormalizedEmail is not null && user.NormalizedEmail != normalizedEmail)
                {
                    bool emailInUse = await db.Users.AnyAsync(u =>
                        u.Id != user.Id && u.NormalizedEmail == normalizedEmail);
                    if (emailInUse)
                        return Results.Redirect("/#google-error=email-in-use");
                    user.Email = email;
                    user.NormalizedEmail = normalizedEmail;
                }
                user.EmailVerified = true;
                user.GoogleSubject = subject;
            }

            string? picture = claims.FindFirstValue("picture");
            if (Uri.TryCreate(picture, UriKind.Absolute, out var pictureUri) &&
                pictureUri.Scheme == Uri.UriSchemeHttps && pictureUri.AbsoluteUri.Length <= 2048)
                user.GooglePictureUrl = pictureUri.AbsoluteUri;

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                loggerFactory.CreateLogger("Authentication").LogWarning(ex, "Google account could not be linked.");
                return Results.Redirect("/#google-error=account");
            }

            string code = handoff.Create(tokens.CreateToken(user));
            return Results.Redirect($"/#google-auth={Uri.EscapeDataString(code)}");
        });

        group.MapPost("/google/exchange", (GoogleExchangeRequest req, GoogleLoginHandoff handoff) =>
            handoff.TryRedeem(req.Code ?? "", out var response)
                ? Results.Ok(response)
                : Results.BadRequest(new { error = "Google sign-in expired. Try again." }));
    }

    private static bool GoogleIsEnabled(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["Authentication:Google:ClientId"]) &&
        !string.IsNullOrWhiteSpace(configuration["Authentication:Google:ClientSecret"]);

    private static string NormalizeEmail(string? email) => (email ?? "").Trim();

    private static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254)
            return false;
        try
        {
            var address = new System.Net.Mail.MailAddress(email);
            return string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static async Task<string> CreateGoogleUsername(string email, VesperaDbContext db)
    {
        string localPart = email[..email.IndexOf('@')];
        string root = Regex.Replace(localPart, "[^A-Za-z0-9_]", "");
        if (root.Length < 3)
            root = "googleuser";
        root = root[..Math.Min(root.Length, 20)];

        string candidate = root;
        int suffix = 1;
        while (await db.Users.AnyAsync(u => u.NormalizedUsername == candidate.ToUpperInvariant()))
        {
            string digits = suffix++.ToString(System.Globalization.CultureInfo.InvariantCulture);
            candidate = root[..Math.Min(root.Length, 20 - digits.Length)] + digits;
        }
        return candidate;
    }
}
