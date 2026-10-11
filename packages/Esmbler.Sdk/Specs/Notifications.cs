namespace Esmbler.Sdk.Specs;

/// <summary>
/// Matches events and hands them to a platform service (ADR 0017, ADR 0021). The first target is <c>notify</c>: the
/// notification service resolves the routes of the subscription. A subscription never sends anything itself.
/// </summary>
public sealed class SubscriptionSpec
{
    public required string Id { get; set; }

    public required string Label { get; set; }

    public string? Description { get; set; }

    /// <summary>Event type ID (see the event type registry), e.g. <c>core.tickets.ticket.resolved</c>.</summary>
    public required string Event { get; set; }

    /// <summary>Only events about this entity (optional; the event type's subject entity by default).</summary>
    public string? Entity { get; set; }

    /// <summary>Optional condition over <c>event</c> (payload fields) and <c>record</c>.</summary>
    public string? When { get; set; }

    /// <summary><c>notify</c> (notification routes) or <c>alert</c> (alert rules); automation and webhooks come later.</summary>
    public string Target { get; set; } = SubscriptionTargets.Notify;

    public bool Enabled { get; set; } = true;
}

public static class SubscriptionTargets
{
    public const string Notify = "notify";
    public const string Alert = "alert";

    public static IReadOnlyList<string> All { get; } = [Notify, Alert];
}

/// <summary>How a route delivers: which channels, and which events are important enough.</summary>
public sealed class NotificationProfileSpec
{
    public required string Id { get; set; }

    public required string Label { get; set; }

    public string? Description { get; set; }

    /// <summary>Channel types in order, e.g. <c>in_app</c>, <c>email</c>, <c>sms</c>.</summary>
    public List<string> Channels { get; set; } = [NotificationChannels.InApp];

    /// <summary><c>all</c> (every channel) or <c>fallback</c> (the first channel the recipient can be reached on).</summary>
    public string Delivery { get; set; } = NotificationDelivery.All;

    /// <summary>Only events at least this severe (info, low, medium, high, critical).</summary>
    public string? MinSeverity { get; set; }

    public bool Enabled { get; set; } = true;
}

public static class NotificationChannels
{
    public const string InApp = "in_app";
    public const string Email = "email";
    public const string Sms = "sms";

    public static IReadOnlyList<string> All { get; } = [InApp, Email, Sms];
}

public static class NotificationDelivery
{
    public const string All = "all";
    public const string Fallback = "fallback";

    public static IReadOnlyList<string> Kinds { get; } = [All, Fallback];
}

/// <summary>
/// Who hears about a subscription's events, through which profile and with which template. Module routes are the
/// module layer, tenant routes the tenant layer; people's preferences sit on top, except for mandatory routes.
/// </summary>
public sealed class NotificationRouteSpec
{
    public required string Id { get; set; }

    public required string Label { get; set; }

    public required string Subscription { get; set; }

    public required string Profile { get; set; }

    public required string Template { get; set; }

    public List<RecipientSpec> Recipients { get; set; } = [];

    /// <summary>People cannot mute it (e.g. approvals, critical escalations). Changing it is a permission change.</summary>
    public bool Mandatory { get; set; }

    public bool Enabled { get; set; } = true;
}

/// <summary>A recipient rule, resolved per event.</summary>
public sealed class RecipientSpec
{
    /// <summary>
    /// <c>field</c> (a user, group or email field of the record), <c>role</c>, <c>group</c> (key), <c>user</c> (principal ID),
    /// <c>approvers</c> (the pending approvers of an approval event), <c>alert_owner</c> (the owner group and assignee of the alert
    /// a <c>platform.alert.*</c> event is about), or <c>address</c> (an external email address or phone number).
    /// </summary>
    public required string Kind { get; set; }

    public required string Ref { get; set; }
}

public static class RecipientKinds
{
    public const string Field = "field";
    public const string Role = "role";
    public const string Group = "group";
    public const string User = "user";
    public const string Approvers = "approvers";
    public const string Address = "address";
    public const string AlertOwner = "alert_owner";

    public static IReadOnlyList<string> All { get; } = [Field, Role, Group, User, Approvers, AlertOwner, Address];

    /// <summary>Recipients outside the workspace: they only see the template's external fields.</summary>
    public static bool IsExternal(RecipientSpec r) => r.Kind == Address;
}

/// <summary>The text of a template for one channel; plain text with placeholders, never HTML.</summary>
public sealed class TemplateVariantSpec
{
    public string? Subject { get; set; }

    public required string Body { get; set; }
}
