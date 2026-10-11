using System.Text;
using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Events;
using Esmbler.Kernel.Infrastructure.Mail;
using Esmbler.Kernel.Infrastructure.Notifications;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Records;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.IntegrationTests.Mail;

/// <summary>
/// REQ-BIZ-003, REQ-ACC-13, REQ-EVT-007 (ADR 0023): email intake. A message opens a ticket as its sender; replies thread
/// by Message-ID or ticket number and become the sender's public replies; agents' public replies go back by email with
/// the reply text and never internal notes; duplicates, automatic mail and the platform's own mail never make records;
/// unverified, unknown and too frequent senders wait for review; mailboxes are isolated per workspace and managed only by
/// people with the right.
/// </summary>
[Collection(PlatformCollection.Name)]
public class MailIntakeTests(PlatformFixture f)
{
    private const string Ticket = "core.tickets.ticket";
    private const string Authserv = "mx.test";

    private MailIntakeService Intake => f.Get<MailIntakeService>();

    private sealed record Box(ProvisionedTenant Tenant, string Domain, Guid InboxId, string Token);

    private async Task<Box> BoxAsync(string prefix, JsonObject? settings = null)
    {
        var t = await f.NewTenantAsync(prefix, "1.9.0");
        var admin = f.As(t, "admin");
        await f.Get<NotificationService>().SaveChannelAsync(admin, new SaveChannelRequest("email", "email", "Desk email", "capture", new JsonObject { ["host"] = "smtp.example.com", ["from"] = "desk@example.com" }, null, 60), default);
        var saved = await Intake.SaveInboxAsync(admin, new MailInboxRequest("support", "Support", "support@desk.example", "core.tickets.mail.tickets", "webhook", settings ?? new JsonObject { ["authservId"] = Authserv }, null), default);
        Assert.NotNull(saved.Token);
        var resolved = await Intake.ResolveTokenAsync(saved.Token, default);
        Assert.NotNull(resolved);
        var email = await EmailOfAsync(t, "dana");
        return new Box(t, email.Split('@')[1], resolved.Value.InboxId, saved.Token!);
    }

