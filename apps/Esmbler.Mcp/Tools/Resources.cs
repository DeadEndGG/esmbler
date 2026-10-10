using System.ComponentModel;
using System.Text.Json;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Sdk.Registry;
using ModelContextProtocol.Server;

namespace Esmbler.Mcp.Tools;

/// <summary>MCP resources: element catalog, design-system tokens, and the tenant's app graph (REQ-AI-003).</summary>
[McpServerResourceType]
public sealed class Resources(ToolRunner runner, IElementRegistry registry, PreviewService preview)
{
    private static readonly string TokenJson = JsonSerializer.Serialize(new
    {
        principles = (string[])["Use registry elements and tokens only.", "Group 2–10 related fields per card; short noun titles.", "Place new cards next to related ones with an anchor.", "Dense but calm: avoid more than 3 cards per column."],
        tones = (string[])["neutral", "info", "success", "warning", "critical"],
        accents = (string[])["accent.gray", "accent.blue", "accent.purple", "accent.green", "accent.amber", "accent.red"],
        spacing = (string[])["space.1", "space.2", "space.3", "space.4", "space.6", "space.8"],
        radius = (string[])["radius.sm", "radius.md"],
        text = (string[])["text.body", "text.body-sm", "text.title-sm", "text.title-lg", "text.muted"],
    }, ToolRunner.Json);

    [McpServerResource(UriTemplate = "esmbler://registry/elements", Name = "Element catalog", MimeType = "application/json"), Description("Every element the builder can use, with props schemas and guidance.")]
    public string Elements() => JsonSerializer.Serialize(registry.All.OrderBy(e => e.Id, StringComparer.Ordinal), ToolRunner.Json);

    [McpServerResource(UriTemplate = "esmbler://design-system/tokens", Name = "Design tokens", MimeType = "application/json"), Description("Design tokens specs may reference. Specs never contain raw colors, CSS, or pixel values.")]
    public string Tokens() => TokenJson;

    [McpServerResource(UriTemplate = "esmbler://app/graph", Name = "App graph", MimeType = "application/json"), Description("Entities, relationships, and pages of this workspace as currently drafted.")]
    public async Task<string> AppGraph()
    {
        var ctx = runner.Context();
        var (config, _, draftVersion, revision) = await preview.WorkingConfigAsync(ctx, default);
        return JsonSerializer.Serialize(new
        {
            draftVersion,
            publishedRevision = revision,
            modules = config.Modules,
            entities = config.Entities.Select(e => new { e.Id, e.Label, fields = e.Fields.Select(f => f.Key) }),
            relationships = config.Relationships.Select(r => new { r.Id, from = r.Entity, to = r.Target, r.Field }),
            views = config.Views.Select(v => new { v.Id, v.Kind, v.Entity }),
        }, ToolRunner.Json);
    }
}
