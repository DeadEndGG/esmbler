using System.Text.Json.Nodes;

namespace Esmbler.Kernel.Infrastructure.Records;

public sealed record TransitionView(string Id, string Label, string? Tone, IReadOnlyList<string> RequiredFields);

public sealed record FieldStatesView(
    IReadOnlyList<string> Required,
    IReadOnlyList<string> ReadOnly,
    IReadOnlyList<string> Hidden,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Options,
    IReadOnlyList<RuleMessageView> Messages);

public sealed record RuleMessageView(string Rule, string Tone, string Text, string? Field);

public sealed record RecordAccessView(bool CanUpdate, bool CanDelete, IReadOnlyDictionary<string, string> Fields);

/// <summary>
/// A record as one viewer may see it: hidden fields are absent from <see cref="Data"/> (REQ-AUTHZ-004),
/// references carry display names only when the viewer may read the target.
/// </summary>
public sealed record RecordView
{
    public required Guid Id { get; init; }

    public required string Entity { get; init; }

    public string? Number { get; init; }

    public required int Revision { get; init; }

    public required int ConfigRevision { get; init; }

    public required JsonObject Data { get; init; }

    public required IReadOnlyDictionary<string, string> Display { get; init; }

    public RecordAccessView? Access { get; init; }

    public IReadOnlyList<TransitionView>? Transitions { get; init; }

    public FieldStatesView? FieldStates { get; init; }

    public IReadOnlyList<RuleMessageView>? Messages { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record RecordPage(IReadOnlyList<RecordView> Items, int Total, bool TotalIsCapped, int Offset, int PageSize, int ConfigRevision);

public sealed record ActivityView(Guid Id, string Kind, string Body, Guid? Author, string? AuthorName, bool ByAgent, DateTimeOffset CreatedAt);

public sealed record HistoryEntryView(long Id, DateTimeOffset At, string Operation, string? Actor, bool ByAgent, int? ConfigRevision, IReadOnlyList<FieldChangeView> Changes);

public sealed record FieldChangeView(string Field, string Label, JsonNode? From, JsonNode? To);
