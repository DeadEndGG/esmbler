using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Records;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Events;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Infrastructure.Events;

/// <summary>
/// Writes event envelopes (v2, ADR 0017) to the transactional outbox in the caller's transaction, so the event
/// commits exactly when the change that caused it does. Every publisher goes through here: the record engine, domain
/// events emitted by transitions and rules, and platform services (approvals, configuration, SLA).
/// </summary>
public static class EventWriter
{
    /// <summary>Adds <paramref name="envelope"/> to the outbox; returns its event ID.</summary>
    public static Guid Add(KernelDbContext db, EventEnvelope envelope)
    {
        var stamped = envelope with
        {
            RecordedAt = envelope.RecordedAt ?? DateTimeOffset.UtcNow,
            Traceparent = envelope.Traceparent ?? (Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity ? activity.Id : null),
        };
        db.Outbox.Add(new OutboxRow
        {
            Id = stamped.EventId,
            TenantId = stamped.TenantId,
            EnvironmentId = stamped.EnvironmentId,
            Type = stamped.Type,
            RecordId = stamped.RecordId,
            RecordRevision = stamped.RecordRevision,
            Event = JsonSerializer.Serialize(stamped, SpecJson.Options),
            AvailableAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        return stamped.EventId;
    }

    /// <summary>
    /// Reads an outbox row. Rows written before envelope v2 as bare <c>config.published</c> notices (revision, source,
    /// content hash) are read as v1 envelopes from the row's columns, so they dispatch instead of dead-lettering.
    /// </summary>
    public static EventEnvelope Read(OutboxRow row)
    {
        var json = JsonNode.Parse(row.Event)?.AsObject() ?? throw new JsonException("Empty event.");
        if (json.ContainsKey("eventId") || row.Type != EventTypes.ConfigPublished)
        {
            return json.Deserialize<EventEnvelope>(SpecJson.Options) ?? throw new JsonException("Empty event.");
        }

        return new EventEnvelope
        {
            EventId = row.Id,
            Type = row.Type,
            SchemaVersion = 1,
            Source = PlatformModule.ModuleId,
            TenantId = row.TenantId,
            EnvironmentId = row.EnvironmentId,
            OccurredAt = row.CreatedAt,
            CorrelationId = row.Id,
            Actor = "system",
            Payload = new JsonObject { ["revision"] = json["revision"]?.DeepClone(), ["source"] = json["source"]?.DeepClone(), ["content_hash"] = json["contentHash"]?.DeepClone() },
        };
    }

    /// <summary>The module that contributed a configuration element ("core.tickets"), or "tenant" for tenant additions.</summary>
    public static string SourceOf(EffectiveConfig config, string elementId) =>
        config.Provenance.TryGetValue(elementId, out var layer) && layer.Contains('@', StringComparison.Ordinal) ? layer[..layer.IndexOf('@', StringComparison.Ordinal)] : "tenant";

    /// <summary>The layer of the rule or transition that published an event (a tenant event type is always "tenant"); a transition without its own provenance takes its workflow's.</summary>
    private static string EmitterSource(EffectiveConfig config, string emitter) =>
        config.Provenance.ContainsKey(emitter) || !emitter.Contains('/', StringComparison.Ordinal) ? SourceOf(config, emitter) : SourceOf(config, emitter[..emitter.IndexOf('/', StringComparison.Ordinal)]);

    /// <summary>A platform event about a record (approvals and other platform services).</summary>
    public static Guid AddPlatform(KernelDbContext db, RequestContext ctx, string type, string source, string? entity, Guid? recordId, int? recordRevision, int? configRevision, JsonObject payload, string severity = EventSeverities.Info, Guid? causation = null) =>
        Add(db, new EventEnvelope
        {
            EventId = Guid.NewGuid(),
            Type = type,
            Source = source,
            TenantId = ctx.TenantId,
            EnvironmentId = ctx.EnvironmentId,
            Entity = entity,
            RecordId = recordId,
            RecordRevision = recordRevision,
            ConfigRevision = configRevision,
            OccurredAt = DateTimeOffset.UtcNow,
            CorrelationId = ctx.CorrelationId,
            CausationId = causation,
            Actor = ctx.PrincipalId == Guid.Empty ? "system" : ctx.ActorRef,
            Severity = severity,
            Sensitivity = EventSensitivities.Internal,
            Payload = payload,
        });

    /// <summary>Domain events emitted by a record write (transition emits, emit_event rules), caused by the record event.</summary>
    public static void AddEmitted(KernelDbContext db, RequestContext ctx, EffectiveConfig config, string entity, Guid recordId, int revision, int configRevision, IEnumerable<EmittedEvent> emitted, Guid causation)
    {
        foreach (var e in emitted)
        {
            Add(db, new EventEnvelope
            {
                EventId = Guid.NewGuid(),
                Type = e.Type,
                TypeVersion = e.TypeVersion,
                Source = SourceOf(config, e.Type) == Composer.TenantLayer ? Composer.TenantLayer : EmitterSource(config, e.Emitter),
                Emitter = e.Emitter,
                TenantId = ctx.TenantId,
                EnvironmentId = ctx.EnvironmentId,
                Entity = entity,
                RecordId = recordId,
                RecordRevision = revision,
                ConfigRevision = configRevision,
                OccurredAt = DateTimeOffset.UtcNow,
                CorrelationId = ctx.CorrelationId,
                CausationId = causation,
                Actor = ctx.ActorRef,
                Severity = e.Severity,
                Sensitivity = e.Sensitivity,
                Payload = e.Payload,
            });
        }
    }
}
