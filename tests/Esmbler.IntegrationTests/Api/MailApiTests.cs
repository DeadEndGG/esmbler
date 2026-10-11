using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel.Infrastructure.Mail;

namespace Esmbler.IntegrationTests.Api;

/// <summary>REQ-BIZ-003 (ADR 0023): the inbound email webhook and mailbox settings over HTTP.</summary>
[Collection(PlatformCollection.Name)]
public class MailApiTests(PlatformFixture platform) : IClassFixture<ApiTestsFactoryHolder>
{
    private ApiFactory Factory => ApiTestsFactoryHolder.Get(platform);

    private static ByteArrayContent Raw(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("message/rfc822");
        return content;
    }

    [Fact]
    public async Task The_webhook_takes_a_message_once_with_the_mailbox_token_and_nothing_without_it()
    {
        var admin = await Factory.SignInAsync("morgan@globex.example", "globex");
        var saved = await admin.PutAsJsonAsync("/api/admin/mail/inboxes/support_api", new { label = "Support (API)", address = "support-api@globex.example", intake = "core.tickets.mail.tickets", mode = "webhook", settings = new { senderVerification = "none" } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var token = (await saved.Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json))!["token"]!.GetValue<string>();
        Assert.StartsWith("esm_mail_", token, StringComparison.Ordinal);

        // The token is shown once: listing the mailboxes never returns it.
        var listed = await admin.GetStringAsync("/api/admin/mail/inboxes");
        Assert.DoesNotContain(token, listed, StringComparison.Ordinal);

        var raw = Encoding.UTF8.GetBytes($"From: taylor@globex.example\r\nTo: support-api@globex.example\r\nSubject: Webhook test\r\nMessage-ID: <{Guid.NewGuid():N}@mail.test>\r\n\r\nHello\r\n");
        using var anonymous = Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/mail/inbound", Raw(raw))).StatusCode);
        using (var wrong = new HttpRequestMessage(HttpMethod.Post, "/api/mail/inbound") { Content = Raw(raw) })
        {
            wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token[..^3] + "abc");
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(wrong)).StatusCode);
        }

        async Task<HttpResponseMessage> PostAsync(byte[] body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/mail/inbound") { Content = Raw(body) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await anonymous.SendAsync(request);
        }

        var first = await PostAsync(raw);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.False((await first.Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json))!["duplicate"]!.GetValue<bool>());
        var again = await PostAsync(raw);
        Assert.True((await again.Content.ReadFromJsonAsync<JsonObject>(ApiFactory.Json))!["duplicate"]!.GetValue<bool>());

        var tooLarge = new byte[MailParser.MaxBytes + 10];
        Encoding.ASCII.GetBytes("From: a@b.example\r\n\r\n").CopyTo(tooLarge, 0);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await PostAsync(tooLarge)).StatusCode);

        // Requesters cannot read the review queue or the mailbox settings.
        var requester = await Factory.SignInAsync("taylor@globex.example", "globex");
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync("/api/mail/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync("/api/admin/mail/inboxes")).StatusCode);
    }
}
