using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using VesperaHub.Server.Data;
using VesperaHub.Server.Models;
using VesperaHub.Server.Services;

namespace VesperaHub.Server.Endpoints;

public static class UploadEndpoints
{
    public const long MaxFileBytes = 10 * 1024 * 1024;

    // Only these types are served inline (shown in the chat). Everything else downloads as a file.
    // SVG is deliberately excluded because it can contain scripts.
    private static readonly Dictionary<string, string> InlineImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    public static void MapUploadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/uploads", async (IFormFile file, ClaimsPrincipal user, VesperaDbContext db, IWebHostEnvironment env) =>
        {
            if (file.Length == 0)
                return Results.BadRequest(new { error = "The file is empty." });
            if (file.Length > MaxFileBytes)
                return Results.BadRequest(new { error = "Files can be up to 10 MB." });

            var id = Guid.NewGuid();
            string fileName = SanitizeFileName(file.FileName);
            string extension = Path.GetExtension(fileName).ToLowerInvariant();
            bool isImage = InlineImageTypes.TryGetValue(extension, out string? imageType);
            string storedName = id.ToString("N") + (extension.Length is > 0 and <= 10 ? extension : "");

            string folder = UploadsFolder(env);
            Directory.CreateDirectory(folder);
            await using (var stream = File.Create(Path.Combine(folder, storedName)))
                await file.CopyToAsync(stream);

            var attachment = new Attachment
            {
                Id = id,
                FileName = fileName,
                ContentType = isImage ? imageType! : "application/octet-stream",
                Size = file.Length,
                StoredName = storedName,
                UploaderId = int.Parse(user.FindFirst(TokenService.UserIdClaim)!.Value),
                CreatedAt = DateTime.UtcNow,
            };
            db.Attachments.Add(attachment);
            await db.SaveChangesAsync();

            return Results.Ok(MessageMapper.ToDto(attachment));
        })
        .RequireAuthorization()
        .DisableAntiforgery(); // Authenticated with a bearer token, not cookies, so CSRF doesn't apply.

        // Not behind auth so <img> tags can load it; the URL contains a random 128-bit id.
        app.MapGet("/api/uploads/{id:guid}", async (Guid id, VesperaDbContext db, IWebHostEnvironment env, HttpContext http) =>
        {
            var attachment = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
            if (attachment is null)
                return Results.NotFound();

            string path = Path.Combine(UploadsFolder(env), attachment.StoredName);
            if (!File.Exists(path))
                return Results.NotFound();

            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            bool inline = attachment.ContentType.StartsWith("image/", StringComparison.Ordinal);
            return Results.File(path, attachment.ContentType, inline ? null : attachment.FileName, enableRangeProcessing: true);
        });
    }

    private static string UploadsFolder(IWebHostEnvironment env) => Path.Combine(env.ContentRootPath, "App_Data", "uploads");

    private static string SanitizeFileName(string? name)
    {
        name = Path.GetFileName(name ?? "");
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim();
        if (name.Length == 0) name = "file";
        if (name.Length > 120)
        {
            string ext = Path.GetExtension(name);
            name = name[..(120 - ext.Length)] + ext;
        }
        return name;
    }
}
