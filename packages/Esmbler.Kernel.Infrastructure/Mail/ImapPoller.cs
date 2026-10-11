using System.Net.Sockets;
using System.Text.Json.Nodes;
using Esmbler.Kernel.Infrastructure.Notifications;
using Esmbler.Kernel.Infrastructure.Persistence;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Esmbler.Kernel.Infrastructure.Mail;

/// <summary>
/// Polls IMAP mailboxes (ADR 0023): new messages since the last seen UID are handed to <see cref="MailIntakeService"/>
/// (which deduplicates) and marked seen. The host must be on the operator's allow-list, re-checked on every poll;
/// credentials come only through secret references. Errors are recorded on the mailbox as a short code, never the
/// server's text.
/// </summary>
public sealed class ImapPoller(Database database, MailIntakeService intake, ISecretStore secrets, NotificationOptions options, ILogger<ImapPoller> logger)
{
    public const int MaxPerPoll = 50;

    private sealed class DueRow
    {
        public Guid TenantId { get; set; }

        public Guid EnvironmentId { get; set; }

        public Guid InboxId { get; set; }
    }

    private sealed record Mailbox(string Host, int Port, SecureSocketOptions Security, string Folder, string? User, string? Password, long? UidValidity, long? LastUid);

    public async Task<int> PollDueAsync(CancellationToken ct)
    {
        var due = await database.AnonymousAsync(db => db.Database.SqlQuery<DueRow>($"""SELECT tenant_id AS "TenantId", environment_id AS "EnvironmentId", inbox_id AS "InboxId" FROM kernel.mail_inboxes_due(120, 20)""").ToListAsync(ct), ct);
        foreach (var d in due)
        {
            await PollAsync(d.TenantId, d.EnvironmentId, d.InboxId, ct);
        }

        return due.Count;
    }

    public async Task PollAsync(Guid tenantId, Guid environmentId, Guid inboxId, CancellationToken ct)
    {
        var box = await database.InTenantAsync(tenantId, environmentId, null, async db =>
        {
            var row = await db.MailInboxes.AsTracking().FirstOrDefaultAsync(i => i.Id == inboxId && i.Mode == "imap", ct);
            if (row is null)
            {
                return null;
            }

            row.PolledAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            var settings = JsonNode.Parse(row.Settings) as JsonObject ?? [];
            var slug = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.Slug).FirstAsync(ct);
            var environment = await db.Environments.Where(e => e.Id == environmentId).Select(e => e.Key).FirstAsync(ct);
            var accessor = secrets.For(slug, environment);
            return new Mailbox(
                settings["host"]?.GetValue<string>() ?? string.Empty,
                settings["port"]?.GetValue<int>() ?? 993,
                settings["security"]?.GetValue<string>() == "starttls" ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect,
                settings["folder"]?.GetValue<string>() ?? "INBOX",
                settings["usernameRef"]?.GetValue<string>() is { } u ? accessor.Get(u) : null,
                settings["passwordRef"]?.GetValue<string>() is { } p ? accessor.Get(p) : null,
                row.ImapUidValidity,
                row.ImapLastUid);
        }, ct);
        if (box is null)
        {
            return;
        }

        string? error = null;
        long? validity = box.UidValidity;
        long? lastUid = box.LastUid;
        if (!options.IsAllowed("imap", box.Host))
        {
            error = "host_not_allowed";
        }
        else if (box.User is null || box.Password is null)
        {
            error = "credentials_missing";
        }
        else
        {
            try
            {
                using var client = new ImapClient { Timeout = 30_000 };
                await client.ConnectAsync(box.Host, box.Port, box.Security, ct);
                await client.AuthenticateAsync(box.User, box.Password, ct);
                var folder = string.Equals(box.Folder, "INBOX", StringComparison.OrdinalIgnoreCase) ? client.Inbox : await client.GetFolderAsync(box.Folder, ct);
                await folder.OpenAsync(FolderAccess.ReadWrite, ct);
                if (validity != folder.UidValidity)
                {
                    // A new folder generation: UIDs start over (deduplication by Message-ID keeps repeats out).
                    validity = folder.UidValidity;
                    lastUid = 0;
                }

                var uids = await folder.SearchAsync(SearchQuery.Uids(new UniqueIdRange(new UniqueId((uint)Math.Min(uint.MaxValue - 1, (lastUid ?? 0) + 1)), UniqueId.MaxValue)), ct);
                foreach (var uid in uids.Where(u => u.Id > (lastUid ?? 0)).OrderBy(u => u.Id).Take(MaxPerPoll))
                {
                    var summary = (await folder.FetchAsync([uid], MessageSummaryItems.Size, ct)).FirstOrDefault();
                    if (summary?.Size is null or <= MailParser.MaxBytes)
                    {
                        using var stream = await folder.GetStreamAsync(uid, ct);
                        using var buffer = new MemoryStream();
                        await stream.CopyToAsync(buffer, ct);
                        await intake.ReceiveAsync(tenantId, environmentId, inboxId, buffer.ToArray(), ct);
                    }

                    await folder.AddFlagsAsync(uid, MessageFlags.Seen, true, ct);
                    lastUid = uid.Id;
                }

                await client.DisconnectAsync(true, ct);
            }
            catch (AuthenticationException)
            {
                error = "authentication_failed";
            }
            catch (Exception ex) when (ex is SocketException or IOException or SslHandshakeException or ImapProtocolException or ImapCommandException or ServiceNotConnectedException or TimeoutException or FolderNotFoundException)
            {
                error = ex is FolderNotFoundException ? "folder_not_found" : "connect_failed";
            }
        }

        if (error is not null)
        {
            logger.LogWarning("Polling mailbox {Inbox} of workspace {Tenant} failed: {Code}", inboxId, tenantId, error);
        }

        await database.InTenantAsync(tenantId, environmentId, null, db =>
            db.MailInboxes.Where(i => i.Id == inboxId).ExecuteUpdateAsync(s => s
                .SetProperty(i => i.ImapUidValidity, validity)
                .SetProperty(i => i.ImapLastUid, lastUid)
                .SetProperty(i => i.PollError, error), ct), ct);
    }
}

/// <summary>Runs <see cref="ImapPoller"/> in the worker every minute.</summary>
public sealed class ImapPollingService(ImapPoller poller, ILogger<ImapPollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await poller.PollDueAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Mailbox polling failed; retrying");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }
}
