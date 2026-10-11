using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Events;
using Esmbler.Kernel.Infrastructure.Identity;
using Esmbler.Kernel.Infrastructure.Notifications;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Records;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Esmbler.Kernel.Infrastructure.Mail;

public sealed record MailInboxRequest(string Key, string Label, string Address, string Intake, string Mode, JsonObject? Settings, int? Version);

/// <summary>A mailbox as administrators see it: never its token, only whether its secrets are configured.</summary>
public sealed record MailInboxView(string Key, string Label, string Address, string Intake, string Mode, JsonObject Settings, bool HasToken, IReadOnlyDictionary<string, bool> Secrets, bool HostAllowed, DateTimeOffset? PolledAt, string? PollError, int Version, DateTimeOffset UpdatedAt);

/// <summary>The token is returned once, when it is created or rotated.</summary>
public sealed record MailInboxSaved(MailInboxView Inbox, string? Token);

public sealed record InboundMessageView(Guid Id, string Inbox, string? From, string? FromName, string? Subject, string Status, string? Reason, string? Outcome, Guid? RecordId, string? EntityId, DateTimeOffset ReceivedAt, DateTimeOffset? ProcessedAt);

public sealed record InboundMessageDetail(InboundMessageView Message, string? Text, IReadOnlyList<string> To, IReadOnlyList<string> Cc, string? Verification, IReadOnlyList<string> AutoSignals, IReadOnlyList<MailAttachment> Attachments);

public sealed record MailReceipt(Guid Id, bool Duplicate);

/// <summary>A mail intake of the published configuration, for choosing one in mailbox settings.</summary>
public sealed record MailIntakeOption(string Id, string Label, string Entity, bool Enabled);

/// <summary>A reviewer's decision to process a suspended message anyway, optionally naming the requester for an unknown sender.</summary>
public sealed record MailDecision(RequestContext Reviewer, Guid? Requester);

/// <summary>
/// Email intake (ADR 0023, REQ-BIZ-003). Receiving stores a message once per mailbox and Message-ID and schedules
/// <c>mail.process</c>; processing applies the guards in order (sender, own mail, automatic mail, rate per sender,
/// verification, membership), then threads: a reply becomes the sender's public reply on the record (and may reopen it
/// or start a follow-up), anything else creates a record as the sender. Every write runs through the record service with
/// the sender's own permissions. Suspended messages wait for people who work the intake's entity (agents) to accept or
/// reject them; mailboxes are managed with notifications.manage or permission administration. Agents (AI) are refused.
/// </summary>
public sealed partial class MailIntakeService(Database database, ConfigStore configs, AccessService access, RecordService records, IRichTextSanitizer sanitizer, ISecretStore secrets, NotificationOptions options, IClock clock)
{
    public const string ProcessKind = "mail.process";
    public const int DefaultMaxPerSenderPerHour = 20;
    private const string TokenPrefix = "esm_mail_";
    private static readonly string[] SettingKeys = ["senderVerification", "authservId", "maxPerSenderPerHour", "host", "port", "security", "folder", "usernameRef", "passwordRef"];
    private static readonly string[] SecretRefSettings = ["usernameRef", "passwordRef"];

    // ---- Receiving -------------------------------------------------------------------------------------------------

