using System.Text.Json.Nodes;

namespace Esmbler.Sdk.Notifications;

/// <summary>A rendered message for one recipient on one channel.</summary>
public sealed record ChannelMessage(string To, string? Subject, string Body, string? RecipientName)
{
    /// <summary>
    /// Email threading (ADR 0023): the message's own ID (without angle brackets), set by the platform so replies can be
    /// matched to the record; adapters that cannot set it ignore it.
    /// </summary>
    public string? MessageId { get; init; }

    /// <summary>Message IDs this message answers (In-Reply-To and References), oldest first.</summary>
    public IReadOnlyList<string> References { get; init; } = [];

    /// <summary>Where replies should go: the workspace mailbox that takes replies for the record, if any.</summary>
    public string? ReplyTo { get; init; }

    /// <summary>
    /// Marks the message as sent by a machine (RFC 3834 <c>Auto-Submitted: auto-generated</c>), so auto-responders do not
    /// answer it. True for every notification.
    /// </summary>
    public bool AutoSubmitted { get; init; } = true;
}

/// <summary>What a provider said about a send (ADR 0021): sent, a transient or permanent failure, or unknown (ambiguous).</summary>
public sealed record ChannelResult(string Outcome, string? ProviderMessageId = null, string? Code = null)
{
    public const string SentOutcome = "sent";
    public const string TransientOutcome = "transient";
    public const string PermanentOutcome = "permanent";
    public const string UnknownOutcome = "unknown";

    public static ChannelResult Sent(string? providerMessageId) => new(SentOutcome, providerMessageId);

    public static ChannelResult Transient(string code) => new(TransientOutcome, Code: code);

    public static ChannelResult Permanent(string code) => new(PermanentOutcome, Code: code);

    public static ChannelResult Unknown(string code) => new(UnknownOutcome, Code: code);
}

/// <summary>Reads a credential by its reference name; values never leave the adapter (no logs, audit or errors).</summary>
public interface ISecretAccessor
{
    string? Get(string name);
}

/// <summary>
/// A channel type (ADR 0017, ADR 0021): an adapter that sends one message through a provider. Instances (settings,
/// secret references, mode, rate) are environment settings; the platform handles retries, rate limits and capture.
/// </summary>
public interface INotificationChannel
{
    /// <summary>email or sms (in_app is built into the platform).</summary>
    string Type { get; }

    /// <summary>Whether the provider deduplicates on the idempotency key, so an ambiguous send can be retried safely.</summary>
    bool SupportsIdempotency { get; }

    Task<ChannelResult> SendAsync(ChannelMessage message, JsonObject settings, ISecretAccessor secrets, string idempotencyKey, CancellationToken ct);
}
