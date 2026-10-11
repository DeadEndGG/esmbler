using System.Text;
using Esmbler.Kernel.Infrastructure.Mail;

namespace Esmbler.UnitTests.Mail;

/// <summary>REQ-BIZ-003 (ADR 0023): sender verification, reply text, subjects and automatic-mail signals.</summary>
public class MailGuardTests
{
    [Theory]
    [InlineData("mx.test; dmarc=pass header.from=example.com", "dana@example.com", true)]
    [InlineData("mx.test; dkim=pass header.d=mail.example.com", "dana@example.com", true)]
    [InlineData("mx.test; spf=pass smtp.mailfrom=bounce@example.com", "dana@sub.example.com", true)]
    [InlineData("mx.test; dkim=pass header.d=evil.example", "dana@example.com", false)]
    [InlineData("mx.test; dmarc=fail header.from=example.com", "dana@example.com", false)]
    [InlineData("mx.test; dkim=pass header.d=com", "dana@example.com", false)]
    [InlineData("attacker.example; dmarc=pass header.from=example.com", "dana@example.com", false)]
    public void Only_the_trusted_servers_aligned_pass_verifies_a_sender(string header, string from, bool verified) =>
        Assert.Equal(verified, MailGuard.Verify([header], from, "mx.test").Verified);

    [Fact]
    public void The_topmost_result_of_the_trusted_server_counts_and_nothing_verifies_without_one()
    {
        // A sender can add their own "pass" further down; the receiving server's header comes first.
        Assert.False(MailGuard.Verify(["mx.test; dmarc=fail header.from=example.com", "mx.test; dmarc=pass header.from=example.com"], "a@example.com", "mx.test").Verified);
        Assert.False(MailGuard.Verify(["mx.test; dmarc=pass header.from=example.com"], "a@example.com", null).Verified);
        Assert.False(MailGuard.Verify([], "a@example.com", "mx.test").Verified);
    }

    [Theory]
    [InlineData("It still drops.\n\nOn Sat, 10 Oct 2026 at 11:00, Desk <desk@example.com> wrote:\n> old", "It still drops.")]
    [InlineData("Thanks!\nOn Sat, 10 Oct 2026 at 11:00, Desk\n<desk@example.com> wrote:\n> old", "Thanks!")]
    [InlineData("New text\n##- Reply above this line -##\nold", "New text")]
    [InlineData("Answer\n\n-----Original Message-----\nFrom: x", "Answer")]
    [InlineData("Answer\n\nFrom: Desk\nSent: Monday\nold", "Answer")]
    [InlineData("Line one\n> quoted\nLine two\n-- \nSignature", "Line one\nLine two")]
    public void Only_the_new_part_of_a_reply_is_kept(string text, string expected) =>
        Assert.Equal(expected, MailGuard.NewText(text));

    [Fact]
    public void Plain_text_becomes_escaped_rich_text() =>
        Assert.Equal("<p>Hi &lt;b&gt;team&lt;/b&gt;<br>line</p><p>next</p>", MailGuard.ToRichText("Hi <b>team</b>\nline\n\nnext"));

    [Theory]
    [InlineData("Re: RE: Fwd: [TCK-000123] Printer jam", "Printer jam")]
    [InlineData("AW: Drucker‮", "Drucker")]
    public void Subjects_lose_prefixes_numbers_and_control_characters(string subject, string clean) =>
        Assert.Equal(clean, MailGuard.CleanSubject(subject));

    [Fact]
    public void Record_numbers_are_found_in_square_brackets() =>
        Assert.Equal(["TCK-000123", "REQ-000007"], MailGuard.NumbersIn("Re: [tck-000123] and [REQ-000007] not TCK-000999"));

    [Theory]
    [InlineData("Auto-Submitted: auto-replied", "auto-submitted")]
    [InlineData("Precedence: bulk", "precedence")]
    [InlineData("List-Id: <team.lists.example>", "list-id")]
    [InlineData("X-Autoreply: yes", "x-autoreply")]
    public void Automatic_mail_is_recognized(string header, string signal)
    {
        var raw = Encoding.UTF8.GetBytes($"From: a@example.com\r\nSubject: s\r\n{header}\r\n\r\nbody\r\n");
        Assert.Contains(signal, MailParser.Parse(raw).AutoSignals);
        Assert.Empty(MailParser.Parse(Encoding.UTF8.GetBytes("From: a@example.com\r\nAuto-Submitted: no\r\nSubject: s\r\n\r\nbody\r\n")).AutoSignals);
    }

    [Fact]
    public void Html_only_mail_is_read_as_text_without_scripts()
    {
        var raw = Encoding.UTF8.GetBytes("From: a@example.com\r\nSubject: s\r\nMessage-ID: <x@y>\r\nMIME-Version: 1.0\r\nContent-Type: text/html\r\n\r\n<p>Hello</p><script>alert(1)</script><p>World &amp; more</p>\r\n");
        var mail = MailParser.Parse(raw);
        Assert.Equal("Hello\nWorld & more", mail.Text);
        Assert.Equal("x@y", mail.MessageId);
        Assert.Equal("id:x@y", MailParser.KeyOf(raw, MailParser.MessageIdOf(raw)));
        Assert.StartsWith("sha256:", MailParser.KeyOf(raw, null), StringComparison.Ordinal);
    }
}
