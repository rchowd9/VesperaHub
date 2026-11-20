using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VesperaHub.Server.Data;
using VesperaHub.Server.Models;
using VesperaHub.Server.Services;

namespace VesperaHub.Server.Endpoints;

public static partial class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";
    private const int MinPasswordLength = 8;

    [GeneratedRegex("^[A-Za-z0-9_]{3,20}$")]
    private static partial Regex UsernamePattern();

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").RequireRateLimiting(RateLimitPolicy);

        group.MapPost("/register", async (AuthRequest req, VesperaDbContext db, TokenService tokens) =>
        {
            string username = req.Username?.Trim() ?? "";
            if (!UsernamePattern().IsMatch(username))
                return Results.BadRequest(new { error = "Username must be 3–20 letters, numbers or underscores." });
            if (string.IsNullOrEmpty(req.Password) || req.Password.Length < MinPasswordLength)
                return Results.BadRequest(new { error = $"Password must be at least {MinPasswordLength} characters." });

            string normalized = username.ToUpperInvariant();
            if (await db.Users.AnyAsync(u => u.NormalizedUsername == normalized))
                return Results.Conflict(new { error = "That username is already taken." });

            var user = new User
            {
                Username = username,
                NormalizedUsername = normalized,
                PasswordHash = PasswordHasher.Hash(req.Password),
                CreatedAt = DateTime.UtcNow,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();

            return Results.Ok(tokens.CreateToken(user));
        });

        group.MapPost("/login", async (AuthRequest req, VesperaDbContext db, TokenService tokens) =>
        {
            string normalized = req.Username?.Trim().ToUpperInvariant() ?? "";
            var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUsername == normalized);

            if (user is null || !PasswordHasher.Verify(req.Password ?? "", user.PasswordHash))
                return Results.Json(new { error = "Wrong username or password." }, statusCode: StatusCodes.Status401Unauthorized);

            return Results.Ok(tokens.CreateToken(user));
        });
    }
}
