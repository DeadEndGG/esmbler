using System.Text.Json.Nodes;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Notifications;
using Esmbler.Sdk.Specs;
using static Esmbler.UnitTests.Configuration.CompilerFixture;

namespace Esmbler.UnitTests.Configuration;

/// <summary>REQ-BIZ-003: mail intakes as validated configuration and reply text in notifications (ADR 0023).</summary>
public class MailIntakeSpecTests
{
    private const string Ticket = "core.tickets.ticket";

    private static MailIntakeSpec Intake(Action<MailIntakeSpec>? change = null)
    {
        var intake = new MailIntakeSpec
        {
            Id = "tenant__support_mail",
            Label = "Support mail",
            Entity = Ticket,
            TitleField = "title",
            BodyField = "description",
            RequesterField = "requester",
            AddressField = "contact_email",
            Set = new() { ["channel"] = JsonValue.Create("email") },
            ReopenTransition = "reopen",
            ReopenFrom = ["resolved"],
            FollowUpFrom = ["closed", "cancelled"],
        };
        change?.Invoke(intake);
        return intake;
    }

    private static IReadOnlyList<Diagnostic> Validate(MailIntakeSpec intake)
    {
        var config = Compile("1.8.0").Config;
        config.MailIntakes.Add(intake);
        return new ConfigValidator(Registry, new JsonSchemaPropsValidator()).Validate(config).Where(d => d.Severity == Severity.Error).ToList();
    }

    [Fact]
    public void A_ticket_intake_validates_and_shows_in_the_dependency_graph()
    {
        Assert.Empty(Validate(Intake()));
        var config = Compile("1.8.0").Config;
        config.MailIntakes.Add(Intake());
        var graph = DependencyGraph.Build(config);
        Assert.Contains("tenant__support_mail", graph.ImpactOf($"{Ticket}/contact_email"));
        Assert.Contains("tenant__support_mail", graph.ImpactOf("core.tickets.lifecycle"));
    }

    [Theory]
    [InlineData("entity", "config.not_found")]
    [InlineData("title_type", "config.mail_bad_field")]
    [InlineData("requester_type", "config.mail_bad_field")]
    [InlineData("missing_body", "config.mail_bad_field")]
    [InlineData("set_status", "config.mail_bad_field")]
    [InlineData("set_requester", "config.mail_bad_field")]
    [InlineData("transition", "config.not_found")]
    [InlineData("state", "config.not_found")]
    [InlineData("reopen_without_transition", "config.mail_reopen")]
    [InlineData("both_lists", "config.mail_reopen")]
    public void Broken_intakes_are_refused_with_a_reason(string broken, string code)
    {
        var intake = Intake(i =>
        {
            switch (broken)
            {
                case "entity": i.Entity = "core.tickets.tickt"; break;
                case "title_type": i.TitleField = "priority"; break;
                case "requester_type": i.RequesterField = "contact_email"; break;
                case "missing_body": i.BodyField = "body_text"; break;
                case "set_status": i.Set["status"] = JsonValue.Create("closed"); break;
                case "set_requester": i.Set["requester"] = JsonValue.Create(Guid.NewGuid().ToString()); break;
                case "transition": i.ReopenTransition = "reopen_now"; break;
                case "state": i.FollowUpFrom = ["archived"]; break;
                case "reopen_without_transition": i.ReopenTransition = null; break;
                case "both_lists": i.FollowUpFrom = ["resolved"]; break;
            }
        });
        var errors = Validate(intake);
        Assert.Contains(errors, e => e.Code == code && e.Target == "tenant__support_mail");
    }

    [Fact]
    public void Two_intakes_cannot_share_an_id()
    {
        var config = Compile("1.8.0").Config;
        config.MailIntakes.Add(Intake());
        config.MailIntakes.Add(Intake());
        Assert.Contains(new ConfigValidator(Registry, new JsonSchemaPropsValidator()).Validate(config), d => d.Code == "config.duplicate_id");
    }

    [Fact]
    public void Templates_may_show_the_reply_and_only_people_inside_the_workspace_read_it()
    {
        var template = new NotificationTemplateSpec { Id = "tenant__reply", Subject = "Re: {{record.title}}", Body = "{{activity.body}}\n\n{{link}}" };
        var record = new JsonObject { ["title"] = "VPN" };
        var entity = Compile("1.8.0").Config.FindEntity(Ticket);
        var inside = NotificationRenderer.Render(template, "email", entity, record, "TCK-000001", [], EventSensitivities.Internal, "https://x/t/1", new RenderScope(null, false, "Dana") { Reply = "Try again now." });
        Assert.StartsWith("Try again now.", inside.Body, StringComparison.Ordinal);
        var outside = NotificationRenderer.Render(template, "email", entity, record, "TCK-000001", [], EventSensitivities.Internal, "https://x/t/1", new RenderScope(null, true, null) { Reply = "Try again now." });
        Assert.DoesNotContain("Try again now.", outside.Body, StringComparison.Ordinal);

        // No reply (any event but a public reply): the placeholder is empty.
        Assert.StartsWith("\n", NotificationRenderer.Render(template, "email", entity, record, null, [], EventSensitivities.Internal, "l", new RenderScope(null, false, null)).Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<p>First</p><p>Second &amp; third</p>", "First\nSecond & third")]
    [InlineData("Line<br>next<br/>last", "Line\nnext\nlast")]
    [InlineData("<ul><li>a</li><li><strong>b</strong></li></ul>", "a\nb")]
    [InlineData("<p>&lt;script&gt;</p>", "<script>")]
    public void Rich_text_becomes_plain_text_for_email(string html, string text) =>
        Assert.Equal(text, NotificationRenderer.PlainText(html));
}
