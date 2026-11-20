using Microsoft.AspNetCore.SignalR;

namespace VesperaHub.Server.Hubs;

/// <summary>Identifies SignalR users by username so Clients.User(name) reaches all of their connections.</summary>
public sealed class NameUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User?.Identity?.Name;
}
