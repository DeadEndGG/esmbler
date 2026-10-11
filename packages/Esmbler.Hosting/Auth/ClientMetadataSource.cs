using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Esmbler.Hosting.Auth;

/// <summary>An external MCP client as its Client ID Metadata Document describes it (ADR 0025).</summary>
public sealed record ClientMetadata(string ClientId, string Host, string Name, IReadOnlyList<string> RedirectUris);

/// <summary>Resolves a client_id (an HTTPS URL) to its metadata document; null when it is not acceptable.</summary>
public interface IClientMetadataSource
{
    Task<ClientMetadata?> GetAsync(string clientId, CancellationToken ct);
}

/// <summary>
/// Fetches Client ID Metadata Documents (ADR 0025, ASM-143): only HTTPS URLs on the operator's host allowlist, never
/// following redirects, never connecting to private, loopback or link-local addresses (checked on the resolved address
/// at connect time), at most 64 KB in 5 seconds, cached for an hour. The document's client_id must equal its URL.
/// </summary>
public sealed partial class HttpClientMetadataSource(PlatformOptions options) : IClientMetadataSource
{
    private const int MaxBytes = 64 * 1024;
    private static readonly ConcurrentDictionary<string, (ClientMetadata Metadata, DateTimeOffset At)> Cache = new(StringComparer.Ordinal);

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        // A proxy would be the address checked at connect time, not the metadata host.
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = ConnectPublicOnlyAsync,
    })
    { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = MaxBytes };

    public async Task<ClientMetadata?> GetAsync(string clientId, CancellationToken ct)
    {
        if (!Acceptable(clientId, options.Mcp.AllowedClientHosts, out var url))
        {
            return null;
        }

        if (Cache.TryGetValue(clientId, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromHours(1))
        {
            return cached.Metadata;
        }

        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType is not ("application/json" or "application/client-metadata+json"))
            {
                return null;
            }

            var metadata = Parse(clientId, url.Host, await response.Content.ReadAsStringAsync(ct));
            if (metadata is not null)
            {
                Cache[clientId] = (metadata, DateTimeOffset.UtcNow);
            }

            return metadata;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Whether a client_id is an HTTPS URL with a path on an allowlisted host (exact host or a subdomain of one).</summary>
    public static bool Acceptable(string? clientId, IEnumerable<string> allowedHosts, out Uri url)
    {
        url = null!;
        if (clientId is null || clientId.Length > 500 || !Uri.TryCreate(clientId, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps || !parsed.IsDefaultPort || parsed.UserInfo.Length > 0 || parsed.AbsolutePath.Length <= 1 || parsed.Fragment.Length > 0)
        {
            return false;
        }

        var host = parsed.IdnHost.ToLowerInvariant();
        if (!allowedHosts.Any(a => host.Equals(a, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + a, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        url = parsed;
        return true;
    }

    /// <summary>Validates a metadata document; redirect URIs must be HTTPS, or loopback HTTP when the document says so.</summary>
    public static ClientMetadata? Parse(string clientId, string host, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("client_id", out var id) || id.GetString() != clientId)
            {
                return null;
            }

            var name = root.TryGetProperty("client_name", out var n) && n.ValueKind == JsonValueKind.String ? Controls().Replace(n.GetString()!, " ").Trim() : host;
            var redirects = root.TryGetProperty("redirect_uris", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(ValidRedirect).Take(20).ToList()
                : [];
            return redirects.Count == 0 ? null : new ClientMetadata(clientId, host, name.Length is > 0 and <= 100 ? name : host, redirects);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ValidRedirect(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Fragment.Length == 0 && u.UserInfo.Length == 0
        && (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && (u.Host is "127.0.0.1" or "[::1]" or "localhost")));

    private static async ValueTask<Stream> ConnectPublicOnlyAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var target = addresses.FirstOrDefault(IsPublic) ?? throw new HttpRequestException("The client metadata host does not resolve to a public address.");
        if (addresses.Any(a => !IsPublic(a)))
        {
            throw new HttpRequestException("The client metadata host resolves to a private address.");
        }

        var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Not loopback, private (RFC 1918, unique local), link-local, CGNAT, multicast or unspecified.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var v6 = address.GetAddressBytes();

            // NAT64 (64:ff9b::/96) and 6to4 (2002::/16) can reach IPv4 addresses inside: not public for this purpose.
            var nat64 = v6[0] == 0 && v6[1] == 0x64 && v6[2] == 0xff && v6[3] == 0x9b;
            var sixToFour = v6[0] == 0x20 && v6[1] == 0x02;
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal || nat64 || sixToFour);
        }

        var b = address.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 0 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] is >= 64 and <= 127) || (b[0] == 198 && b[1] is 18 or 19) || b[0] >= 224);
    }

    [GeneratedRegex(@"[\p{Cc}\p{Cf}]")]
    private static partial Regex Controls();
}
