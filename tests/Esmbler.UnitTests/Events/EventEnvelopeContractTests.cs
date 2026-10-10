using System.Text.Json;
using Esmbler.Sdk.Events;
using Esmbler.Sdk.Specs;

namespace Esmbler.UnitTests.Events;

/// <summary>REQ-EVT-003, REQ-EVT-008 (ADR 0017): consumers read envelope v1 (Gate 1 outbox rows) and v2 alike.</summary>
public class EventEnvelopeContractTests
{
    private const string V1 = """
        {"eventId":"0b9c6d1e-2f43-4c1e-9d64-3c1f1d6f3a10","type":"record.transitioned","schemaVersion":1,
         "tenantId":"7d0f2c8a-1b2e-4f6e-8a9b-0c1d2e3f4a5b","environmentId":"1a2b3c4d-5e6f-4a1b-8c9d-0e1f2a3b4c5d",
         "entity":"core.tickets.ticket","recordId":"9f8e7d6c-5b4a-4c3d-8e2f-1a0b9c8d7e6f","recordRevision":3,"configRevision":2,
         "occurredAt":"2026-06-01T10:00:00.000Z","correlationId":"2c3d4e5f-6a7b-4c8d-9e0f-1a2b3c4d5e6f","actor":"user:abc",
         "payload":{"from":"new","to":"in_progress"}}
        """;

    [Fact]
    public void A_version_1_envelope_reads_with_empty_v2_fields()
    {
        var e = JsonSerializer.Deserialize<EventEnvelope>(V1, SpecJson.Options)!;
        Assert.Equal(1, e.SchemaVersion);
        Assert.Equal(EventTypes.RecordTransitioned, e.Type);
        Assert.Equal(3, e.RecordRevision);
        Assert.Null(e.Source);
        Assert.Null(e.Severity);
        Assert.Null(e.RecordedAt);
        Assert.Equal(1, e.TypeVersion);
        Assert.Equal("in_progress", e.Payload["to"]!.GetValue<string>());
    }

    [Fact]
    public void A_version_2_envelope_round_trips_with_its_new_fields()
    {
        var e = JsonSerializer.Deserialize<EventEnvelope>(V1, SpecJson.Options)! with
        {
            SchemaVersion = 2,
            Source = "core.tickets",
            TypeVersion = 2,
            Severity = EventSeverities.High,
            Sensitivity = EventSensitivities.Internal,
            Traceparent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            RecordedAt = DateTimeOffset.Parse("2026-06-01T10:00:01Z", System.Globalization.CultureInfo.InvariantCulture),
        };
        var json = JsonSerializer.Serialize(e, SpecJson.Options);
        Assert.Contains("\"schemaVersion\":2", json, StringComparison.Ordinal);
        var back = JsonSerializer.Deserialize<EventEnvelope>(json, SpecJson.Options)!;
        Assert.Equal((e.Source, e.TypeVersion, e.Severity, e.Sensitivity, e.Traceparent, e.RecordedAt), (back.Source, back.TypeVersion, back.Severity, back.Sensitivity, back.Traceparent, back.RecordedAt));
    }

    [Fact]
    public void Severities_are_ordered()
    {
        Assert.Equal(EventSeverities.All, EventSeverities.All.OrderBy(EventSeverities.Rank).ToList());
        Assert.True(EventSeverities.Rank(EventSeverities.Critical) > EventSeverities.Rank(EventSeverities.High));
    }
}
