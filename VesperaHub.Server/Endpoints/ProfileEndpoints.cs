using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using VesperaHub.Server.Data;
using VesperaHub.Server.Hubs;
using VesperaHub.Server.Models;
using VesperaHub.Server.Services;

namespace VesperaHub.Server.Endpoints;

public static class ProfileEndpoints
{
    public static void MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/profile", async (ClaimsPrincipal principal, VesperaDbContext db) =>
        {
            int userId = GetUserId(principal);
            var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
            return Results.Ok(new ProfileDto(user.Username, user.Email, user.EmailVerified, AvatarUrl(user)));
        }).RequireAuthorization();

        app.MapGet("/api/profiles", async (string? usernames, VesperaDbContext db) =>
        {
            string[] requested = (usernames ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(101)
                .ToArray();
            if (requested.Length > 100)
                return Results.BadRequest(new { error = "Request at most 100 profiles." });

            var normalized = requested.Select(n => n.ToUpperInvariant()).ToArray();
            var users = await db.Users.AsNoTracking()
                .Where(u => normalized.Contains(u.NormalizedUsername))
                .Select(u => new { u.Username, u.ProfilePictureId, u.GooglePictureUrl })
                .ToListAsync();
            return Results.Ok(users.Select(u => new PublicProfileDto(
                u.Username,
                u.ProfilePictureId is Guid id ? $"/api/uploads/{id}" : u.GooglePictureUrl)));
        }).RequireAuthorization();

        app.MapPost("/api/profile/avatar", async (
            AvatarRequest request,
            ClaimsPrincipal principal,
            VesperaDbContext db,
            IHubContext<ChatHub> hub) =>
        {
            int userId = GetUserId(principal);
            var attachment = await db.Attachments.FirstOrDefaultAsync(a =>
                a.Id == request.AttachmentId && a.UploaderId == userId);
            if (attachment is null || !attachment.ContentType.StartsWith("image/", StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Choose an image you uploaded." });

            var user = await db.Users.FirstAsync(u => u.Id == userId);
            user.ProfilePictureId = attachment.Id;
            await db.SaveChangesAsync();

            string? avatarUrl = AvatarUrl(user);
            await hub.Clients.All.SendAsync("UserProfileUpdated", user.Username, avatarUrl);
            return Results.Ok(new ProfileDto(user.Username, user.Email, user.EmailVerified, avatarUrl));
        }).RequireAuthorization().DisableAntiforgery();
    }

    private static int GetUserId(ClaimsPrincipal principal) =>
        int.Parse(principal.FindFirst(TokenService.UserIdClaim)!.Value);

    private static string? AvatarUrl(User user) =>
        user.ProfilePictureId is Guid id ? $"/api/uploads/{id}" : user.GooglePictureUrl;
}
