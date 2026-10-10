using System.Text.Json.Nodes;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Sdk.Events;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Events;

/// <summary>
/// Current-state projection (last known revision and state per record). Rejects stale revisions so duplicate or
/// out-of-order delivery can never regress state (REQ-EVT-004, REQ-ACC-06).
/// </summary>
public sealed class RecordStateProjection : IEventConsumer
{
    public const string ConsumerName = "projection.record_state";

    public string Name => ConsumerName;

    public bool Handles(EventEnvelope envelope) => envelope.RecordId is not null && envelope.RecordRevision is not null && envelope.Type.StartsWith("record.", StringComparison.Ordinal);

    public async Task HandleAsync(EventEnvelope envelope, KernelDbContext db, CancellationToken ct)
    {
        var recordId = envelope.RecordId!.Value;
        var revision = envelope.RecordRevision!.Value;
        var existing = await db.Projections.AsTracking().FirstOrDefaultAsync(p => p.Consumer == ConsumerName && p.RecordId == recordId, ct);
        if (existing is not null && existing.RecordRevision >= revision)
        {
            return; // stale or duplicate: newer state already applied
        }

        var state = new JsonObject
        {
            ["entity"] = envelope.Entity,
            ["lastEvent"] = envelope.Type,
            ["deleted"] = envelope.Type == EventTypes.RecordDeleted,
        };
        if (envelope.Payload["to"] is { } to)
        {
            state["state"] = to.DeepClone();
        }

        if (existing is null)
        {
            db.Projections.Add(new ProjectionRow { TenantId = envelope.TenantId, EnvironmentId = envelope.EnvironmentId, Consumer = ConsumerName, RecordId = recordId, RecordRevision = revision, State = state.ToJsonString(), UpdatedAt = DateTimeOffset.UtcNow });
        }
        else
        {
            if (existing.RecordRevision + 1 < revision)
            {
                // Gap detected: earlier transitions may be missing. Keep the newest state and mark it for reconciliation.
                state["gap"] = true;
            }

            existing.RecordRevision = revision;
            existing.State = state.ToJsonString();
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }
}
