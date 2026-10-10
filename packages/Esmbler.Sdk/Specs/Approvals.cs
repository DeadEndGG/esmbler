namespace Esmbler.Sdk.Specs;

/// <summary>
/// An approval policy (ADR 0016): ordered stages of approvers that a request passes before fulfillment. Stages run
/// in order; the approvers of one stage decide in parallel. Configuration, not data: approvals in flight keep a
/// snapshot of the policy they started with.
/// </summary>
public sealed class ApprovalPolicySpec
{
    public required string Id { get; set; }

    public required string Label { get; set; }

    public string? Description { get; set; }

    /// <summary>Request entity whose requests this policy approves; it must declare an approval binding.</summary>
    public required string Entity { get; set; }

    public List<ApprovalStageSpec> Stages { get; set; } = [];

    public ApprovalStageSpec? FindStage(string id) => Stages.Find(s => s.Id == id);
}

/// <summary>One stage of an approval policy.</summary>
public sealed class ApprovalStageSpec
{
    /// <summary>snake_case, unique within the policy.</summary>
    public required string Id { get; set; }

    public required string Label { get; set; }

    /// <summary><c>all</c>: every approver must approve; <c>any</c>: the first approval decides.</summary>
    public string Mode { get; set; } = ApprovalModes.All;

    public List<ApproverSpec> Approvers { get; set; } = [];

    /// <summary>Condition over <c>record</c> and <c>variables</c>; when false the stage is skipped.</summary>
    public string? When { get; set; }

    /// <summary>Hours until the stage expires; null means it waits indefinitely.</summary>
    public int? ExpiresAfterHours { get; set; }

    /// <summary><c>reject</c> (default) or <c>escalate</c> to <see cref="EscalateTo"/> with a second deadline.</summary>
    public string OnExpiry { get; set; } = ApprovalExpiry.Reject;

    public List<ApproverSpec> EscalateTo { get; set; } = [];
}

/// <summary>Who may decide a step: a group (any member), a role (any holder), or a user or group named by a field.</summary>
public sealed class ApproverSpec
{
    /// <summary><c>group</c>, <c>role</c>, or <c>field</c>.</summary>
    public required string Kind { get; set; }

    /// <summary>Group key, role ID, or <c>record.&lt;field&gt;</c> / <c>variables.&lt;key&gt;</c> holding a user or group.</summary>
    public required string Ref { get; set; }
}

public static class ApprovalModes
{
    public const string All = "all";
    public const string Any = "any";

    public static IReadOnlyList<string> Values { get; } = [All, Any];
}

public static class ApprovalExpiry
{
    public const string Reject = "reject";
    public const string Escalate = "escalate";

    public static IReadOnlyList<string> Values { get; } = [Reject, Escalate];
}

public static class ApproverKinds
{
    public const string Group = "group";
    public const string Role = "role";
    public const string Field = "field";

    /// <summary>Resolved kinds stored on approval steps (fields resolve to a user or a group).</summary>
    public const string User = "user";

    public static IReadOnlyList<string> Configurable { get; } = [Group, Role, Field];
}

/// <summary>Declares how a request entity's workflow carries approvals (ADR 0016).</summary>
public sealed class ApprovalBindingSpec
{
    /// <summary>State the request waits in while approvals are pending.</summary>
    public required string State { get; set; }

    /// <summary>System transition from the initial state into <see cref="State"/>.</summary>
    public required string RequestTransition { get; set; }

    /// <summary>System transition fired when every stage is approved (typically into a fulfillment start state).</summary>
    public required string ApprovedTransition { get; set; }

    /// <summary>System transition fired when a stage is rejected or expires (into a cancelled-category state).</summary>
    public required string DeclinedTransition { get; set; }

    /// <summary>Field of type <c>principals</c> listing approvers; declared as an access field so they can read the request.</summary>
    public string ApproversField { get; set; } = "approvers";
}
