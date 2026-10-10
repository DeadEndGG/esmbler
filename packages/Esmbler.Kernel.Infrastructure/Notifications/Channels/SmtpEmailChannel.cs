using System.Net.Sockets;
using System.Text.Json.Nodes;
using Esmbler.Sdk.Notifications;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Esmbler.Kernel.Infrastructure.Notifications.Channels;

/// <summary>
/// Email over SMTP (MailKit). Settings: host, port, security (starttls, ssl), from, fromName; secret references
/// username and password. Failures map to transient (retry), permanent (give up) or unknown (may have been sent).
/// </summary>
public sealed class SmtpEmailChannel(NotificationOptions options) : INotificationChannel
{
    public string Type => "email";

    public bool SupportsIdempotency => false;

    public async Task<ChannelResult> SendAsync(ChannelMessage message, JsonObject settings, ISecretAccessor secrets, string idempotencyKey, CancellationToken ct)
    {
        var host = settings["host"]?.GetValue<string>();
        if (!options.IsAllowed(Type, host))
        {
            return ChannelResult.Permanent("host_not_allowed");
        }

        var from = settings["from"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(from) || !MailboxAddress.TryParse(message.To, out var to))
        {
            return ChannelResult.Permanent("bad_address");
        }

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(settings["fromName"]?.GetValue<string>() ?? "Esmbler", from));
        mime.To.Add(to);
        mime.Subject = message.Subject ?? string.Empty;
        mime.MessageId = $"{idempotencyKey.Replace(':', '.')}@esmbler";
        mime.Body = new TextPart("plain") { Text = message.Body };

        using var client = new SmtpClient { Timeout = 15_000 };
        try
        {
            var port = settings["port"]?.GetValue<int>() ?? 587;
            var security = settings["security"]?.GetValue<string>() == "ssl" ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
            await client.ConnectAsync(host!, port, security, ct);
            if (secrets.Get(settings["usernameRef"]?.GetValue<string>() ?? "SMTP_USERNAME") is { } user && secrets.Get(settings["passwordRef"]?.GetValue<string>() ?? "SMTP_PASSWORD") is { } password)
            {
                await client.AuthenticateAsync(user, password, ct);
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or SslHandshakeException or ServiceNotConnectedException or SmtpProtocolException or TimeoutException)
        {
            return ChannelResult.Transient("connect_failed");
        }
        catch (AuthenticationException)
        {
            return ChannelResult.Permanent("authentication_failed");
        }

        try
        {
            var response = await client.SendAsync(mime, ct);
            await client.DisconnectAsync(true, ct);
            return ChannelResult.Sent(response.Length > 100 ? response[..100] : response);
        }
        catch (SmtpCommandException ex)
        {
            return (int)ex.StatusCode >= 500 ? ChannelResult.Permanent($"smtp_{(int)ex.StatusCode}") : ChannelResult.Transient($"smtp_{(int)ex.StatusCode}");
        }
        catch (Exception ex) when (ex is IOException or SmtpProtocolException or TimeoutException or OperationCanceledException)
        {
            // The server may have accepted the message before the connection broke.
            return ChannelResult.Unknown("connection_lost");
        }
    }
}
