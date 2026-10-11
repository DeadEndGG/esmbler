using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Specs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Esmbler.Kernel.Infrastructure.Records;

/// <summary>
/// CSV export of explorer results (REQ-OPS-004): the same WHERE clause and order as the list
/// (<see cref="QueryCompiler.Compile"/>), so the file holds exactly the records the explorer counts for this viewer, at
/// most <see cref="QueryCompiler.MaxCount"/>, with only the fields the viewer can see.
/// </summary>
public sealed partial class RecordService
{
    /// <summary>
    /// Checks the export and records it in the audit log in its own transaction before the first byte is written, so an
    /// aborted download is on record. People only: AI agents cannot export records.
    /// </summary>
    public Task PrepareExportAsync(RequestContext ctx, RecordQuery query, CancellationToken ct) =>
        database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            if (ctx.AgentId is not null)
            {
                throw new KernelException("agent_not_allowed", "AI agents cannot export records.", 403, null, "Export the list yourself from the explorer.");
            }

            var (artifact, entity, perms) = await PrepareAsync(db, ctx, query.Entity, null, ct);
            if (perms.Entity(entity.Id).Read == RecordScopes.None)
            {
                throw KernelException.NotFound($"Entity '{query.Entity}'");
            }

            // Compiling here refuses bad or hidden-field filters before anything is recorded.
            QueryCompiler.Compile(query, artifact.Config, entity, perms);
            AuditWriter.Add(db, ctx, "records.export", "entity", entity.Id, artifact.Revision, new { format = "csv", filters = query.Filters, sla = query.Sla.Select(s => s.ToString()), query.Search, limit = QueryCompiler.MaxCount });
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

    /// <summary>Writes the CSV: a header of field labels, then one row per record in the explorer's order.</summary>
    public async Task WriteExportAsync(RequestContext ctx, RecordQuery query, Stream output, CancellationToken ct)
    {
        var lines = await database.InTenantAsync(ctx.TenantId, ctx.EnvironmentId, ctx.PrincipalId, async db =>
        {
            var (artifact, entity, perms) = await PrepareAsync(db, ctx, query.Entity, null, ct);
            var compiled = QueryCompiler.Compile(query, artifact.Config, entity, perms);
            var conn = db.Database.GetDbConnection();
            var tx = db.Database.CurrentTransaction!.GetDbTransaction();
            await Exec(conn, tx, "SET LOCAL statement_timeout = '15s'", [], ct);
            var rows = new List<(JsonObject Data, DateTimeOffset Created, DateTimeOffset Updated)>();
            await using (var cmd = Command(conn, tx, $"SELECT data::text, created_at, updated_at FROM kernel.records r WHERE {compiled.Where} ORDER BY {compiled.OrderBy} LIMIT {QueryCompiler.MaxCount}", compiled.Parameters))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    rows.Add((JsonNode.Parse(reader.GetString(0))!.AsObject(), reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2)));
                }
            }

            var fieldAccess = perms.FieldAccessMap(entity);
            var fields = entity.Fields.Where(f => !f.Hidden && fieldAccess.GetValueOrDefault(f.Key) != FieldAccessLevels.Hidden && f.Type != FieldTypes.AutoNumber).ToList();
            var numbered = entity.Fields.Any(f => f.Type == FieldTypes.AutoNumber && fieldAccess.GetValueOrDefault(f.Key) != FieldAccessLevels.Hidden);
            var header = new List<string>();
            if (numbered)
            {
                header.Add("Number");
            }

            header.AddRange(fields.Select(f => f.Label));
            header.Add("Created");
            header.Add("Updated");
            var lines = new List<string> { CsvText.Row(header) };
            foreach (var chunk in rows.Chunk(500))
            {
                var display = await DisplayAsync(db, artifact.Config, entity, perms, chunk.Select(r => r.Data), ct);
                foreach (var (data, created, updated) in chunk)
                {
                    var visible = Filter(data, fieldAccess);
                    var labels = DisplayFor(artifact.Config, entity, data, display, fieldAccess);
                    var cells = new List<string>();
                    if (numbered)
                    {
                        cells.Add(NumberText(entity, data) ?? string.Empty);
                    }

                    cells.AddRange(fields.Select(f => labels.TryGetValue(f.Key, out var label) ? label : ExportText(artifact.Config, f, visible[f.Key])));
                    cells.Add(Kernel.Records.FieldNormalizer.FormatInstant(created));
                    cells.Add(Kernel.Records.FieldNormalizer.FormatInstant(updated));
                    lines.Add(CsvText.Row(cells));
                }
            }

            return lines;
        }, ct);

        await using var writer = new StreamWriter(output, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
        foreach (var line in lines)
        {
            await writer.WriteLineAsync(line.AsMemory(), ct);
        }
    }

    /// <summary>A cell for one field value: choice labels, Yes/No, list items joined, text as is.</summary>
    private static string ExportText(SpecCatalog config, FieldSpec field, JsonNode? value)
    {
        string Choice(string id) => field.ChoiceList is { } list && config.FindChoiceList(list)?.Find(id) is { } option ? option.Label : id;
        return value switch
        {
            null => string.Empty,
            JsonValue v when v.TryGetValue<bool>(out var b) => b ? "Yes" : "No",
            JsonValue v when v.TryGetValue<string>(out var s) => FieldTypes.UsesChoiceList(field.Type) ? Choice(s) : s,
            JsonValue v => v.ToJsonString(),
            JsonArray a => string.Join("; ", a.Select(i => i is JsonValue iv && iv.TryGetValue<string>(out var s) ? (FieldTypes.UsesChoiceList(field.Type) ? Choice(s) : s) : i?.ToJsonString() ?? string.Empty)),
            _ => value.ToJsonString(),
        };
    }
}
