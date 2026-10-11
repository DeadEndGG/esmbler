using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Esmbler.Kernel.Infrastructure.Mail;

/// <summary>
/// Sender checks and text clean-up for inbound mail (ADR 0023), all pure. Senders are verified only through an
/// Authentication-Results header added by the mail server the workspace trusts (named by its authserv-id): the
/// platform does not run SPF, DKIM or DMARC itself.
/// </summary>
public static partial class MailGuard
{
    /// <summary>Line that outbound mail puts above the quoted text; everything from it down is dropped from replies.</summary>
    public const string ReplyMarker = "##- Reply above this line -##";

    public const int MaxText = 20_000;

    /// <summary>
    /// Whether the topmost Authentication-Results header from <paramref name="authservId"/> says the From domain passed
    /// DMARC, or an aligned DKIM signature or SPF check. Headers from other servers are ignored (a sender can add any).
    /// </summary>
    public static (bool Verified, string Detail) Verify(IReadOnlyList<string> authenticationResults, string fromAddress, string? authservId)
    {
        if (string.IsNullOrWhiteSpace(authservId))
        {
            return (false, "no trusted mail server is configured");
        }

        var at = fromAddress.LastIndexOf('@');
        if (at < 0)
        {
            return (false, "no sender domain");
        }

        var domain = fromAddress[(at + 1)..].ToLowerInvariant();
        var header = authenticationResults.FirstOrDefault(h => AuthservId(h).Equals(authservId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (header is null)
        {
            return (false, "no result from the trusted mail server");
        }

        foreach (var result in header.Split(';').Skip(1).Select(r => r.Trim()))
        {
            var match = MethodResult().Match(result);
            if (!match.Success || !match.Groups["result"].Value.Equals("pass", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var method = match.Groups["method"].Value.ToLowerInvariant();
            string? Property(string name) => Regex.Match(result, $@"(?:^|\s){Regex.Escape(name)}=([^\s;]+)", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value.Trim('"').ToLowerInvariant() : null;
            switch (method)
            {
                case "dmarc" when Aligned(Property("header.from"), domain):
                    return (true, "dmarc=pass");
                case "dkim" when Aligned(Property("header.d") ?? DomainOf(Property("header.i")), domain):
                    return (true, "dkim=pass");
                case "spf" when Aligned(DomainOf(Property("smtp.mailfrom")) ?? Property("smtp.helo"), domain):
                    return (true, "spf=pass");
            }
        }

        return (false, "the sender's domain did not pass");
    }

    /// <summary>
    /// The new part of a reply: text above the reply marker or a quote header ("On ... wrote:", "-----Original Message-----",
    /// an Outlook "From:" block), without quoted lines, signature or trailing blank lines; at most 20,000 characters.
    /// </summary>
    public static string NewText(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var kept = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Contains(ReplyMarker, StringComparison.Ordinal) || OriginalMessage().IsMatch(trimmed) || Underscores().IsMatch(trimmed))
            {
                break;
            }

            // "On <date>, <name> wrote:" may wrap onto the next line.
            if (OnWrote().IsMatch(trimmed) || (trimmed.StartsWith("On ", StringComparison.Ordinal) && i + 1 < lines.Length && OnWrote().IsMatch(trimmed + " " + lines[i + 1].Trim())))
            {
                break;
            }

            if (trimmed.StartsWith("From:", StringComparison.OrdinalIgnoreCase) && kept.Count > 0 && kept[^1].Trim().Length == 0 && i + 1 < lines.Length
                && (lines[i + 1].TrimStart().StartsWith("Sent:", StringComparison.OrdinalIgnoreCase) || lines[i + 1].TrimStart().StartsWith("Date:", StringComparison.OrdinalIgnoreCase)))
            {
                break;
            }

            if (line == "-- " || trimmed == "--")
            {
                break;
            }

            if (trimmed.StartsWith('>'))
            {
                continue;
            }

            kept.Add(line.TrimEnd());
        }

        var result = string.Join('\n', kept).Trim();
        return result.Length <= MaxText ? result : result[..MaxText];
    }

    /// <summary>Plain text as safe rich text: escaped, paragraphs on blank lines, line breaks kept.</summary>
    public static string ToRichText(string text)
    {
        var html = new StringBuilder();
        foreach (var paragraph in Paragraphs().Split(text.Trim()).Where(p => p.Trim().Length > 0))
        {
            html.Append("<p>").Append(string.Join("<br>", paragraph.Trim().Split('\n').Select(l => WebUtility.HtmlEncode(l.TrimEnd())))).Append("</p>");
        }

        return html.ToString();
    }

    /// <summary>A subject without reply and forward prefixes, the record number tag and control characters.</summary>
    public static string CleanSubject(string subject)
    {
        var s = Controls().Replace(subject, " ").Trim();
        string previous;
        do
        {
            previous = s;
            s = Prefix().Replace(s, string.Empty).Trim();
        }
        while (s != previous);

        return Whitespace().Replace(NumberTag().Replace(s, string.Empty), " ").Trim();
    }

    /// <summary>Record numbers in square brackets in a subject, such as [TCK-000123].</summary>
    public static IEnumerable<string> NumbersIn(string subject) => NumberTag().Matches(subject).Select(m => m.Groups[1].Value.ToUpperInvariant()).Distinct();

    private static string AuthservId(string header)
    {
        var first = header.Split(';', 2)[0].Trim();
        var space = first.IndexOfAny([' ', '\t']);
        return space < 0 ? first : first[..space];
    }

    private static string? DomainOf(string? address) => address is null ? null : address.LastIndexOf('@') is var at and >= 0 ? address[(at + 1)..] : address;

    // Relaxed alignment (RFC 7489): the authenticated domain is the From domain or shares its parent (a.example.com
    // with example.com), but never a different registrable domain.
    private static bool Aligned(string? authenticated, string fromDomain) =>
        authenticated is { Length: > 0 } a && (a == fromDomain || a.EndsWith("." + fromDomain, StringComparison.Ordinal) || fromDomain.EndsWith("." + a, StringComparison.Ordinal)) && a.Contains('.', StringComparison.Ordinal);

    [GeneratedRegex(@"^(?<method>[a-z0-9_-]+)\s*=\s*(?<result>[a-z]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MethodResult();

    [GeneratedRegex(@"^On\s.+\swrote:$", RegexOptions.IgnoreCase)]
    private static partial Regex OnWrote();

    [GeneratedRegex(@"^-{2,}\s*(Original Message|Forwarded message)\s*-{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex OriginalMessage();

    [GeneratedRegex(@"^_{10,}$")]
    private static partial Regex Underscores();

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex Paragraphs();

    [GeneratedRegex(@"^\s*(re|fw|fwd|aw|wg|sv|vs)\s*(\[\d+\])?\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Prefix();

    [GeneratedRegex(@"\[\s*([A-Za-z][A-Za-z0-9]{0,9}-\d{1,9})\s*\]")]
    private static partial Regex NumberTag();

    [GeneratedRegex(@"[\p{Cc}\p{Cf}]")]
    private static partial Regex Controls();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Whitespace();
}
