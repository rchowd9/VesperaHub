namespace VesperaHub.Server.Services;

public sealed class VoicePresenceTracker
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (string Channel, string User, bool Muted)> _connections = new();

    public void Join(string connectionId, string channel, string user)
        => Mutate(connectionId, channel, user, false);

    public void SetMuted(string connectionId, bool muted)
    {
        lock (_lock)
        {
            if (_connections.TryGetValue(connectionId, out var value))
                _connections[connectionId] = (value.Channel, value.User, muted);
        }
    }

    public string? Leave(string connectionId)
    {
        lock (_lock)
        {
            if (!_connections.Remove(connectionId, out var value)) return null;
            return value.Channel;
        }
    }

    public IReadOnlyList<(string User, bool Muted)> UsersInChannel(string channel)
    {
        lock (_lock)
        {
            return _connections.Values
                .Where(v => v.Channel == channel)
                .GroupBy(v => v.User, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.Key, g.All(v => v.Muted)))
                .OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public IReadOnlyList<(string ConnectionId, string User, bool Muted)> PeersWithDetails(string channel)
    {
        lock (_lock)
        {
            return _connections
                .Where(v => v.Value.Channel == channel)
                .Select(v => (v.Key, v.Value.User, v.Value.Muted))
                .OrderBy(v => v.User, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public IReadOnlyList<string> PeersInChannel(string connectionId, string channel)
    {
        lock (_lock)
        {
            return _connections
                .Where(v => v.Key != connectionId && v.Value.Channel == channel)
                .Select(v => v.Key)
                .ToList();
        }
    }

    private void Mutate(string connectionId, string channel, string user, bool muted)
    {
        lock (_lock) _connections[connectionId] = (channel, user, muted);
    }
}
