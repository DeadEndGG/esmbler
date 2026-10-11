namespace Esmbler.Sdk.Specs;

/// <summary>
/// How inbound email becomes records and replies (ADR 0023, REQ-BIZ-003). A module declares where a new message's subject,
/// text and sender go and which transition a reply runs on a resolved record; mailboxes (addresses, credentials, sender
/// verification) are environment settings that point at an intake. A new message creates a record as its sender, with the
/// sender's own permissions; a reply in a thread becomes the sender's public reply on the record. Messages that are
/// automatic, unverified, from unknown senders or too frequent are suspended for people to review, never processed.
/// </summary>
public sealed class MailIntakeSpec
{
    public required string Id { get; set; }

    public required string Label { get; set; }

    public string? Description { get; set; }

    /// <summary>The entity new messages create.</summary>
    public required string Entity { get; set; }

    /// <summary>Text field that takes the subject (cut to the field's length).</summary>
    public required string TitleField { get; set; }

    /// <summary>Text or rich-text field that takes the message text (quoted history removed).</summary>
    public string? BodyField { get; set; }

    /// <summary>User field set to the sender (a member of the workspace).</summary>
    public required string RequesterField { get; set; }

    /// <summary>Email field that keeps the sender's address.</summary>
    public string? AddressField { get; set; }

    /// <summary>Constant values set on new records, such as the channel (validated like any write).</summary>
    public Dictionary<string, System.Text.Json.Nodes.JsonNode?> Set { get; set; } = [];

    /// <summary>Transition a reply runs (as its sender) when the record is in one of <see cref="ReopenFrom"/>.</summary>
    public string? ReopenTransition { get; set; }

    /// <summary>States in which a reply reopens the record through <see cref="ReopenTransition"/>.</summary>
    public List<string> ReopenFrom { get; set; } = [];

    /// <summary>States in which a reply starts a new record instead (a follow-up), such as closed or cancelled.</summary>
    public List<string> FollowUpFrom { get; set; } = [];

    public bool Enabled { get; set; } = true;
}

/// <summary>Why an inbound message was suspended or rejected (ADR 0023). Stable codes shown to reviewers.</summary>
public static class MailReasons
{
    /// <summary>Auto-Submitted, Precedence bulk/junk/list, X-Autoreply, mailing lists or delivery reports (RFC 3834).</summary>
    public const string AutoReply = "auto_reply";

    /// <summary>Sent by one of the workspace's own mailboxes or by the platform.</summary>
    public const string OwnMessage = "own_message";

    /// <summary>More messages from one sender in an hour than the mailbox allows.</summary>
    public const string TooManyFromSender = "too_many_from_sender";

    /// <summary>No trusted Authentication-Results header says the sender's domain passed DMARC, DKIM or SPF.</summary>
    public const string UnverifiedSender = "unverified_sender";

    /// <summary>The sender is not an enabled member of the workspace.</summary>
    public const string UnknownSender = "unknown_sender";

    /// <summary>The sender may not create the record or reply on the thread's record.</summary>
    public const string NotAllowed = "not_allowed";

    /// <summary>No usable From address, or more than one.</summary>
    public const string BadSender = "bad_sender";

    /// <summary>The message has no text and no subject.</summary>
    public const string Empty = "empty";

    /// <summary>The mailbox's intake is missing or disabled in the published configuration.</summary>
    public const string NoIntake = "no_intake";

    /// <summary>The record the message would create is not valid (for example a required field the intake does not set).</summary>
    public const string InvalidRecord = "invalid_record";

    /// <summary>The message could not be read as email.</summary>
    public const string Unreadable = "unreadable";

    /// <summary>A reviewer rejected the message.</summary>
    public const string Rejected = "rejected";

    public static IReadOnlyList<string> All { get; } = [AutoReply, OwnMessage, TooManyFromSender, UnverifiedSender, UnknownSender, NotAllowed, BadSender, Empty, NoIntake, InvalidRecord, Unreadable, Rejected];
}
