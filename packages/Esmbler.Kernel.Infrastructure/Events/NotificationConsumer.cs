using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Events;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Events;

/// <summary>
/// Renders notification templates for record events into the in-app fake channel (Gate 1; email in Gate 2).
/// Templates fire on "record.transitioned:&lt;state&gt;" or "record.updated:&lt;field&gt;". Each recipient only sees fields
/// they may read (derived surfaces follow permissions); the unique key makes redelivery idempotent.
/// </summary>
public sealed partial class NotificationConsumer(ConfigStore configs, AccessService access) : IEventConsumer
{
    public string Name => "notifications.templates";

    public bool Handles(EventEnvelope envelope) => envelope.Type is EventTypes.RecordTransitioned or EventTypes.RecordUpdated or EventTypes.RecordCreated;

    public async Task HandleAsync(EventEnvelope envelope, KernelDbContext db, CancellationToken ct)
    {
        var artifact = await configs.GetActiveAsync(db, envelope.TenantId, envelope.EnvironmentId, ct);
        var triggers = new HashSet<string>(StringComparer.Ordinal) { envelope.Type };
        if (envelope.Payload["to"] is JsonValue to)
        {
            triggers.Add($"{EventTypes.RecordTransitioned}:{to}");
        }

        if (envelope.Payload["changed"] is JsonArray changed)
        {
            foreach (var key in changed.OfType<JsonValue>())
            {
                triggers.Add($"{EventTypes.RecordUpdated}:{key}");
            }
        }

        var templates = artifact.Config.NotificationTemplates.Where(t => t.Enabled && t.Entity == envelope.Entity && triggers.Contains(t.Event)).ToList();
        if (templates.Count == 0)
        {
            return;
        }

        var entity = artifact.Config.FindEntity(envelope.Entity!)!;
        var row = await db.Records.FirstOrDefaultAsync(r => r.Id == envelope.RecordId, ct);
        if (row is null || row.DeletedAt is not null)
        {
            return;
        }

        var data = JsonNode.Parse(row.Data)!.AsObject();
        foreach (var template in templates)
        {
            foreach (var recipientField in template.Recipients.Where(r => r != "watchers"))
            {
                if (data[recipientField] is not JsonValue v || !v.TryGetValue<string>(out var raw) || !Guid.TryParse(raw, out var recipient))
                {
                    continue;
                }

                if (await db.Notifications.AnyAsync(n => n.EventId == envelope.EventId && n.TemplateId == template.Id && n.Recipient == recipient, ct))
                {
                    continue;
                }

                EffectivePermissions perms;
                try
                {
                    perms = await access.ForAsync(db, new RequestContext { TenantId = envelope.TenantId, EnvironmentId = envelope.EnvironmentId, PrincipalId = recipient, Channel = "worker" }, artifact.Config, ct);
                }
                catch (KernelException)
                {
                    continue; // not a member any more
                }

                if (!perms.CanRead(entity, data))
                {
                    continue;
                }

                db.Notifications.Add(new NotificationRow
                {
                    TenantId = envelope.TenantId,
                    EnvironmentId = envelope.EnvironmentId,
                    Id = Guid.NewGuid(),
                    TemplateId = template.Id,
                    Recipient = recipient,
                    Subject = Render(template.Subject, entity, data, perms),
                    Body = Render(template.Body, entity, data, perms),
                    EventId = envelope.EventId,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static string Render(string template, EntitySpec entity, JsonObject data, EffectivePermissions perms) =>
        Placeholder().Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            if (perms.FieldAccess(entity, key) == FieldAccessLevels.Hidden)
            {
                return string.Empty;
            }

            return data[key] switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                null => string.Empty,
                var other => other.ToJsonString(),
            };
        });

    [GeneratedRegex(@"\{\{\s*record\.([a-z][a-z0-9_]*)\s*\}\}")]
    private static partial Regex Placeholder();
}
