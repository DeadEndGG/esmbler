using System.Text.Json.Nodes;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Configuration;

/// <summary>
/// Overlay operations that edit a dashboard's grid (ADR 0019, REQ-WS-005), shared by the dashboard builder and the MCP
/// tools. Tiles sit in columns of rows inside the page's first section; a row's columns always span the 12 grid units,
/// so adding or removing a tile re-spans its row evenly (at most <see cref="MaxTilesPerRow"/> tiles a row).
/// </summary>
public static class DashboardLayout
{
    public const int MaxTilesPerRow = 4;

    /// <summary>The tile element for a kind: kpi (one figure), bar (chart) or table.</summary>
    public static string TileType(string kind) => kind switch
    {
        "kpi" => "display.kpi_tile",
        "bar" => "display.bar_chart",
        "table" => "display.metric_table",
        _ => throw new KernelException("invalid_kind", $"'{kind}' is not a tile kind.", 400, "kind", "Use kpi, bar or table."),
    };

    /// <summary>A new dashboard: a page with one empty section, ready for rows of tiles.</summary>
    public static ViewSpec NewDashboard(string label, string entity)
    {
        var slug = Namespaces.TenantKeyFromLabel(label)[Namespaces.TenantPrefix.Length..];
        slug = slug.Length > 30 ? slug[..30].TrimEnd('_') : slug;
        var id = $"{Namespaces.TenantPrefix}dash_{slug}_{Guid.NewGuid().ToString("N")[..6]}";
        return new ViewSpec
        {
            Id = id,
            Kind = ViewKinds.Dashboard,
            Entity = entity,
            Label = label,
            Layout = new NodeSpec
            {
                Id = id,
                Type = "layout.page",
                Props = new JsonObject { ["title"] = label },
                Children = [new NodeSpec { Id = Namespaces.NewTenantNodeId(), Type = "layout.section" }],
            },
        };
    }

    /// <summary>
    /// Puts a tile in a row: a new row at the end of the first section when <paramref name="rowId"/> is null, otherwise
    /// a new column in that row with every column of the row re-spanned evenly.
    /// </summary>
    public static IReadOnlyList<OverlayOp> AddTile(ViewSpec view, NodeSpec tile, string? rowId)
    {
        RequireDashboard(view);
        if (rowId is null)
        {
            var section = view.Layout.Children.FirstOrDefault(c => c.Type == "layout.section")
                ?? throw new KernelException("invalid_layout", $"{view.Label} has no section to add a row to.", 400, view.Id);
            var column = new NodeSpec { Id = Namespaces.NewTenantNodeId(), Type = "layout.column", Props = new JsonObject { ["span"] = 12 }, Children = [tile] };
            var row = new NodeSpec { Id = Namespaces.NewTenantNodeId(), Type = "layout.row", Children = [column] };
            return [new InsertNode { View = view.Id, Parent = section.Id, Node = row }];
        }

        var target = view.Layout.Find(rowId);
        if (target is not { Type: "layout.row" })
        {
            throw new KernelException("invalid_row", $"'{rowId}' is not a row of {view.Label}.", 400, "row", "Pick a row of this dashboard, or add the tile on a new row.");
        }

        if (target.Children.Count >= MaxTilesPerRow)
        {
            throw new KernelException("row_full", $"That row already has {target.Children.Count} tiles.", 400, "row", "Add the tile on a new row.");
        }

        var spans = Spans(target.Children.Count + 1);
        var ops = new List<OverlayOp>();
        for (var i = 0; i < target.Children.Count; i++)
        {
            ops.Add(Respan(view.Id, target.Children[i], spans[i]));
        }

        ops.Add(new InsertNode
        {
            View = view.Id,
            Parent = target.Id,
            Node = new NodeSpec { Id = Namespaces.NewTenantNodeId(), Type = "layout.column", Props = new JsonObject { ["span"] = spans[^1] }, Children = [tile] },
        });
        return ops;
    }

    /// <summary>
    /// Removes a tile: its column when the tile is alone in it (re-spanning the rest of the row), and the row when it
    /// was the row's last column.
    /// </summary>
    public static IReadOnlyList<OverlayOp> RemoveTile(ViewSpec view, string tileId)
    {
        RequireDashboard(view);
        var tile = view.Layout.Find(tileId);
        if (tile is null || !tile.Type.StartsWith("display.", StringComparison.Ordinal))
        {
            throw new KernelException("invalid_tile", $"'{tileId}' is not a tile of {view.Label}.", 400, "tile");
        }

        var column = Parent(view.Layout, tileId);
        if (column is not { Type: "layout.column" } || column.Children.Count > 1)
        {
            return [new RemoveNode { View = view.Id, Node = tileId }];
        }

        var row = Parent(view.Layout, column.Id)!;
        if (row.Children.Count == 1)
        {
            return [new RemoveNode { View = view.Id, Node = row.Id }];
        }

        var rest = row.Children.Where(c => c.Id != column.Id).ToList();
        var spans = Spans(rest.Count);
        return [new RemoveNode { View = view.Id, Node = column.Id }, .. rest.Select((c, i) => Respan(view.Id, c, spans[i]))];
    }

    /// <summary>Even spans that add up to 12: 12, 6/6, 4/4/4, 3/3/3/3.</summary>
    public static int[] Spans(int columns) => columns switch
    {
        1 => [12],
        2 => [6, 6],
        3 => [4, 4, 4],
        4 => [3, 3, 3, 3],
        _ => throw new KernelException("row_full", $"A row holds at most {MaxTilesPerRow} tiles.", 400, "row"),
    };

    private static UpdateNode Respan(string view, NodeSpec column, int span) =>
        new() { View = view, Node = column.Id, Props = new JsonObject { ["span"] = span, ["spanTablet"] = null } };

    private static void RequireDashboard(ViewSpec view)
    {
        if (view.Kind != ViewKinds.Dashboard)
        {
            throw new KernelException("invalid_view", $"{view.Label} is not a dashboard.", 400, view.Id);
        }
    }

    private static NodeSpec? Parent(NodeSpec root, string id)
    {
        foreach (var child in root.Children)
        {
            if (child.Id == id)
            {
                return root;
            }

            if (Parent(child, id) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
