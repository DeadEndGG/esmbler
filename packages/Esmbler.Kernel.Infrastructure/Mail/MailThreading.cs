using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Mail;

/// <summary>
/// Email threading (ADR 0023). Outbound email carries an opaque random Message-ID kept on the delivery; a reply's
/// In-Reply-To or References leads back to the delivery's record, or to the record an earlier inbound message created.
/// The record number in the subject ([TCK-000123]) is the fallback. Finding a record never grants anything: the sender
/// still needs the right to reply on it.
/// </summary>
public static class MailThreading
{
    /// <summary>A new Message-ID in the sending address's domain (no record, person or event in it).</summary>
    public static string NewMessageId(string? fromAddress)
    {
        var at = fromAddress?.LastIndexOf('@') ?? -1;
        var domain = at > 0 && at < fromAddress!.Length - 1 ? fromAddress[(at + 1)..].Trim().ToLowerInvariant() : "esmbler.invalid";
        return $"{Guid.NewGuid():N}@{domain}";
    }

    /// <summary>
    /// For an email about a record: the record's first inbound Message-ID (so the requester's mail client threads the
    /// conversation) and the workspace mailbox that takes replies for the record's entity.
    /// </summary>
    public static async Task<(IReadOnlyList<string> References, string? ReplyTo)> ForRecordAsync(KernelDbContext db, ConfigStore configs, Guid tenantId, Guid environmentId, string? entityId, Guid? recordId, CancellationToken ct)
    {
        if (entityId is null || recordId is null)
        {
            return ([], null);
        }

        var first = await db.InboundMessages.AsNoTracking()
            .Where(m => m.RecordId == recordId && m.MessageId != null && m.Outcome == "created")
            .OrderBy(m => m.ReceivedAt).Select(m => m.MessageId).FirstOrDefaultAsync(ct);
        var config = (await configs.GetActiveAsync(db, tenantId, environmentId, ct)).Config;
        var intakes = config.MailIntakes.Where(i => i.Enabled && i.Entity == entityId).Select(i => i.Id).ToList();
        var replyTo = intakes.Count == 0 ? null : await db.MailInboxes.AsNoTracking()
            .Where(i => i.Mode != "disabled" && intakes.Contains(i.IntakeId)).OrderBy(i => i.Key).Select(i => i.Address).FirstOrDefaultAsync(ct);
        return (first is null ? [] : [first], replyTo);
    }

    /// <summary>
    /// The record (of the intake's entity, not deleted) a reply belongs to, and how it was found: "header" (Message-ID)
    /// or "subject" (record number).
    /// </summary>
    public static async Task<(Guid? Record, string? By)> FindAsync(KernelDbContext db, EffectiveConfig config, string entityId, ParsedMail mail, CancellationToken ct)
    {
        var ids = mail.ThreadIds.ToList();
        if (ids.Count > 0)
        {
            var byDelivery = await db.NotificationDeliveries.AsNoTracking()
                .Where(d => d.MessageId != null && ids.Contains(d.MessageId) && d.EntityId == entityId && d.RecordId != null)
                .Select(d => d.RecordId).FirstOrDefaultAsync(ct);
            var byInbound = byDelivery ?? await db.InboundMessages.AsNoTracking()
                .Where(m => m.MessageId != null && ids.Contains(m.MessageId) && m.EntityId == entityId && m.RecordId != null)
                .Select(m => m.RecordId).FirstOrDefaultAsync(ct);
            if (byInbound is { } threaded && await db.Records.AnyAsync(r => r.Id == threaded && r.EntityId == entityId && r.DeletedAt == null, ct))
            {
                return (threaded, "header");
            }
        }

        var numberField = config.FindEntity(entityId)?.Fields.Find(f => f.Type == Sdk.Specs.FieldTypes.AutoNumber)?.Key;
        if (numberField is null)
        {
            return (null, null);
        }

        foreach (var number in MailGuard.NumbersIn(mail.Subject).Take(3))
        {
            var id = await db.Records.FromSqlInterpolated($"SELECT * FROM kernel.records WHERE entity_id = {entityId} AND deleted_at IS NULL AND data ->> {numberField} = {number} LIMIT 1")
                .AsNoTracking().Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
            if (id is not null)
            {
                return (id, "subject");
            }
        }

        return (null, null);
    }
}
