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

    /// <summary>Refresh redeems a 256-bit random token, so it only needs flood protection, not guessing protection.</summary>
    public const string RefreshPolicy = "auth-refresh";

    /// <summary>Per-person limit for dashboards, metrics and explorer group counts.</summary>
    public const string QueryPolicy = "figures";

    /// <summary>Per-person limit for creating workspaces (each one is a new tenant).</summary>
    public const string WorkspacePolicy = "workspaces";

    /// <summary>Audit exports (ADR 0022): two a minute per person.</summary>
    public const string ExportPolicy = "exports";

    /// <summary>Explorer and figure CSV exports (REQ-OPS-004): ten a minute per person.</summary>
    public const string RecordExportPolicy = "record-exports";

    /// <summary>
    /// OAuth token and revocation endpoints (ADR 0025): ChatGPT and Claude refresh for many people from few addresses,
    /// so the limit is per address and generous; tokens are 256-bit, so guessing is not the risk.
    /// </summary>
    public const string OAuthPolicy = "oauth";

    /// <summary>Inbound email webhook (ADR 0023): per sending address (the mail provider), before the token is checked.</summary>
    public const string InboundMailPolicy = "inbound-mail";
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
                if (context.HttpContext.Request.Path.StartsWithSegments("/oauth"))
                {
                    // OAuth clients expect RFC 6749 errors.
                    await context.HttpContext.Response.WriteAsJsonAsync(new { error = "temporarily_unavailable", error_description = "Too many requests. Try again in a minute." }, ct);
                    return;
                }

                context.HttpContext.Response.ContentType = "application/problem+json";
                var title = context.HttpContext.Request.Path.StartsWithSegments("/api/auth")
                    ? "Too many sign-in attempts from this address. Wait a minute and try again."
                    : context.HttpContext.Request.Path.StartsWithSegments("/api/workspaces")
                        ? "Too many workspaces created in a short time. Wait a minute and try again."
                        : context.HttpContext.Request.Path.StartsWithSegments("/api/mail/inbound")
                            ? "Too many messages from this address. Try again in a minute."
                        : context.HttpContext.Request.Path.StartsWithSegments("/api/admin/audit")
                            ? "Too many audit exports or verifications in a short time. Wait a minute and try again."
                        : context.HttpContext.Request.Path.Value?.EndsWith("/export", StringComparison.Ordinal) == true
                            ? "Too many exports in a short time. Wait a minute and try again."
                            : "Too many requests for figures in a short time. Wait a minute and try again.";
                await context.HttpContext.Response.WriteAsJsonAsync(new { status = 429, title, code = "rate_limited" }, ct);
            };
            o.AddPolicy(Policy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = options.Auth.LoginAttemptsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            // Figures and group counts aggregate many rows: limited per person (threat model, ADR 0019).
            o.AddPolicy(QueryPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                (http.User.FindFirst("sub") ?? http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier))?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(options.MetricRequestsPerMinute, 1), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy(WorkspacePolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                (http.User.FindFirst("sub") ?? http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier))?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 3, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy(OAuthPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 1200, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy(InboundMailPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy(RecordExportPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                (http.User.FindFirst("sub") ?? http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier))?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy(ExportPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                (http.User.FindFirst("sub") ?? http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier))?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 2, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy(RefreshPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(options.Auth.LoginAttemptsPerMinute, 1) * 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }
}
