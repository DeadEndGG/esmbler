using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Esmbler.Kernel.Infrastructure.Notifications;
using MimeKit;

namespace Esmbler.Kernel.Infrastructure.Mail;

/// <summary>An attachment as reported to reviewers: name, type and size only (contents are not imported, KI-046).</summary>
public sealed record MailAttachment(string? Name, string ContentType, long Size);

/// <summary>What the intake needs from one inbound message (ADR 0023). Everything here is untrusted sender data.</summary>
public sealed record ParsedMail
{
    public string? MessageId { get; init; }

    /// <summary>In-Reply-To first, then References newest first: the IDs a reply may point at.</summary>
    public IReadOnlyList<string> ThreadIds { get; init; } = [];

    public IReadOnlyList<(string Address, string? Name)> From { get; init; } = [];

    public IReadOnlyList<string> To { get; init; } = [];

    public IReadOnlyList<string> Cc { get; init; } = [];

    public string Subject { get; init; } = "";

    /// <summary>The message text (plain text, or HTML turned into text).</summary>
    public string Text { get; init; } = "";

    /// <summary>Header values that mark automatic mail (RFC 3834 and common practice).</summary>
    public IReadOnlyList<string> AutoSignals { get; init; } = [];

    /// <summary>Authentication-Results header values, topmost first.</summary>
    public IReadOnlyList<string> AuthenticationResults { get; init; } = [];

    public IReadOnlyList<MailAttachment> Attachments { get; init; } = [];
}

/// <summary>Parses raw RFC 5322 messages with MimeKit (which stays in this project, architecture test).</summary>
public static partial class MailParser
{
    /// <summary>Largest message accepted (bytes); larger ones are refused before parsing.</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    /// <summary>Most HTML characters turned into text; the rest of a longer HTML body is dropped.</summary>
    public const int MaxHtml = 512 * 1024;

    public static ParsedMail Parse(byte[] raw)
    {
        using var stream = new MemoryStream(raw, writable: false);
        var message = MimeMessage.Load(stream);
        var threadIds = new List<string>();
        if (!string.IsNullOrWhiteSpace(message.InReplyTo))
        {
            threadIds.Add(message.InReplyTo.Trim('<', '>', ' '));
        }

        threadIds.AddRange(message.References.Reverse().Select(r => r.Trim('<', '>', ' ')));
        var html = message.HtmlBody;
        // HTML-only mail: at most MaxHtml characters are converted, with linear-time patterns (anyone can send this).
        var text = message.TextBody ?? (html is null ? string.Empty : NotificationRenderer.PlainText(StyleBlock().Replace(ScriptBlock().Replace(html.Length > MaxHtml ? html[..MaxHtml] : html, string.Empty), string.Empty)));
        return new ParsedMail
        {
            MessageId = CleanId(message.MessageId),
            ThreadIds = [.. threadIds.Select(CleanId).OfType<string>().Distinct(StringComparer.Ordinal).Take(20)],
            From = [.. message.From.Mailboxes.Select(m => (m.Address.Trim(), string.IsNullOrWhiteSpace(m.Name) ? null : m.Name.Trim()))],
            To = [.. message.To.Mailboxes.Select(m => m.Address.Trim()).Take(50)],
            Cc = [.. message.Cc.Mailboxes.Select(m => m.Address.Trim()).Take(50)],
            Subject = (message.Subject ?? string.Empty).Trim(),
            Text = text.Replace("\r\n", "\n", StringComparison.Ordinal),
            AutoSignals = AutoSignalsOf(message),
            AuthenticationResults = [.. message.Headers.Where(h => h.Id == HeaderId.AuthenticationResults).Select(h => h.Value)],
            Attachments = [.. message.Attachments.OfType<MimePart>().Take(50).Select(p => new MailAttachment(p.FileName, p.ContentType.MimeType, p.Content?.Stream?.CanSeek == true ? p.Content.Stream.Length : 0))],
        };
    }

    /// <summary>Message-ID, or a hash of the content when a message has none: the deduplication key per mailbox.</summary>
    public static string KeyOf(byte[] raw, string? messageId) =>
        messageId is { Length: > 0 and <= 250 } ? "id:" + messageId.ToLowerInvariant() : "sha256:" + Convert.ToHexStringLower(SHA256.HashData(raw));

    /// <summary>Reads only the Message-ID header (cheap, for deduplication before the full parse).</summary>
    public static string? MessageIdOf(byte[] raw)
    {
        using var stream = new MemoryStream(raw, writable: false);
        var parser = new MimeParser(stream, MimeFormat.Entity);
        var headers = parser.ParseHeaders();
        return CleanId(headers[HeaderId.MessageId]);
    }

    /// <summary>
    /// A Message-ID safe to store and reuse in References: printable ASCII without spaces or angle brackets, with an @,
    /// at most 250 characters; anything else counts as no ID.
    /// </summary>
    public static string? CleanId(string? value)
    {
        var id = value?.Trim().Trim('<', '>').Trim();
        return id is { Length: > 2 and <= 250 } && id.Contains('@', StringComparison.Ordinal) && id.All(c => c is > ' ' and < (char)127 and not ('<' or '>' or '"' or '\\' or ',')) ? id : null;
    }

    private static List<string> AutoSignalsOf(MimeMessage message)
    {
        var signals = new List<string>();
        var autoSubmitted = message.Headers["Auto-Submitted"];
        if (!string.IsNullOrWhiteSpace(autoSubmitted) && !autoSubmitted.Trim().StartsWith("no", StringComparison.OrdinalIgnoreCase))
        {
            signals.Add("auto-submitted");
        }

        if (message.Headers["Precedence"] is { } precedence && precedence.Trim().ToLowerInvariant() is "bulk" or "junk" or "list" or "auto_reply")
        {
            signals.Add("precedence");
        }

        foreach (var header in new[] { "X-Autoreply", "X-Autorespond", "List-Id", "List-Unsubscribe", "X-Loop" })
        {
            if (!string.IsNullOrWhiteSpace(message.Headers[header]))
            {
                signals.Add(header.ToLowerInvariant());
            }
        }

        if (message.Body is Multipart { ContentType: var type } && type.IsMimeType("multipart", "report"))
        {
            signals.Add("delivery-report");
        }

        if (message.Headers[HeaderId.ReturnPath] is { } returnPath && returnPath.Trim() == "<>")
        {
            signals.Add("null-return-path");
        }

        var from = message.From.Mailboxes.FirstOrDefault()?.LocalPart.ToLowerInvariant();
        if (from is "mailer-daemon" or "postmaster" or "noreply" or "no-reply" or "donotreply" or "do-not-reply")
        {
            signals.Add("system-sender");
        }

        return signals;
    }

    [GeneratedRegex("<script[^>]*>.*?</script[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.NonBacktracking)]
    private static partial Regex ScriptBlock();

    [GeneratedRegex("<style[^>]*>.*?</style[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.NonBacktracking)]
    private static partial Regex StyleBlock();
}
