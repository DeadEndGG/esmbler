namespace Esmbler.Kernel.Infrastructure.Notifications;

/// <summary>
/// Operator settings for notification delivery (ADR 0021). Channel instances name provider hosts; only hosts on these
/// lists can be used live, so a workspace cannot make the platform connect to internal addresses (threat model T-73).
/// </summary>
public sealed record NotificationOptions
{
    /// <summary>SMTP servers channel instances may use, e.g. smtp.sendgrid.net. Empty: email can only be captured.</summary>
    public IReadOnlyList<string> AllowedEmailHosts { get; init; } = [];

    /// <summary>HTTPS hosts SMS instances may call (Twilio-style API).</summary>
    public IReadOnlyList<string> AllowedSmsHosts { get; init; } = ["api.twilio.com"];

    /// <summary>IMAP servers workspace mailboxes may poll (ADR 0023). Empty: mailboxes can only receive through the webhook.</summary>
    public IReadOnlyList<string> AllowedImapHosts { get; init; } = [];

    /// <summary>Base URL of the web app, for links in messages.</summary>
    public string WebUrl { get; init; } = "http://localhost:8080";

    /// <summary>Most recipients one event may reach through all routes.</summary>
    public int MaxRecipientsPerEvent { get; init; } = 200;

    public bool IsAllowed(string type, string? host) =>
        host is not null && (type switch { "email" => AllowedEmailHosts, "imap" => AllowedImapHosts, _ => AllowedSmsHosts }).Contains(host, StringComparer.OrdinalIgnoreCase);
}
