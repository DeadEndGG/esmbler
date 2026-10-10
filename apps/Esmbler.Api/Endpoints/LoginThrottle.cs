using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Esmbler.Hosting;
using Esmbler.Kernel;
using Microsoft.AspNetCore.RateLimiting;

namespace Esmbler.Api.Endpoints;

/// <summary>
/// Brute-force protection for sign-in (threat model T-50): a per-address rate limit on login and refresh, plus a
/// temporary lock after repeated failed passwords for one account, whatever address they come from.
/// </summary>
public sealed class LoginThrottle(PlatformOptions options, TimeProvider clock)
{
    public const string Policy = "auth";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> failures = new(StringComparer.OrdinalIgnoreCase);

    public void EnsureAllowed(string email)
    {
        if (!failures.TryGetValue(email, out var q))
        {
            return;
        }

        lock (q)
        {
            Trim(q);
            if (q.Count >= options.Auth.FailedLoginsPerAccount)
            {
                throw new KernelException("rate_limited", "Too many failed sign-ins for this account. Try again in 15 minutes or reset your password.", 429);
            }
        }
    }

    public void RecordFailure(string email)
    {
        var q = failures.GetOrAdd(email, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            Trim(q);
            q.Enqueue(clock.GetUtcNow());
        }

        if (failures.Count > 100_000)
        {
            foreach (var (key, value) in failures)
            {
                lock (value)
                {
                    Trim(value);
                    if (value.Count == 0)
                    {
                        failures.TryRemove(key, out _);
                    }
                }
            }
        }
    }

    public void RecordSuccess(string email) => failures.TryRemove(email, out _);

    private void Trim(Queue<DateTimeOffset> q)
    {
        var now = clock.GetUtcNow();
        while (q.Count > 0 && now - q.Peek() > Window)
        {
            q.Dequeue();
        }
    }

    public static void AddPolicy(IServiceCollection services, PlatformOptions options)
    {
        services.AddSingleton<LoginThrottle>();
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.ContentType = "application/problem+json";
                await context.HttpContext.Response.WriteAsJsonAsync(new { status = 429, title = "Too many sign-in attempts from this address. Wait a minute and try again.", code = "rate_limited" }, ct);
            };
            o.AddPolicy(Policy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = options.Auth.LoginAttemptsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }
}
