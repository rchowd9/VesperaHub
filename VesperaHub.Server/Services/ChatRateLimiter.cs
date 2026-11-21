using System.Threading.RateLimiting;

namespace VesperaHub.Server.Services;

/// <summary>
/// Per-user token bucket for chat actions: bursts of up to 8 messages, then 2 per second.
/// </summary>
public sealed class ChatRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(user =>
        RateLimitPartition.GetTokenBucketLimiter(user.ToUpperInvariant(), _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 8,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromMilliseconds(500),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    public bool TryAcquire(string user)
    {
        using var lease = _limiter.AttemptAcquire(user);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
