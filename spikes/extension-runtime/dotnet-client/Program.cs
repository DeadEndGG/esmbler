// .NET client for the extension-runtime sidecar (Gate 0 spike).
// In the real platform this lives behind an IExtensionRuntime port in the
// Extensions module; the platform resolves tenant/environment from the request
// context (never from user input), sends only the settings snapshot for that
// tenant+environment, and maps statuses to the hook's fail policy.
//
// Usage: dotnet run -- [http://127.0.0.1:8080] [iterations]
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

var baseUrl = args.Length > 0 ? args[0] : "http://127.0.0.1:8080";
var iterations = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 500;
using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(5) };
var client = new ExtensionRuntimeClient(http);

var settings = new Dictionary<string, string> { ["ticket.impact.min-length"] = "10", ["platform.db.connection-string"] = "must-never-leak" };

async Task Show(string label, object record, InvokeLimits? limits = null, string tenant = "acme", Dictionary<string, string>? tenantSettings = null)
{
    var sw = Stopwatch.StartNew();
    var (code, r) = await client.ValidateAsync(tenant, "prod", "ticket-validator", record, tenantSettings ?? settings, limits);
    sw.Stop();
    Console.WriteLine($"{label,-34} http={code} status={r?.Status,-22} roundtrip={sw.Elapsed.TotalMilliseconds,7:F2}ms " +
        $"guest_total={r?.Metrics?.TotalUs / 1000.0,6:F2}ms fuel={r?.Metrics?.FuelConsumed,10}" +
        (r?.Violations is { Count: > 0 } v ? $" violations=[{string.Join("; ", v.Select(x => $"{x.Field}:{x.Code}"))}]" : "") +
        (r?.Error is { } e ? $" error=\"{e}\"" : "") +
        (r?.DeniedCapabilities is { Count: > 0 } d ? $" denied=[{string.Join(",", d)}]" : "") +
        (r?.Logs is { Count: > 0 } l ? $" log=\"{l[0].Msg}\"" : ""));
}

Console.WriteLine($"Sidecar: {baseUrl}");
await Show("(a) P1 without impact -> reject", new { priority = 1, title = "Mail down" });
await Show("(a) P1 with impact -> accept", new { priority = 1, title = "Mail down", impact = "All EU users cannot send email" });
await Show("(a) P3 -> accept", new { priority = 3, title = "Printer" });
await Show("(b) infinite loop, manifest limits", new { _spike = "spin" });
await Show("(b) infinite loop, timeout 10ms", new { _spike = "spin" }, new InvokeLimits(TimeoutMs: 10));
await Show("(c) memory hog", new { _spike = "oom" });
await Show("capability: read ungranted setting", new { _spike = "secret" });
await Show("settings: acme (min-length 10)", new { priority = 1, impact = "short" });
await Show("settings: globex (no setting)", new { priority = 1, impact = "short" }, tenant: "globex", tenantSettings: []);

// Per-tenant concurrency quota: 8 concurrent slow calls for one tenant (quota = 4).
var burst = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
    client.ValidateAsync("noisy", "prod", "ticket-validator", new { _spike = "spin" }, settings, new InvokeLimits(TimeoutMs: 50))));
var other = await client.ValidateAsync("quiet", "prod", "ticket-validator", new { priority = 2 }, settings, null);
Console.WriteLine($"quota burst (tenant 'noisy' x8): {string.Join(", ", burst.GroupBy(b => b.Code).Select(g => $"http {g.Key} x{g.Count()}"))}; " +
    $"concurrent tenant 'quiet' -> http {other.Code} {other.Response?.Status}");

// Warm latency from .NET through HTTP (keep-alive, sequential).
var samples = new List<double>(iterations);
for (var i = 0; i < iterations; i++)
{
    var sw = Stopwatch.StartNew();
    var (code, _) = await client.ValidateAsync($"tenant-{i % 8}", "prod", "ticket-validator",
        new { priority = 1, title = "x", impact = i % 2 == 0 ? "long enough impact" : "" }, settings, null);
    sw.Stop();
    if (code != 200) throw new InvalidOperationException($"unexpected http {code}");
    samples.Add(sw.Elapsed.TotalMilliseconds);
}
samples.Sort();
double P(double p) => samples[(int)Math.Round(p / 100.0 * (samples.Count - 1))];
Console.WriteLine($".NET -> sidecar roundtrip over HTTP (n={iterations}): p50={P(50):F3}ms p95={P(95):F3}ms p99={P(99):F3}ms max={samples[^1]:F3}ms");

public sealed class ExtensionRuntimeClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<(int Code, InvokeResponse? Response)> ValidateAsync(
        string tenant, string environment, string package, object record,
        IReadOnlyDictionary<string, string>? settings, InvokeLimits? limits, CancellationToken ct = default)
    {
        var request = new InvokeRequest(tenant, environment, package, "validate",
            new InvokeInput(record, Guid.NewGuid().ToString("N"), "user-42"), settings, limits);
        using var resp = await http.PostAsJsonAsync("/invoke", request, Json, ct);
        var body = await resp.Content.ReadFromJsonAsync<InvokeResponse>(Json, ct);
        return ((int)resp.StatusCode, body);
    }
}

public sealed record InvokeRequest(string Tenant, string Environment, string Package, string Hook, InvokeInput Input,
    IReadOnlyDictionary<string, string>? Settings, InvokeLimits? Limits);
public sealed record InvokeInput(object Record, string CorrelationId, string? ActorId);
public sealed record InvokeLimits(long? Fuel = null, long? TimeoutMs = null, long? MemoryBytes = null);
public sealed record InvokeResponse(string Status, List<ViolationDto>? Violations, string? Error, List<LogLine>? Logs,
    List<string>? DeniedCapabilities, string? Stderr, Metrics? Metrics);
public sealed record ViolationDto(string? Field, string Code, string Message);
public sealed record LogLine(string Level, string Msg);
public sealed record Metrics(long InstantiateUs, long CallUs, long TotalUs, long FuelConsumed);