    private Task<string> EmailOfAsync(ProvisionedTenant t, string user) =>
        f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.Principals.Where(p => p.Id == t.Users[user]).Select(p => p.Email!).FirstAsync(), default);

    private static byte[] Message(string from, string subject, string body, string? messageId = null, string? inReplyTo = null, string? authDomain = null, bool verified = true, params string[] headers)
    {
        var sb = new StringBuilder();
        if (verified)
        {
            sb.Append($"Authentication-Results: {Authserv}; dkim=pass header.d={authDomain ?? from.Split('@')[1]}; dmarc=pass header.from={authDomain ?? from.Split('@')[1]}\r\n");
        }

        sb.Append($"From: Someone <{from}>\r\nTo: support@desk.example\r\nSubject: {subject}\r\nMessage-ID: <{messageId ?? Guid.NewGuid().ToString("N") + "@mail.test"}>\r\nDate: Sat, 10 Oct 2026 10:00:00 +0000\r\n");
        if (inReplyTo is not null)
        {
            sb.Append($"In-Reply-To: <{inReplyTo}>\r\nReferences: <{inReplyTo}>\r\n");
        }

        foreach (var h in headers)
        {
            sb.Append(h).Append("\r\n");
        }

        sb.Append("MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n").Append(body.Replace("\n", "\r\n", StringComparison.Ordinal)).Append("\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private async Task PumpAsync()
    {
        var dispatcher = f.Get<OutboxDispatcher>();
        var runner = f.Get<JobRunner>();
        while (await dispatcher.DispatchOnceAsync(default) > 0 | await runner.RunOnceAsync(default) > 0)
        {
        }
    }

    private Task<InboundMessageRow> MessageRowAsync(Box box, Guid id) =>
        f.Get<Database>().InTenantAsync(box.Tenant.TenantId, box.Tenant.Environments["prod"], null, db => db.InboundMessages.AsNoTracking().FirstAsync(m => m.Id == id), default);

    private async Task<(Guid Id, InboundMessageRow Row)> SendAsync(Box box, byte[] raw)
    {
        var receipt = await Intake.ReceiveAsync(box.Tenant.TenantId, box.Tenant.Environments["prod"], box.InboxId, raw, default);
        await PumpAsync();
        return (receipt.Id, await MessageRowAsync(box, receipt.Id));
    }

    private Task<List<NotificationDeliveryRow>> EmailsAsync(Box box, Guid? record = null) =>
        f.Get<Database>().InTenantAsync(box.Tenant.TenantId, box.Tenant.Environments["prod"], null, db => db.NotificationDeliveries.AsNoTracking()
            .Where(d => d.ChannelType == "email" && (record == null || d.RecordId == record)).OrderBy(d => d.CreatedAt).ToListAsync(), default);

    [Fact]
    public async Task A_message_opens_a_ticket_for_its_sender_and_replies_go_both_ways_without_internal_notes()
    {
        var box = await BoxAsync("mail");
        var t = box.Tenant;
        var dana = await EmailOfAsync(t, "dana");
        var (_, row) = await SendAsync(box, Message(dana, "VPN drops every hour", "Hello,\n\nThe VPN drops every hour since Monday.\n\nThanks,\nDana", messageId: "first@mail.test"));
        Assert.True(row.Status == "processed", $"{row.Status} {row.Reason} {row.Details}");
        Assert.Equal("created", row.Outcome);

        var records = f.Get<RecordService>();
        var agent = f.As(t, "agent");
        var ticket = await records.GetAsync(agent, Ticket, row.RecordId!.Value, default);
        Assert.Equal("VPN drops every hour", ticket.Data["title"]!.GetValue<string>());
        Assert.Equal(t.Users["dana"].ToString(), ticket.Data["requester"]!.GetValue<string>());
        Assert.Equal("email", ticket.Data["channel"]!.GetValue<string>());
        Assert.Equal(dana, ticket.Data["contact_email"]!.GetValue<string>());
        Assert.Contains("since Monday", ticket.Data["description"]!.GetValue<string>(), StringComparison.Ordinal);

        // The requester hears that the ticket was opened, threaded on their own message.
        var number = ticket.Data["number"]!.GetValue<string>();
        var ack = Assert.Single(await EmailsAsync(box, ticket.Id));
        Assert.Equal($"[{number}] Received: VPN drops every hour", ack.Subject);
        Assert.NotNull(ack.MessageId);

        // An internal note and a public reply: only the reply reaches the requester, with its text.
        var sanitizer = f.Get<IRichTextSanitizer>();
        await records.AddActivityAsync(agent, Ticket, ticket.Id, "work_note", "<p>Root cause: expired certificate on vpn-gw-2</p>", sanitizer, default);
        await records.AddActivityAsync(agent, Ticket, ticket.Id, "public_reply", "<p>Please reconnect now &amp; tell us if it drops again.</p>", sanitizer, default);
        await PumpAsync();
        var emails = await EmailsAsync(box, ticket.Id);
        Assert.DoesNotContain(emails, e => e.Body.Contains("expired certificate", StringComparison.Ordinal) || e.Subject.Contains("expired certificate", StringComparison.Ordinal));
        var reply = Assert.Single(emails, e => e.Subject == $"[{number}] Re: VPN drops every hour");
        Assert.Contains("Please reconnect now & tell us if it drops again.", reply.Body, StringComparison.Ordinal);
        Assert.StartsWith(MailGuard.ReplyMarker, reply.Body, StringComparison.Ordinal);

        // Dana answers that email: only the new text joins the conversation, as her public reply, and she is not
        // emailed her own reply.
        var answer = $"It still drops.\n\nOn Sat, 10 Oct 2026 at 11:00, Desk <desk@example.com> wrote:\n> {MailGuard.ReplyMarker}\n> Please reconnect now";
        var (_, second) = await SendAsync(box, Message(dana, $"Re: [{number}] Re: VPN drops every hour", answer, inReplyTo: reply.MessageId));
        Assert.Equal(("processed", "replied", ticket.Id), (second.Status, second.Outcome, second.RecordId));
        var conversation = await records.ActivityAsync(f.As(t, "dana"), Ticket, ticket.Id, default);
        Assert.DoesNotContain(conversation, a => a.Kind == "work_note");
        var fromEmail = Assert.Single(conversation, a => a.Source == "email");
        Assert.Equal((t.Users["dana"], "public_reply"), (fromEmail.Author!.Value, fromEmail.Kind));
        Assert.Contains("It still drops.", fromEmail.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Please reconnect", fromEmail.Body, StringComparison.Ordinal);
        Assert.Equal(2, (await EmailsAsync(box, ticket.Id)).Count);
    }

    [Fact]
    public async Task Replies_reopen_resolved_tickets_closed_ones_start_a_follow_up_and_nobody_replies_into_someone_elses_ticket()
    {
        var box = await BoxAsync("mailr");
        var t = box.Tenant;
        var records = f.Get<RecordService>();
        var agent = f.As(t, "agent");
        var (_, first) = await SendAsync(box, Message(await EmailOfAsync(t, "dana"), "Printer jam", "Tray 2 jams."));
        var ticket = await records.GetAsync(agent, Ticket, first.RecordId!.Value, default);
        var number = ticket.Data["number"]!.GetValue<string>();

        // Jordan cannot add to Dana's ticket by naming its number.
        var (_, intruder) = await SendAsync(box, Message(await EmailOfAsync(t, "jordan"), $"Re: [{number}] Printer jam", "Please close this."));
        Assert.Equal(("suspended", MailReasons.NotAllowed), (intruder.Status, intruder.Reason));
        Assert.DoesNotContain(await records.ActivityAsync(agent, Ticket, ticket.Id, default), a => a.Body.Contains("close this", StringComparison.Ordinal));

        // A reply to the resolved ticket reopens it.
        var started = await records.TransitionAsync(agent, Ticket, ticket.Id, ticket.Revision, "start", [], null, null, default);
        await records.TransitionAsync(agent, Ticket, ticket.Id, started.Revision, "resolve", new JsonObject { ["resolution_notes"] = "Cleared" }, null, null, default);
        var (_, reopen) = await SendAsync(box, Message(await EmailOfAsync(t, "dana"), $"Re: [{number}] Printer jam", "It jammed again."));
        Assert.Equal(("processed", "reopened"), (reopen.Status, reopen.Outcome));
        Assert.Equal("in_progress", (await records.GetAsync(agent, Ticket, ticket.Id, default)).Data["status"]!.GetValue<string>());

        // Once closed, a reply starts a new ticket.
        var current = await records.GetAsync(agent, Ticket, ticket.Id, default);
        var resolved = await records.TransitionAsync(agent, Ticket, ticket.Id, current.Revision, "resolve", new JsonObject { ["resolution_notes"] = "Replaced roller" }, null, null, default);
        await records.TransitionAsync(f.As(t, "dana"), Ticket, ticket.Id, resolved.Revision, "confirm", [], null, null, default);
        var (_, followUp) = await SendAsync(box, Message(await EmailOfAsync(t, "dana"), $"Re: [{number}] Printer jam", "Now tray 1 jams."));
        Assert.Equal(("processed", "follow_up"), (followUp.Status, followUp.Outcome));
        Assert.NotEqual(ticket.Id, followUp.RecordId);
    }

    [Fact]
    public async Task Duplicates_automatic_mail_and_the_platforms_own_mail_never_make_tickets()
    {
        var box = await BoxAsync("maild");
        var t = box.Tenant;
        var dana = await EmailOfAsync(t, "dana");
        var raw = Message(dana, "Monitor flickers", "The left monitor flickers.", messageId: "dup@mail.test");
        var first = await SendAsync(box, raw);
        var again = await Intake.ReceiveAsync(t.TenantId, t.Environments["prod"], box.InboxId, raw, default);
        Assert.True(again.Duplicate);
        Assert.Equal(first.Id, again.Id);

        var (_, auto) = await SendAsync(box, Message(dana, "Out of office", "I am away until Monday.", headers: "Auto-Submitted: auto-replied"));
        Assert.Equal(("rejected", MailReasons.AutoReply), (auto.Status, auto.Reason));
        var (_, bounce) = await SendAsync(box, Message("mailer-daemon@" + box.Domain, "Undeliverable", "Delivery failed."));
        Assert.Equal(("rejected", MailReasons.AutoReply), (bounce.Status, bounce.Reason));
        var (_, own) = await SendAsync(box, Message("support@desk.example", "Loop", "From ourselves.", authDomain: "desk.example"));
        Assert.Equal(("rejected", MailReasons.OwnMessage), (own.Status, own.Reason));

        // Our own notification coming back (same Message-ID) is never processed either.
        var ack = (await EmailsAsync(box, first.Row.RecordId)).First(e => e.MessageId is not null);
        var (_, echo) = await SendAsync(box, Message(dana, "Re: echo", "echo", messageId: ack.MessageId));
        Assert.Equal(("rejected", MailReasons.OwnMessage), (echo.Status, echo.Reason));

        var tickets = await f.Get<RecordService>().QueryAsync(f.As(t, "agent"), new RecordQuery { Entity = Ticket, PageSize = 100 }, default);
        Assert.Single(tickets.Items, r => r.Data["channel"]?.GetValue<string>() == "email");

        // Retention: processed mail loses its raw content after a day and goes after 30 days.
        await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db =>
            db.InboundMessages.ExecuteUpdateAsync(s => s.SetProperty(m => m.ReceivedAt, DateTimeOffset.UtcNow.AddDays(-40)).SetProperty(m => m.ProcessedAt, DateTimeOffset.UtcNow.AddDays(-40))), default);
        var purged = await f.Get<AuditMaintenance>().PurgeAsync(default);
        Assert.Contains(purged, p => p.TenantId == t.TenantId && p.Kind == "inbound_mail" && p.Removed == 5);
    }

    [Fact]
    public async Task Unverified_unknown_and_too_frequent_senders_wait_for_an_agent_to_accept_or_reject()
    {
        var box = await BoxAsync("mails", new JsonObject { ["authservId"] = Authserv, ["maxPerSenderPerHour"] = 2 });
        var t = box.Tenant;
        var dana = await EmailOfAsync(t, "dana");
        var (unverifiedId, unverified) = await SendAsync(box, Message(dana, "Reset my password", "Please reset it.", verified: false));
        Assert.Equal(("suspended", MailReasons.UnverifiedSender), (unverified.Status, unverified.Reason));
        var (_, spoofed) = await SendAsync(box, Message(dana, "Wire money", "Now.", authDomain: "evil.example"));
        Assert.Equal(("suspended", MailReasons.UnverifiedSender), (spoofed.Status, spoofed.Reason));
        var (_, tooMany) = await SendAsync(box, Message(dana, "Third", "Third message within the hour."));
        Assert.Equal(("suspended", MailReasons.TooManyFromSender), (tooMany.Status, tooMany.Reason));
        var (strangerId, stranger) = await SendAsync(box, Message("pat@partner.example", "Invoice question", "Where is invoice 42?"));
        Assert.Equal(("suspended", MailReasons.UnknownSender), (stranger.Status, stranger.Reason));

        // Requesters and AI agents do not review; agents of the ticket entity do.
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => Intake.ListAsync(f.As(t, "dana"), null, default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => Intake.ListAsync(f.As(t, "agent", agent: Guid.NewGuid()), null, default))).Status);
        var agent = f.As(t, "agent");
        var queue = await Intake.ListAsync(agent, null, default);
        Assert.Equal(4, queue.Count);
        var detail = await Intake.GetAsync(agent, strangerId, default);
        Assert.Equal("Where is invoice 42?", detail.Text);

        // An unknown sender is accepted on behalf of a member; the stranger stays the contact.
        await Assert.ThrowsAsync<KernelException>(() => Intake.AcceptAsync(agent, strangerId, Guid.NewGuid(), default));
        var accepted = await Intake.AcceptAsync(agent, strangerId, t.Users["jordan"], default);
        Assert.Equal(("processed", "created"), (accepted.Status, accepted.Outcome));
        var created = await f.Get<RecordService>().GetAsync(agent, Ticket, accepted.RecordId!.Value, default);
        Assert.Equal((t.Users["jordan"].ToString(), "pat@partner.example"), (created.Data["requester"]!.GetValue<string>(), created.Data["contact_email"]!.GetValue<string>()));

        // The unverified message is accepted as Dana's; a decided message cannot be decided again.
        var verifiedLater = await Intake.AcceptAsync(agent, unverifiedId, null, default);
        Assert.Equal("processed", verifiedLater.Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<KernelException>(() => Intake.RejectAsync(agent, unverifiedId, default))).Status);
        var rejected = await Intake.RejectAsync(agent, (await Intake.ListAsync(agent, null, default))[0].Id, default);
        Assert.Equal(("rejected", MailReasons.Rejected), (rejected.Status, rejected.Reason));
        var audit = await f.Get<Database>().InTenantAsync(t.TenantId, t.Environments["prod"], null, db => db.AuditLog.AsNoTracking().Where(a => a.Operation.StartsWith("mail.")).Select(a => a.Operation).ToListAsync(), default);
        Assert.Contains("mail.accept", audit);
        Assert.Contains("mail.reject", audit);
        Assert.Contains("mail.suspended", audit);
    }

    [Fact]
    public async Task Mailboxes_belong_to_one_workspace_and_only_people_with_the_right_configure_them()
    {
        var box = await BoxAsync("mailx");
        var other = await BoxAsync("maily");
        var t = box.Tenant;
        var admin = f.As(t, "admin");

        // A token names exactly its own mailbox; rotation retires the old one.
        Assert.Equal(t.TenantId, (await Intake.ResolveTokenAsync(box.Token, default))!.Value.TenantId);
        Assert.Null(await Intake.ResolveTokenAsync("esm_mail_not-a-real-token-at-all", default));
        Assert.Null(await Intake.ResolveTokenAsync(box.Token[..^2], default));
        var rotated = await Intake.RotateTokenAsync(admin, "support", default);
        Assert.Null(await Intake.ResolveTokenAsync(box.Token, default));
        Assert.Equal(box.InboxId, (await Intake.ResolveTokenAsync(rotated.Token, default))!.Value.InboxId);

        // Another workspace's mailbox is not reachable from here, and its messages are not visible.
        await Assert.ThrowsAsync<KernelException>(() => Intake.ReceiveAsync(t.TenantId, t.Environments["prod"], other.InboxId, Message("x@y.example", "x", "y"), default));
        await SendAsync(other, Message(await EmailOfAsync(other.Tenant, "dana"), "Other", "Other workspace", verified: false));
        Assert.Empty(await Intake.ListAsync(f.As(t, "agent"), "all", default));
        Assert.Empty(await Intake.ListAsync(f.As(t, "agent", env: "sandbox"), "all", default));

        // Only notifications.manage or permission administrators configure mailboxes; credentials are references.
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => Intake.InboxesAsync(f.As(t, "builder"), default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<KernelException>(() => Intake.SaveInboxAsync(f.As(t, "admin", agent: Guid.NewGuid()), new MailInboxRequest("x", "X", "x@desk.example", "core.tickets.mail.tickets", "webhook", null, null), default))).Status);
        var secret = await Assert.ThrowsAsync<ValidationFailedException>(() => Intake.SaveInboxAsync(admin, new MailInboxRequest("imap", "IMAP", "it@desk.example", "core.tickets.mail.tickets", "imap", new JsonObject { ["host"] = "10.0.0.5", ["password"] = "hunter2", ["authservId"] = Authserv }, null), default));
        Assert.Contains(secret.Errors, e => e.Field == "settings.password");
        Assert.Contains(secret.Errors, e => e.Field == "settings.host" && e.Code == "not_allowed");
        var noAuthserv = await Assert.ThrowsAsync<ValidationFailedException>(() => Intake.SaveInboxAsync(admin, new MailInboxRequest("loose", "Loose", "l@desk.example", "core.tickets.mail.tickets", "webhook", [], null), default));
        Assert.Contains(noAuthserv.Errors, e => e.Field == "settings.authservId");
        var unknownIntake = await Assert.ThrowsAsync<ValidationFailedException>(() => Intake.SaveInboxAsync(admin, new MailInboxRequest("bad", "Bad", "b@desk.example", "core.tickets.mail.nope", "webhook", new JsonObject { ["senderVerification"] = "none" }, null), default));
        Assert.Contains(unknownIntake.Errors, e => e.Field == "intake");

        var listed = Assert.Single(await Intake.InboxesAsync(admin, default));
        Assert.True(listed.HasToken);
        Assert.DoesNotContain(rotated.Token!, System.Text.Json.JsonSerializer.Serialize(listed), StringComparison.Ordinal);
    }
}