    /// <summary>The workspace mailbox a webhook token names, or null (no session: the token is the credential).</summary>
    public async Task<(Guid TenantId, Guid EnvironmentId, Guid InboxId)?> ResolveTokenAsync(string? token, CancellationToken ct)
    {
        if (token is null || !token.StartsWith(TokenPrefix, StringComparison.Ordinal) || token.Length is < 20 or > 200)
        {
            return null;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var rows = await database.AnonymousAsync(db => db.Database.SqlQuery<TokenRow>($"""SELECT tenant_id AS "TenantId", environment_id AS "EnvironmentId", inbox_id AS "InboxId" FROM kernel.mail_inbox_by_token({hash})""").ToListAsync(ct), ct);
        return rows.Count == 1 ? (rows[0].TenantId, rows[0].EnvironmentId, rows[0].InboxId) : null;
    }

    /// <summary>Stores a message once per mailbox and Message-ID (or content hash) and schedules its processing.</summary>
    public async Task<MailReceipt> ReceiveAsync(Guid tenantId, Guid environmentId, Guid inboxId, byte[] raw, CancellationToken ct)
    {
        if (raw.Length == 0 || raw.Length > MailParser.MaxBytes)
        {
            throw new KernelException("mail_too_large", $"A message must be between 1 byte and {MailParser.MaxBytes / (1024 * 1024)} MB.", 413);
        }

        string? messageId;
        try
        {
            messageId = MailParser.MessageIdOf(raw);
        }
        catch (FormatException)
        {
            messageId = null;
        }

        var key = MailParser.KeyOf(raw, messageId);
        try
        {
            return await database.InTenantAsync(tenantId, environmentId, null, async db =>
            {
                if (!await db.MailInboxes.AnyAsync(i => i.Id == inboxId && i.Mode != "disabled", ct))
                {
                    throw KernelException.NotFound("Mailbox");
                }

                if (await db.InboundMessages.Where(m => m.InboxId == inboxId && m.MessageKey == key).Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct) is { } known)
                {
                    return new MailReceipt(known, true);
                }

                var row = new InboundMessageRow
                {
                    TenantId = tenantId,
                    EnvironmentId = environmentId,
                    Id = Guid.NewGuid(),
                    InboxId = inboxId,
                    MessageKey = key,
                    MessageId = messageId is { Length: <= 250 } ? messageId : null,
                    Status = "received",
                    Raw = raw,
                    SizeBytes = raw.Length,
                    ReceivedAt = clock.UtcNow,
                };
                db.InboundMessages.Add(row);
                await JobScheduler.ScheduleAsync(db, tenantId, environmentId, new JobRequest(ProcessKind, new JsonObject { ["message"] = row.Id.ToString() }) { DedupKey = $"mail:{row.Id}" }, ct);
                await db.SaveChangesAsync(ct);
                return new MailReceipt(row.Id, false);
            }, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two deliveries of one message raced: the other stored it.
            var id = await database.InTenantAsync(tenantId, environmentId, null, db => db.InboundMessages.Where(m => m.InboxId == inboxId && m.MessageKey == key).Select(m => m.Id).FirstAsync(ct), ct);
            return new MailReceipt(id, true);
        }
    }

    // ---- Processing ------------------------------------------------------------------------------------------------

    private sealed record Plan(InboundMessageRow Row, MailIntakeSpec Intake, EntitySpec Entity, ParsedMail Mail, string Sender, Guid? SenderId, Guid? RecordId, string? State, string Text, string Subject, bool FromRequester, bool ActAsSender);

    /// <summary>Processes a received message (the job), or a suspended one a reviewer accepted.</summary>
    public async Task ProcessAsync(Guid tenantId, Guid environmentId, Guid messageId, MailDecision? decision, CancellationToken ct)
    {
        var plan = await database.InTenantAsync(tenantId, environmentId, decision?.Reviewer.PrincipalId, db => PlanAsync(db, tenantId, environmentId, messageId, decision, ct), ct);
        if (plan is null)
        {
            return;
        }

        // Writes go through the record service as the sender (or, for an unknown sender a reviewer accepted, as the
        // reviewer on the requester's behalf), so every rule and permission applies.
        // A reviewer's decision never makes the platform act as the claimed sender of a message nobody verified: the
        // reviewer acts (with their own rights) on the sender's behalf.
        var onBehalf = !plan.ActAsSender;
        var actor = new RequestContext
        {
            TenantId = tenantId,
            EnvironmentId = environmentId,
            PrincipalId = onBehalf ? decision!.Reviewer.PrincipalId : plan.SenderId!.Value,
            Channel = onBehalf ? decision!.Reviewer.Channel : "email",
            CorrelationId = decision?.Reviewer.CorrelationId ?? Guid.NewGuid(),
        };
        try
        {
            if (plan.RecordId is { } recordId && !plan.Intake.FollowUpFrom.Contains(plan.State ?? string.Empty))
            {
                await ReplyAsync(plan, actor, recordId, onBehalf, ct);
            }
            else
            {
                await CreateAsync(plan, actor, decision?.Requester ?? plan.SenderId ?? actor.PrincipalId, plan.RecordId is not null, ct);
            }
        }
        catch (ValidationFailedException ex)
        {
            await SuspendAsync(tenantId, environmentId, messageId, MailReasons.InvalidRecord, new JsonObject { ["fields"] = new JsonArray([.. ex.Errors.Select(e => JsonValue.Create($"{e.Field}: {e.Message}"))]) }, ct);
        }
        catch (KernelException ex) when (ex.Status is 403 or 404)
        {
            await SuspendAsync(tenantId, environmentId, messageId, MailReasons.NotAllowed, null, ct);
        }
    }

    private async Task<Plan?> PlanAsync(KernelDbContext db, Guid tenantId, Guid environmentId, Guid messageId, MailDecision? decision, CancellationToken ct)
    {
        var row = (await db.InboundMessages.FromSqlInterpolated($"SELECT * FROM kernel.inbound_messages WHERE id = {messageId} FOR UPDATE").AsTracking().ToListAsync(ct)).FirstOrDefault();
        if (row is null || row.Status != (decision is null ? "received" : "suspended"))
        {
            return null;
        }

        var details = JsonNode.Parse(row.Details) as JsonObject ?? [];
        Plan? Stop(string status, string reason)
        {
            row.Status = status;
            row.Reason = reason;
            row.ProcessedAt = clock.UtcNow;
            row.Details = details.ToJsonString();
            AuditWriter.AddSystem(db, tenantId, environmentId, status == "rejected" ? "mail.rejected" : "mail.suspended", "inbound_message", row.Id.ToString(), new { reason, inbox = row.InboxId }, outcome: "denied");
            return null;
        }

        async Task<Plan?> StopAsync(string status, string reason)
        {
            var result = Stop(status, reason);
            await db.SaveChangesAsync(ct);
            return result;
        }

        var inbox = await db.MailInboxes.AsNoTracking().FirstOrDefaultAsync(i => i.Id == row.InboxId, ct);
        var artifact = await configs.GetActiveAsync(db, tenantId, environmentId, ct);
        var intake = inbox is null ? null : artifact.Config.FindMailIntake(inbox.IntakeId);
        var entity = intake is { Enabled: true } ? artifact.Config.FindEntity(intake.Entity) : null;
        if (inbox is null || inbox.Mode == "disabled" || intake is null || entity is null)
        {
            return await StopAsync("suspended", MailReasons.NoIntake);
        }

        ParsedMail mail;
        try
        {
            mail = MailParser.Parse(row.Raw ?? []);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
        {
            return await StopAsync("failed", MailReasons.Unreadable);
        }

        var settings = JsonNode.Parse(inbox.Settings) as JsonObject ?? [];
        var sender = mail.From.Count == 1 && EmailAddress().IsMatch(mail.From[0].Address) ? mail.From[0].Address.ToLowerInvariant() : null;
        var first = mail.From.Count > 0 ? mail.From[0] : default;
        row.FromAddress = sender ?? (first.Address is { } raw ? Cut(raw, 254) : null);
        row.FromName = first.Name is { } name ? Cut(Controls().Replace(name, " ").Trim(), 200) : null;
        row.Subject = Cut(MailGuard.CleanSubject(mail.Subject), 500);
        details["to"] = new JsonArray([.. mail.To.Select(a => JsonValue.Create(a))]);
        details["cc"] = new JsonArray([.. mail.Cc.Select(a => JsonValue.Create(a))]);
        details["autoSignals"] = new JsonArray([.. mail.AutoSignals.Select(a => JsonValue.Create(a))]);
        details["attachments"] = new JsonArray([.. mail.Attachments.Select(a => new JsonObject { ["name"] = a.Name is null ? null : Cut(a.Name, 200), ["contentType"] = a.ContentType, ["size"] = a.Size })]);
        if (sender is null)
        {
            return await StopAsync("suspended", MailReasons.BadSender);
        }

        if (decision is null)
        {
            // Mail the platform sent, or a workspace mailbox sending to another: never processed (loops).
            var own = (mail.MessageId is { } mid && await db.NotificationDeliveries.AnyAsync(d => d.MessageId == mid, ct))
                || await db.MailInboxes.AnyAsync(i => EF.Functions.ILike(i.Address, Like(sender), "\\"), ct)
                || (await db.NotificationChannels.AsNoTracking().Where(c => c.Type == "email").Select(c => c.Settings).ToListAsync(ct))
                    .Any(s => (JsonNode.Parse(s)?["from"]?.GetValue<string>() ?? string.Empty).Equals(sender, StringComparison.OrdinalIgnoreCase));
            if (own)
            {
                return await StopAsync("rejected", MailReasons.OwnMessage);
            }

            if (mail.AutoSignals.Count > 0)
            {
                return await StopAsync("rejected", MailReasons.AutoReply);
            }

            var limit = settings["maxPerSenderPerHour"]?.GetValue<int>() ?? DefaultMaxPerSenderPerHour;
            var since = clock.UtcNow.AddHours(-1);
            // Only mail that was processed counts, so forged mail in someone's name cannot use up their allowance.
            var recent = await db.InboundMessages.CountAsync(m => m.InboxId == row.InboxId && m.Id != row.Id && m.Status == "processed" && m.FromAddress != null && EF.Functions.ILike(m.FromAddress, Like(sender), "\\") && m.ReceivedAt > since, ct);
            if (recent >= limit)
            {
                return await StopAsync("suspended", MailReasons.TooManyFromSender);
            }

            if (settings["senderVerification"]?.GetValue<string>() != "none")
            {
                var (verified, detail) = MailGuard.Verify(mail.AuthenticationResults, sender, settings["authservId"]?.GetValue<string>());
                details["verification"] = detail;
                details["verified"] = verified;
                if (!verified)
                {
                    return await StopAsync("suspended", MailReasons.UnverifiedSender);
                }
            }
            else
            {
                details["verification"] = "not required by the mailbox";
            }
        }

        var text = MailGuard.NewText(mail.Text);
        var subject = MailGuard.CleanSubject(mail.Subject);
        if (text.Length == 0 && subject.Length == 0)
        {
            return await StopAsync(decision is null ? "rejected" : "suspended", MailReasons.Empty);
        }

        // People only: an enabled user who is a member of the workspace.
        var senderId = await db.Principals.AsNoTracking()
            .Where(p => p.Kind == "user" && !p.Disabled && p.Email != null && EF.Functions.ILike(p.Email, Like(sender), "\\"))
            .Where(p => db.Memberships.Any(m => m.PrincipalId == p.Id && m.TenantId == tenantId))
            .Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        if (senderId is null && decision?.Requester is null)
        {
            return await StopAsync("suspended", MailReasons.UnknownSender);
        }

        if (decision is not null)
        {
            if (decision.Requester is { } requester && !await db.Memberships.AnyAsync(m => m.PrincipalId == requester && m.TenantId == tenantId, ct))
            {
                throw new KernelException("bad_requester", "The requester must be a member of this workspace.", 400, "requester");
            }

            details["acceptedBy"] = decision.Reviewer.PrincipalId.ToString();
            AuditWriter.Add(db, decision.Reviewer, "mail.accept", "inbound_message", row.Id.ToString(), artifact.Revision, new { reason = row.Reason, onBehalf = senderId is null });
        }

        var (recordId, threadedBy) = await MailThreading.FindAsync(db, artifact.Config, entity.Id, mail, ct);
        string? state = null;
        var fromRequester = false;
        if (recordId is { } found)
        {
            var data = JsonNode.Parse(await db.Records.AsNoTracking().Where(r => r.Id == found).Select(r => r.Data).FirstAsync(ct))?.AsObject();
            state = artifact.Config.Workflows.Find(w => w.Entity == entity.Id) is { } wf ? data?[wf.StateField]?.GetValue<string>() : null;
            fromRequester = senderId is { } s && data?[intake.RequesterField]?.GetValue<string>() == s.ToString();
        }

        details["threadedTo"] = recordId?.ToString();
        details["threadedBy"] = threadedBy;
        row.Details = details.ToJsonString();
        row.EntityId = entity.Id;
        await db.SaveChangesAsync(ct);
        // The job acts as a member sender (verified, or the mailbox does not require it); a reviewer's decision does so
        // only for a message whose sender was verified.
        var actAsSender = senderId is not null && (decision is null || details["verified"]?.GetValue<bool>() == true);
        return new Plan(row, intake, entity, mail, sender, senderId, recordId, state, text, subject, fromRequester && actAsSender, actAsSender);
    }

    private async Task ReplyAsync(Plan plan, RequestContext actor, Guid recordId, bool onBehalf, CancellationToken ct)
    {
        // Only the requester's own email becomes a public reply. Anyone else (an agent answering an escalation email, a
        // colleague in copy) adds an internal note, which needs the right to write notes, so nothing reaches the
        // requester by accident; a reply from a non-member a reviewer accepted is kept as the reviewer's internal note.
        var kind = onBehalf || !plan.FromRequester ? "work_note" : "public_reply";
        var body = onBehalf ? $"<p>Email from {System.Net.WebUtility.HtmlEncode(plan.Sender)}:</p>{MailGuard.ToRichText(plan.Text)}" : MailGuard.ToRichText(plan.Text.Length > 0 ? plan.Text : plan.Subject);
        await records.AddActivityAsync(actor, plan.Entity.Id, recordId, kind, body, sanitizer, "email", async (db, activityId) =>
        {
            var updated = await db.InboundMessages.Where(m => m.Id == plan.Row.Id && m.Status == plan.Row.Status)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, "processed").SetProperty(m => m.Reason, (string?)null).SetProperty(m => m.Outcome, "replied")
                    .SetProperty(m => m.RecordId, recordId).SetProperty(m => m.ActivityId, activityId).SetProperty(m => m.ProcessedAt, clock.UtcNow), ct);
            if (updated == 0)
            {
                // Someone decided the message meanwhile (or another run processed it): the reply is not added.
                throw KernelException.Conflict("mail_decided", "This message was already processed or decided.");
            }
        }, ct);

        // A reply to a resolved record reopens it, as the sender (their own right to run the transition decides).
        if (kind == "public_reply" && plan.Intake.ReopenTransition is { } transition && plan.Intake.ReopenFrom.Contains(plan.State ?? string.Empty))
        {
            try
            {
                var current = await records.GetAsync(actor, plan.Entity.Id, recordId, ct);
                await records.TransitionAsync(actor, plan.Entity.Id, recordId, current.Revision, transition, [], null, $"mail:{plan.Row.Id}:reopen", ct);
                await database.InTenantAsync(actor.TenantId, actor.EnvironmentId, null, db =>
                    db.InboundMessages.Where(m => m.Id == plan.Row.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Outcome, "reopened"), ct), ct);
            }
            catch (KernelException)
            {
                // The reply stands; the record stays as it is (the sender may not reopen it, or it moved meanwhile).
            }
        }
    }

    private async Task CreateAsync(Plan plan, RequestContext actor, Guid requester, bool followUp, CancellationToken ct)
    {
        var intake = plan.Intake;
        var data = new JsonObject();
        foreach (var (key, value) in intake.Set)
        {
            data[key] = value?.DeepClone();
        }

        var title = plan.Subject.Length > 0 ? plan.Subject : Cut(plan.Text.Split('\n')[0], 120);
        var titleMax = plan.Entity.FindField(intake.TitleField)?.MaxLength ?? 200;
        data[intake.TitleField] = Cut(title, Math.Max(10, titleMax));
        if (intake.BodyField is { } bodyField && plan.Text.Length > 0)
        {
            var rich = plan.Entity.FindField(bodyField)?.Type == FieldTypes.RichText;
            data[bodyField] = rich ? MailGuard.ToRichText(plan.Text) : plan.Text;
        }

        // The sender creating their own record gets the field's default (themselves); only a reviewer acting for an
        // unknown sender names the requester, which their own rights must allow.
        if (requester != actor.PrincipalId)
        {
            data[intake.RequesterField] = requester.ToString();
        }
        if (intake.AddressField is { } addressField)
        {
            data[addressField] = plan.Sender;
        }

        // Idempotent: a retried job finds the record it already created.
        var created = await records.CreateAsync(actor, plan.Entity.Id, data, null, $"mail:{plan.Row.Id}", ct);
        await database.InTenantAsync(actor.TenantId, actor.EnvironmentId, null, async db =>
        {
            await db.InboundMessages.Where(m => m.Id == plan.Row.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, "processed").SetProperty(m => m.Reason, (string?)null).SetProperty(m => m.Outcome, followUp ? "follow_up" : "created")
                    .SetProperty(m => m.RecordId, created.Id).SetProperty(m => m.ProcessedAt, clock.UtcNow), ct);
            return 0;
        }, ct);
    }

    private Task SuspendAsync(Guid tenantId, Guid environmentId, Guid messageId, string reason, JsonObject? extra, CancellationToken ct) =>
        database.InTenantAsync(tenantId, environmentId, null, async db =>
        {
            var row = await db.InboundMessages.AsTracking().FirstAsync(m => m.Id == messageId, ct);
            if (row.Status == "processed")
            {
                return 0;
            }

            var details = JsonNode.Parse(row.Details) as JsonObject ?? [];
            foreach (var (key, value) in extra ?? [])
            {
                details[key] = value?.DeepClone();
            }

            row.Status = "suspended";
            row.Reason = reason;
            row.ProcessedAt = clock.UtcNow;
            row.Details = details.ToJsonString();
            AuditWriter.AddSystem(db, tenantId, environmentId, "mail.suspended", "inbound_message", messageId.ToString(), new { reason, inbox = row.InboxId }, outcome: "denied");
            await db.SaveChangesAsync(ct);
            return 0;
        }, ct);

    internal Task FailAsync(Guid tenantId, Guid environmentId, Guid messageId, CancellationToken ct) =>
        database.InTenantAsync(tenantId, environmentId, null, db =>
            db.InboundMessages.Where(m => m.Id == messageId && m.Status == "received")
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, "failed").SetProperty(m => m.Reason, MailReasons.ProcessingFailed).SetProperty(m => m.ProcessedAt, clock.UtcNow), ct), ct);

    // ---- Review (people who work the intake's entity) --------------------------------------------------------------

    public Task<IReadOnlyList<InboundMessageView>> ListAsync(RequestContext ctx, string? status, CancellationToken ct) =>
        database.InTenantAsync<IReadOnlyList<InboundMessageView>>(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var inboxes = await ReviewableAsync(db, ctx, ct);
            var ids = inboxes.Keys.ToList();
            var query = db.InboundMessages.AsNoTracking().Where(m => ids.Contains(m.InboxId));
            query = status is null or "" ? query.Where(m => m.Status == "suspended") : status == "all" ? query : query.Where(m => m.Status == status);
            var rows = await query.OrderByDescending(m => m.ReceivedAt).Take(200).ToListAsync(ct);
            return rows.Select(r => View(r, inboxes[r.InboxId])).ToList();
        }, ct);

    public Task<InboundMessageDetail> GetAsync(RequestContext ctx, Guid id, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var inboxes = await ReviewableAsync(db, ctx, ct);
            var row = await db.InboundMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
            if (row is null || !inboxes.TryGetValue(row.InboxId, out var key))
            {
                throw KernelException.NotFound("Message");
            }

            string? text = null;
            if (row.Raw is { } raw)
            {
                try
                {
                    text = MailGuard.NewText(MailParser.Parse(raw).Text);
                }
                catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
                {
                    text = null;
                }
            }

            var details = JsonNode.Parse(row.Details) as JsonObject ?? [];
            static IReadOnlyList<string> Strings(JsonNode? node) => (node as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>().ToList();
            var attachments = (details["attachments"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(a => new MailAttachment(a["name"]?.GetValue<string>(), a["contentType"]?.GetValue<string>() ?? "application/octet-stream", a["size"]?.GetValue<long>() ?? 0)).ToList();
            return new InboundMessageDetail(View(row, key), text, Strings(details["to"]), Strings(details["cc"]), details["verification"]?.GetValue<string>(), Strings(details["autoSignals"]), attachments);
        }, ct);

    public async Task<InboundMessageView> AcceptAsync(RequestContext ctx, Guid id, Guid? requester, CancellationToken ct)
    {
        await RequireReviewerAsync(ctx, id, ct);
        await ProcessAsync(ctx.TenantId, ctx.EnvironmentId, id, new MailDecision(ctx, requester), ct);
        return (await GetAsync(ctx, id, ct)).Message;
    }

    public Task<InboundMessageView> RejectAsync(RequestContext ctx, Guid id, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var inboxes = await ReviewableAsync(db, ctx, ct);
            var row = (await db.InboundMessages.FromSqlInterpolated($"SELECT * FROM kernel.inbound_messages WHERE id = {id} FOR UPDATE").AsTracking().ToListAsync(ct)).FirstOrDefault();
            if (row is null || !inboxes.TryGetValue(row.InboxId, out var key))
            {
                throw KernelException.NotFound("Message");
            }

            if (row.Status != "suspended")
            {
                throw KernelException.Conflict("mail_decided", "This message was already processed or decided.", suggestion: "Refresh the list.");
            }

            row.Status = "rejected";
            row.Reason = MailReasons.Rejected;
            row.DecidedBy = ctx.PrincipalId;
            row.ProcessedAt = clock.UtcNow;
            if (!await db.LegalHolds.AnyAsync(h => h.ReleasedAt == null, ct))
            {
                row.Raw = null;
            }

            AuditWriter.Add(db, ctx, "mail.reject", "inbound_message", row.Id.ToString(), null, new { inbox = key });
            await db.SaveChangesAsync(ct);
            return View(row, key);
        }, ct);

    private Task RequireReviewerAsync(RequestContext ctx, Guid id, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var inboxes = await ReviewableAsync(db, ctx, ct);
            var inbox = await db.InboundMessages.AsNoTracking().Where(m => m.Id == id).Select(m => (Guid?)m.InboxId).FirstOrDefaultAsync(ct);
            if (inbox is null || !inboxes.ContainsKey(inbox.Value))
            {
                throw KernelException.NotFound("Message");
            }

            return 0;
        }, ct);

    /// <summary>Mailboxes (id → key) whose intake entity the person works: they read all its records and may create them.</summary>
    private async Task<Dictionary<Guid, string>> ReviewableAsync(KernelDbContext db, RequestContext ctx, CancellationToken ct)
    {
        if (ctx.AgentId is not null)
        {
            throw KernelException.Forbidden("AI agents cannot review inbound email.");
        }

        var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
        var perms = await access.ForAsync(db, ctx, artifact.Config, ct, fresh: true);
        var works = artifact.Config.MailIntakes
            .Where(i => artifact.Config.FindEntity(i.Entity) is { } entity && perms.Entity(entity.Id).Read == RecordScopes.All && perms.CanCreate(entity))
            .Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        if (works.Count == 0)
        {
            throw KernelException.Forbidden("Reviewing inbound email is for people who work the mailbox's records.");
        }

        var inboxes = await db.MailInboxes.AsNoTracking().Select(i => new { i.Id, i.Key, i.IntakeId }).ToListAsync(ct);
        return inboxes.Where(i => works.Contains(i.IntakeId)).ToDictionary(i => i.Id, i => i.Key);
    }

    private static InboundMessageView View(InboundMessageRow r, string inbox) =>
        new(r.Id, inbox, r.FromAddress, r.FromName, r.Subject, r.Status, r.Reason, r.Outcome, r.RecordId, r.EntityId, r.ReceivedAt, r.ProcessedAt);

    // ---- Mailboxes (environment settings) --------------------------------------------------------------------------

    public Task<IReadOnlyList<MailInboxView>> InboxesAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync<IReadOnlyList<MailInboxView>>(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireManageAsync(db, ctx, ct);
            var accessor = await AccessorAsync(db, ctx, ct);
            var rows = await db.MailInboxes.AsNoTracking().OrderBy(i => i.Key).ToListAsync(ct);
            return rows.Select(r => InboxView(r, accessor)).ToList();
        }, ct);

    public Task<IReadOnlyList<MailIntakeOption>> IntakesAsync(RequestContext ctx, CancellationToken ct) =>
        database.InTenantAsync<IReadOnlyList<MailIntakeOption>>(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireManageAsync(db, ctx, ct);
            var config = (await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct)).Config;
            return config.MailIntakes.Select(i => new MailIntakeOption(i.Id, i.Label, i.Entity, i.Enabled)).ToList();
        }, ct);

    public Task<MailInboxSaved> SaveInboxAsync(RequestContext ctx, MailInboxRequest request, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var perms = await RequireManageAsync(db, ctx, ct);
            var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
            var errors = new List<FieldError>();
            if (request.Settings?["senderVerification"]?.GetValue<string>() == "none" && !perms.Can(PlatformActions.PermissionsAdmin))
            {
                // Without verification anyone can write as any member: a permission decision.
                throw KernelException.Forbidden("Turning sender verification off needs permission administration.", "settings.senderVerification", "Name the trusted mail server (authservId) instead.");
            }

            if (!InboxKey().IsMatch(request.Key ?? string.Empty))
            {
                errors.Add(new FieldError("key", "invalid", "Use 2 to 41 lowercase letters, digits or underscores, starting with a letter."));
            }

            var label = Controls().Replace(request.Label ?? string.Empty, " ").Trim();
            if (label.Length is 0 or > 100)
            {
                errors.Add(new FieldError("label", "invalid", "Give the mailbox a name of 1 to 100 characters."));
            }

            var address = (request.Address ?? string.Empty).Trim().ToLowerInvariant();
            if (!EmailAddress().IsMatch(address) || address.Length > 254)
            {
                errors.Add(new FieldError("address", "invalid", "Enter the mailbox's email address, such as support@example.com."));
            }

            if (artifact.Config.FindMailIntake(request.Intake ?? string.Empty) is null)
            {
                errors.Add(new FieldError("intake", "not_found", $"Choose a mail intake of the published configuration: {string.Join(", ", artifact.Config.MailIntakes.Select(i => i.Id))}."));
            }

            if (request.Mode is not ("webhook" or "imap" or "disabled"))
            {
                errors.Add(new FieldError("mode", "invalid", "The mode is webhook, imap or disabled."));
            }

            var settings = request.Settings ?? [];
            ValidateSettings(settings, request.Mode, errors);
            if (errors.Count > 0)
            {
                throw new ValidationFailedException(errors);
            }

            var row = await db.MailInboxes.AsTracking().FirstOrDefaultAsync(i => i.Key == request.Key, ct);
            string? token = null;
            if (row is null)
            {
                row = new MailInboxRow { TenantId = ctx.TenantId, EnvironmentId = ctx.EnvironmentId, Id = Guid.NewGuid(), Key = request.Key!, CreatedAt = clock.UtcNow };
                db.MailInboxes.Add(row);
            }
            else if (request.Version is { } version && version != row.Version)
            {
                throw KernelException.Conflict("stale_version", "Someone changed this mailbox meanwhile.", suggestion: "Reload and apply your change again.");
            }
            else
            {
                row.Version++;
            }

            if (request.Mode == "webhook" && row.TokenHash is null)
            {
                token = NewToken();
                row.TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            }

            if (row.Mode != request.Mode || row.Settings != settings.ToJsonString())
            {
                row.ImapLastUid = null;
                row.ImapUidValidity = null;
                row.PollError = null;
            }

            row.Label = label;
            row.Address = address;
            row.IntakeId = request.Intake!;
            row.Mode = request.Mode!;
            row.Settings = settings.ToJsonString();
            row.UpdatedBy = ctx.PrincipalId;
            row.UpdatedAt = clock.UtcNow;
            AuditWriter.Add(db, ctx, "mail.inbox_save", "mail_inbox", row.Key, artifact.Revision, new { row.Mode, row.IntakeId, row.Address, verification = settings["senderVerification"]?.GetValue<string>() ?? "required" });
            await db.SaveChangesAsync(ct);
            return new MailInboxSaved(InboxView(row, await AccessorAsync(db, ctx, ct)), token);
        }, ct);

    public Task<MailInboxSaved> RotateTokenAsync(RequestContext ctx, string key, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            await RequireManageAsync(db, ctx, ct);
            var row = await db.MailInboxes.AsTracking().FirstOrDefaultAsync(i => i.Key == key, ct) ?? throw KernelException.NotFound("Mailbox");
            if (row.Mode != "webhook")
            {
                throw new KernelException("not_webhook", "Only webhook mailboxes have a token.", 400, "mode", "Switch the mailbox to webhook first.");
            }

            var token = NewToken();
            row.TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            row.Version++;
            row.UpdatedBy = ctx.PrincipalId;
            row.UpdatedAt = clock.UtcNow;
            AuditWriter.Add(db, ctx, "mail.token_rotate", "mail_inbox", row.Key, null);
            await db.SaveChangesAsync(ct);
            return new MailInboxSaved(InboxView(row, await AccessorAsync(db, ctx, ct)), token);
        }, ct);

    private void ValidateSettings(JsonObject settings, string? mode, List<FieldError> errors)
    {
        foreach (var (key, value) in settings)
        {
            if (!SettingKeys.Contains(key))
            {
                errors.Add(new FieldError($"settings.{key}", "unknown", $"Unknown setting '{key}'. Credentials are secret references (usernameRef, passwordRef), never values."));
            }
            else if (value is JsonObject or JsonArray)
            {
                errors.Add(new FieldError($"settings.{key}", "invalid", $"'{key}' takes a single value."));
            }
        }

        string? Text(string key) => settings[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (Text("senderVerification") is { } verification && verification is not ("required" or "none"))
        {
            errors.Add(new FieldError("settings.senderVerification", "invalid", "Sender verification is required or none."));
        }

        if (Text("senderVerification") != "none" && !AuthservIdPattern().IsMatch(Text("authservId") ?? string.Empty))
        {
            errors.Add(new FieldError("settings.authservId", "required", "Name the mail server whose Authentication-Results header you trust (its authserv-id, such as mx.example.com), or set senderVerification to none."));
        }

        if (settings["maxPerSenderPerHour"] is { } max && (max is not JsonValue mv || !mv.TryGetValue<int>(out var n) || n is < 1 or > 500))
        {
            errors.Add(new FieldError("settings.maxPerSenderPerHour", "invalid", "Allow 1 to 500 messages per sender and hour."));
        }

        foreach (var refKey in SecretRefSettings)
        {
            if (Text(refKey) is { } name && (!SecretName().IsMatch(name) || name.Contains("__", StringComparison.Ordinal)))
            {
                errors.Add(new FieldError($"settings.{refKey}", "invalid", "A secret reference is a name in capitals, digits and single underscores, such as IMAP_PASSWORD."));
            }
        }

        if (mode == "imap")
        {
            var host = Text("host");
            if (!options.IsAllowed("imap", host))
            {
                errors.Add(new FieldError("settings.host", "not_allowed", "This IMAP server is not on the platform's allow-list (Esmbler__Notifications__AllowedImapHosts). Ask the operator, or use a webhook mailbox."));
            }

            if (settings["port"] is { } port && (port is not JsonValue pv || !pv.TryGetValue<int>(out var p) || p is < 1 or > 65535))
            {
                errors.Add(new FieldError("settings.port", "invalid", "The port is a number from 1 to 65535 (993 for IMAPS)."));
            }

            if (Text("security") is { } security && security is not ("ssl" or "starttls"))
            {
                errors.Add(new FieldError("settings.security", "invalid", "Security is ssl or starttls."));
            }

            if (Text("usernameRef") is null || Text("passwordRef") is null)
            {
                errors.Add(new FieldError("settings.passwordRef", "required", "Name the secret references for the IMAP user name and password (usernameRef, passwordRef)."));
            }
        }
    }

    private MailInboxView InboxView(MailInboxRow r, Sdk.Notifications.ISecretAccessor accessor)
    {
        var settings = JsonNode.Parse(r.Settings) as JsonObject ?? [];
        var refs = SecretRefSettings.Select(k => settings[k]?.GetValue<string>()).OfType<string>().Distinct();
        return new MailInboxView(r.Key, r.Label, r.Address, r.IntakeId, r.Mode, settings, r.TokenHash is not null, refs.ToDictionary(n => n, n => accessor.Get(n) is not null),
            r.Mode != "imap" || options.IsAllowed("imap", settings["host"]?.GetValue<string>()), r.PolledAt, r.PollError, r.Version, r.UpdatedAt);
    }

    private async Task<EffectivePermissions> RequireManageAsync(KernelDbContext db, RequestContext ctx, CancellationToken ct)
    {
        var artifact = await configs.GetActiveAsync(db, ctx.TenantId, ctx.EnvironmentId, ct);
        var perms = await access.ForAsync(db, ctx, artifact.Config, ct, fresh: true);
        if (!perms.Can(PlatformActions.NotificationsManage) && !perms.Can(PlatformActions.PermissionsAdmin))
        {
            throw KernelException.Forbidden(ctx.AgentId is null ? "Managing mailboxes needs the notifications.manage right." : "AI agents cannot manage mailboxes.");
        }

        return perms;
    }

    private async Task<Sdk.Notifications.ISecretAccessor> AccessorAsync(KernelDbContext db, RequestContext ctx, CancellationToken ct)
    {
        var slug = await db.Tenants.Where(t => t.Id == ctx.TenantId).Select(t => t.Slug).FirstAsync(ct);
        var environment = await db.Environments.Where(e => e.Id == ctx.EnvironmentId).Select(e => e.Key).FirstAsync(ct);
        return secrets.For(slug, environment);
    }

    private static string NewToken() => TokenPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>An exact, case-insensitive ILIKE pattern (wildcards escaped).</summary>
    private static string Like(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private sealed class TokenRow
    {
        public Guid TenantId { get; set; }

        public Guid EnvironmentId { get; set; }

        public Guid InboxId { get; set; }
    }

    [GeneratedRegex(@"^[^@\s<>()\[\],;:""]{1,64}@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$")]
    private static partial Regex EmailAddress();

    [GeneratedRegex("^[a-z][a-z0-9_]{1,40}$")]
    private static partial Regex InboxKey();

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$")]
    private static partial Regex AuthservIdPattern();

    [GeneratedRegex("^[A-Z][A-Z0-9_]{1,60}$")]
    private static partial Regex SecretName();

    [GeneratedRegex(@"[\p{Cc}\p{Cf}]")]
    private static partial Regex Controls();
}

/// <summary>Processes one inbound message (ADR 0023).</summary>
public sealed class MailProcessJobHandler(MailIntakeService intake) : IJobHandler
{
    public string Kind => MailIntakeService.ProcessKind;

    public string? RequiredAction => null;

    public Task RunAsync(JobContext job, CancellationToken ct) =>
        intake.ProcessAsync(job.TenantId, job.EnvironmentId, Guid.Parse(job.Payload["message"]!.GetValue<string>()), null, ct);

    /// <summary>When retries run out, the message is marked failed so people see it instead of it waiting forever.</summary>
    public Task OnAbandonedAsync(JobContext job, string reason, CancellationToken ct) =>
        intake.FailAsync(job.TenantId, job.EnvironmentId, Guid.Parse(job.Payload["message"]!.GetValue<string>()), ct);
}
