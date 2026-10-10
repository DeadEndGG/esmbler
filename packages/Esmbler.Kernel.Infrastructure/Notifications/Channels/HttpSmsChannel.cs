using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Esmbler.Sdk.Notifications;

namespace Esmbler.Kernel.Infrastructure.Notifications.Channels;

/// <summary>
/// Text messages through a Twilio-style HTTPS API. Settings: host (allow-listed), accountSid, from; secret reference
/// tokenRef (default SMS_AUTH_TOKEN). The idempotency header lets an ambiguous send be retried safely.
/// </summary>
public sealed class HttpSmsChannel(NotificationOptions options, IHttpClientFactory http) : INotificationChannel
{
    public string Type => "sms";

    public bool SupportsIdempotency => true;

    public async Task<ChannelResult> SendAsync(ChannelMessage message, JsonObject settings, ISecretAccessor secrets, string idempotencyKey, CancellationToken ct)
    {
        var host = settings["host"]?.GetValue<string>() ?? "api.twilio.com";
        if (!options.IsAllowed(Type, host))
        {
            return ChannelResult.Permanent("host_not_allowed");
        }

        var sid = settings["accountSid"]?.GetValue<string>();
        var from = settings["from"]?.GetValue<string>();
        var token = secrets.Get(settings["tokenRef"]?.GetValue<string>() ?? "SMS_AUTH_TOKEN");
        if (string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(from) || token is null)
        {
            return ChannelResult.Permanent("not_configured");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/2010-04-01/Accounts/{Uri.EscapeDataString(sid)}/Messages.json")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["To"] = message.To, ["From"] = from, ["Body"] = message.Body }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{sid}:{token}")));
        request.Headers.Add("I-Twilio-Idempotency-Token", idempotencyKey);
        try
        {
            using var client = http.CreateClient("notifications.sms");
            client.Timeout = TimeSpan.FromSeconds(15);
            using var response = await client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
                return ChannelResult.Sent(body?["sid"]?.GetValue<string>());
            }

            var code = $"http_{(int)response.StatusCode}";
            return response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500 ? ChannelResult.Transient(code) : ChannelResult.Permanent(code);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError)
        {
            return ChannelResult.Transient("connect_failed");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return ChannelResult.Unknown("connection_lost");
        }
    }
}
