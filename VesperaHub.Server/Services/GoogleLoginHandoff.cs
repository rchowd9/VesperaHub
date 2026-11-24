using System.Collections.Concurrent;
using System.Security.Cryptography;
using VesperaHub.Server.Models;

namespace VesperaHub.Server.Services;

public sealed class GoogleLoginHandoff
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, (AuthResponse Response, DateTime ExpiresAt)> _pending = new();

    public string Create(AuthResponse response)
    {
        RemoveExpired();
        string code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _pending[code] = (response, DateTime.UtcNow.Add(Lifetime));
        return code;
    }

    public bool TryRedeem(string code, out AuthResponse? response)
    {
        response = null;
        if (!_pending.TryRemove(code, out var item) || item.ExpiresAt <= DateTime.UtcNow)
            return false;

        response = item.Response;
        return true;
    }

    private void RemoveExpired()
    {
        DateTime now = DateTime.UtcNow;
        foreach (var item in _pending)
            if (item.Value.ExpiresAt <= now)
                _pending.TryRemove(item.Key, out _);
    }
}
