namespace VesperaHub.Server.Services;

/// <summary>
/// Tracks which user owns each SignalR connection and which room that connection is in.
/// A user can have several connections (e.g. two browser tabs).
/// </summary>
public sealed class PresenceTracker
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (string User, string? Room)> _connections = new();

    public void Connect(string connectionId, string user)
    {
        lock (_lock) _connections[connectionId] = (user, null);
    }

    /// <summary>Removes the connection and returns the room it was in, if any.</summary>
    public string? Disconnect(string connectionId)
    {
        lock (_lock)
        {
            return _connections.Remove(connectionId, out var entry) ? entry.Room : null;
        }
    }

    /// <summary>Moves the connection to a room and returns the room it was in before, if any.</summary>
    public string? SetRoom(string connectionId, string user, string room)
    {
        lock (_lock)
        {
            string? previous = _connections.TryGetValue(connectionId, out var entry) ? entry.Room : null;
            _connections[connectionId] = (user, room);
            return previous;
        }
    }

    public string? GetRoom(string connectionId)
    {
        lock (_lock) return _connections.TryGetValue(connectionId, out var entry) ? entry.Room : null;
    }

    public IReadOnlyList<string> UsersInRoom(string room)
    {
        lock (_lock)
        {
            return _connections.Values
                .Where(c => c.Room == room)
                .Select(c => c.User)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public IReadOnlyList<string> OnlineUsers()
    {
        lock (_lock)
        {
            return _connections.Values
                .Select(c => c.User)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public int CountInRoom(string room) => UsersInRoom(room).Count;
}
