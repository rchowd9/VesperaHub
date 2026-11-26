using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using VesperaHub.Server.Data;
using VesperaHub.Server.Hubs;
using VesperaHub.Server.Models;
using VesperaHub.Server.Services;

namespace VesperaHub.Server.Endpoints;

public static partial class RoomEndpoints
{
    [GeneratedRegex("^[a-z0-9-]{2,32}$")]
    private static partial Regex RoomNamePattern();

    public static void MapRoomEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/rooms").RequireAuthorization();

        group.MapGet("/", async (VesperaDbContext db, PresenceTracker presence) =>
        {
            var rooms = await db.Rooms.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
            return rooms.Select(r => new RoomDto(r.Name, r.Description, presence.CountInRoom(r.Name)));
        });

        group.MapPost("/", async (CreateRoomRequest req, VesperaDbContext db, IHubContext<ChatHub> hub, PresenceTracker presence) =>
        {
            string name = req.Name?.Trim().ToLowerInvariant() ?? "";
            if (!RoomNamePattern().IsMatch(name))
                return Results.BadRequest(new { error = "Room names must be 2–32 lowercase letters, numbers or dashes." });
            if (await db.Rooms.AnyAsync(r => r.Name == name))
                return Results.Conflict(new { error = $"Room '{name}' already exists." });

            string description = (req.Description ?? "").Trim();
            if (description.Length > 200) description = description[..200];

            var room = new Room { Name = name, Description = description, CreatedAt = DateTime.UtcNow };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();

            // Subscribe every connected client to the new room so they get its messages and unread counts.
            foreach (string connectionId in presence.AllConnections())
                await hub.Groups.AddToGroupAsync(connectionId, ChatHub.RoomGroup(room.Name));

            var dto = new RoomDto(room.Name, room.Description, 0);
            await hub.Clients.All.SendAsync("RoomCreated", dto);
            return Results.Created($"/api/rooms/{room.Name}", dto);
        });

        group.MapGet("/{roomName}/voice", async (string roomName, VesperaDbContext db, VoicePresenceTracker voice) =>
        {
            var room = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Name == roomName);
            if (room is null) return Results.NotFound();
            var channels = await db.VoiceChannels.AsNoTracking()
                .Where(v => v.RoomId == room.Id).OrderBy(v => v.Id)
                .Select(v => new VoiceChannelDto(v.Id, v.RoomId, v.Name)).ToListAsync();
            return Results.Ok(channels);
        });

        group.MapPost("/{roomName}/voice", async (string roomName, CreateVoiceChannelRequest req, VesperaDbContext db, IHubContext<ChatHub> hub) =>
        {
            var room = await db.Rooms.FirstOrDefaultAsync(r => r.Name == roomName);
            if (room is null) return Results.NotFound();
            string name = (req.Name ?? "").Trim().ToLowerInvariant();
            if (!RoomNamePattern().IsMatch(name))
                return Results.BadRequest(new { error = "Voice channel names must be 2-32 lowercase letters, numbers or dashes." });
            if (await db.VoiceChannels.AnyAsync(v => v.RoomId == room.Id && v.Name == name))
                return Results.Conflict(new { error = $"Voice channel '{name}' already exists." });
            var channel = new VoiceChannel { RoomId = room.Id, Name = name, CreatedAt = DateTime.UtcNow };
            db.VoiceChannels.Add(channel);
            await db.SaveChangesAsync();
            var dto = new VoiceChannelDto(channel.Id, room.Id, channel.Name);
            await hub.Clients.All.SendAsync("VoiceChannelCreated", dto);
            return Results.Created($"/api/rooms/{roomName}/voice/{channel.Name}", dto);
        });
    }
}
