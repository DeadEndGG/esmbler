using Esmbler.IntegrationTests.Infrastructure;
using Esmbler.Kernel;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;

namespace Esmbler.IntegrationTests.Configuration;

/// <summary>REQ-CFG-011: customizations are visible, and an element can be reset to default through the draft.</summary>
[Collection(PlatformCollection.Name)]
public class ResetToDefaultTests(PlatformFixture f)
{
    private const string Ticket = "core.tickets.ticket";

    [Fact]
    public async Task Customizations_are_listed_and_a_reset_restores_the_default_after_publish()
    {
        var t = await f.NewTenantAsync("rd");
        var admin = f.As(t, "admin");
        var drafts = f.Get<DraftService>();
        var publisher = f.Get<PublishService>();
        var draft = await drafts.OpenAsync(admin, default);
        await drafts.ApplyAsync(admin, new UpdateField { Entity = Ticket, Field = "category", Set = new FieldPatch { Label = "Request type" } }, draft.Version, "ui", default);
        var state = await drafts.CurrentAsync(admin, default);
        await publisher.PublishAsync(admin, state!.ArtifactHash!, state.PublishedRevision, default);

        var preview = f.Get<PreviewService>();
        var listed = await preview.CustomizationsAsync(admin, default);
        var category = Assert.Single(listed, c => c.Element == $"{Ticket}/category");
        Assert.Equal("changed", category.Kind);
        Assert.Contains("Request type", category.Label, StringComparison.Ordinal);

        var reopened = await drafts.OpenAsync(admin, default);
        await drafts.ApplyAsync(admin, new ResetElement { Target = $"{Ticket}/category" }, reopened.Version, "ui", default);
        state = await drafts.CurrentAsync(admin, default);
        Assert.Contains(state!.Changes, c => c.Id == $"{Ticket}/category");
        await publisher.PublishAsync(admin, state.ArtifactHash!, state.PublishedRevision, default);

        var entity = await f.Get<ExperienceService>().EntityAsync(admin, Ticket, default);
        Assert.Equal("Category", entity.Entity.FindField("category")!.Label);
        Assert.DoesNotContain(await preview.CustomizationsAsync(admin, default), c => c.Element == $"{Ticket}/category");
    }

    [Fact]
    public async Task Only_builders_see_the_customization_list()
    {
        var t = await f.NewTenantAsync("rq");
        var ex = await Assert.ThrowsAsync<KernelException>(() => f.Get<PreviewService>().CustomizationsAsync(f.As(t, "dana"), default));
        Assert.Equal(403, ex.Status);
    }
}
