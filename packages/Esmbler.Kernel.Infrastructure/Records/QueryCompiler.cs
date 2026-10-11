using System.Text;
using Esmbler.Expressions;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Configuration;
using Esmbler.Sdk.Specs;
using Npgsql;
using NpgsqlTypes;

namespace Esmbler.Kernel.Infrastructure.Records;

public sealed record SortSpec(string Field, bool Descending);

/// <summary>Typed record query (REQ-REC-002). Filters are CEL-subset expressions over record and user.</summary>
public sealed class RecordQuery
{
    public required string Entity { get; init; }

    public IReadOnlyList<string> Filters { get; init; } = [];

    public string? Search { get; init; }

    public SortSpec? Sort { get; init; }

    public int Offset { get; init; }

    public int PageSize { get; init; } = 25;

    /// <summary>Only records with an SLA timer in a state: breached or at risk (drill-down from SLA metrics, ADR 0019).</summary>
    public IReadOnlyList<SlaFilter> Sla { get; init; } = [];

    /// <summary>Group rows by this field: the group key is sorted first, in choice order (explorer grouping).</summary>
    public string? GroupBy { get; init; }
}

/// <summary>
/// An SLA condition in a query: records whose timer of <see cref="Policy"/> (and <see cref="Target"/>) recorded a breach
/// (<c>breached</c>), or is open with a recorded warning and no breach (<c>at_risk</c>).
/// </summary>
public sealed record SlaFilter(string Policy, string? Target, string State = "breached")
{
    /// <summary>Parses <c>policy[/target]:state</c>, the form used in explorer URLs.</summary>
    public static SlaFilter Parse(string value)
    {
        var colon = value.LastIndexOf(':');
        var state = colon > 0 ? value[(colon + 1)..] : "breached";
        var target = colon > 0 ? value[..colon] : value;
        var slash = target.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? new SlaFilter(target[..slash], target[(slash + 1)..], state) : new SlaFilter(target, null, state);
    }

    public override string ToString() => $"{Policy}{(Target is null ? string.Empty : "/" + Target)}:{State}";
}

public sealed record CompiledQuery(string Where, IReadOnlyList<NpgsqlParameter> Parameters, string OrderBy);

/// <summary>
/// Compiles a record query to parameterized SQL. The permission predicate is ANDed into WHERE so authorization
/// applies before ORDER BY, LIMIT, and COUNT (REQ-REC-003, REQ-AUTHZ-005). Boolean sub-expressions are wrapped in
/// COALESCE(..., false) and equality uses IS [NOT] DISTINCT FROM, which reproduces the expression language's null
/// semantics exactly, including under negation. Values are always parameters; field keys are validated against the
/// entity before being embedded as JSON keys.
/// </summary>
public sealed class QueryCompiler
{
    public const int MaxPageSize = 100;
    public const int MaxOffset = 10_000;

    /// <summary>Totals and metric counts stop counting here ("10,000+"), the same for the explorer and dashboards (bounded work per request).</summary>
    public const int MaxCount = 10_000;
    public const int MaxFilterNodes = 64;
    private static readonly ExpressionLimits Limits = new(MaxLength: 1000, MaxDepth: 16, MaxNodes: MaxFilterNodes);

    public static CompiledQuery Compile(RecordQuery query, SpecCatalog config, EntitySpec entity, EffectivePermissions permissions)
    {
        var ctx = new Ctx(config, entity, permissions);
        var where = Where(query, config, entity, permissions, ctx);
        var order = OrderBy(query.Sort, entity, permissions, ctx);
        if (query.GroupBy is { } group && GroupKey(entity, permissions, ctx, group) is { } key)
        {
            order = $"{key.Order} NULLS LAST, {order}";
        }

        return new CompiledQuery(where, ctx.Parameters, order);
    }

    /// <summary>
    /// The WHERE clause of a query: entity, not deleted, the viewer's permission predicate, the filters, SLA conditions
    /// and search. Lists and metrics both use it, so a figure always counts exactly the records the explorer lists.
    /// </summary>
    public static string Where(RecordQuery query, SpecCatalog config, EntitySpec entity, EffectivePermissions permissions, Ctx ctx)
    {
        var where = new List<string> { "r.entity_id = " + ctx.Param(entity.Id), "r.deleted_at IS NULL", "(" + PermissionPredicate(entity, permissions, ctx) + ")" };
        foreach (var filter in query.Filters.Where(f => !string.IsNullOrWhiteSpace(f)))
        {
            where.Add(ctx.Filter(filter));
        }

        foreach (var sla in query.Sla)
        {
            where.Add(SlaPredicate(sla, config, entity, ctx));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var q = query.Search.Trim();
            var numberMatch = System.Text.RegularExpressions.Regex.Match(q, @"^(?:[A-Za-z]+-)?0*(\d{1,12})$");
            var search = $"r.search @@ websearch_to_tsquery('simple', {ctx.Param(q)})";
            where.Add(numberMatch.Success ? $"({search} OR r.number = {ctx.Param(long.Parse(numberMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))})" : search);
        }

        return string.Join(" AND ", where);
    }

    /// <summary>
    /// Records of the entity whose timer of the policy (and target) is in the state; RLS scopes the timers. A breach
    /// counts once recorded, even after the clock stops (ADR 0018). At risk means a recorded warning on a running or
    /// paused timer that has not breached: the same moment the at-risk alert is raised, never a guess from the clock.
    /// </summary>
    private static string SlaPredicate(SlaFilter sla, SpecCatalog config, EntitySpec entity, Ctx ctx)
    {
        var policy = config.FindSlaPolicy(sla.Policy);
        if (policy is null || policy.Entity != entity.Id || (sla.Target is { } target && policy.FindTarget(target) is null) || !MetricSlaStates.All.Contains(sla.State))
        {
            throw new KernelException("invalid_filter", $"'{sla}' is not an SLA condition of {entity.Label}.", 400, sla.ToString(), "Use <policy>[/<target>]:breached or :at_risk.");
        }

        var targetSql = sla.Target is null ? string.Empty : $" AND t.target_id = {ctx.Param(sla.Target)}";
        var stateSql = sla.State == MetricSlaStates.AtRisk
            ? "t.status IN ('running', 'paused') AND t.warned_at IS NOT NULL AND t.breached_at IS NULL"
            : "t.status <> 'cancelled' AND t.breached_at IS NOT NULL";
        return $"EXISTS (SELECT 1 FROM kernel.sla_timers t WHERE t.tenant_id = r.tenant_id AND t.environment_id = r.environment_id AND t.record_id = r.id AND t.policy_id = {ctx.Param(sla.Policy)}{targetSql} AND {stateSql})";
    }

    /// <summary>
    /// The SQL value records are grouped by (the same expression an == filter compares, so a group's count equals its
    /// drill-down) and its order (choice order for choice fields). Null when the field cannot group or is hidden.
    /// </summary>
    public static (string Key, string Order)? GroupKey(EntitySpec entity, EffectivePermissions permissions, Ctx ctx, string fieldKey)
    {
        if (entity.FindField(fieldKey) is not { } field || !ConfigValidator.IsGroupable(field) || permissions.FieldAccess(entity, field.Key) == FieldAccessLevels.Hidden)
        {
            return null;
        }

        var key = ctx.GroupKeySql(field.Key);
        var order = FieldTypes.UsesChoiceList(field.Type) && ctx.ChoiceOrder(field) is { } choices ? $"array_position({choices}, {key})" : key;
        return (key, order);
    }

    public static string PermissionPredicate(EntitySpec entity, EffectivePermissions permissions, Ctx ctx)
    {
        var access = permissions.Entity(entity.Id);
        if (!permissions.Context.HasScope(Tenancy.AgentScopes.RecordsRead))
        {
            return "false";
        }

        switch (access.Read)
        {
            case RecordScopes.All:
                return "true";
            case RecordScopes.Related:
                var parts = new List<string>();
                foreach (var af in entity.AccessFields)
                {
                    var field = entity.FindField(af.Field);
                    if (field?.Type == FieldTypes.Principals)
                    {
                        var ids = permissions.Facts.Groups.Select(g => g.ToString()).Prepend(permissions.Facts.PrincipalId.ToString()).ToArray();
                        var key = ctx.Param(af.Field);
                        parts.Add($"(jsonb_typeof(r.data->{key}) = 'array' AND (r.data->{key}) ?| {ctx.Param(ids)})");
                    }
                    else if (field?.Type == FieldTypes.Group)
                    {
                        if (permissions.Facts.Groups.Count > 0)
                        {
                            parts.Add($"(r.data->>{ctx.Param(af.Field)}) = ANY({ctx.Param(permissions.Facts.Groups.Select(g => g.ToString()).ToArray())})");
                        }
                    }
                    else
                    {
                        parts.Add($"(r.data->>{ctx.Param(af.Field)}) = {ctx.Param(permissions.Facts.PrincipalId.ToString())}");
                    }
                }

                return parts.Count == 0 ? "false" : string.Join(" OR ", parts);
            default:
                return "false";
        }
    }

    private static string OrderBy(SortSpec? sort, EntitySpec entity, EffectivePermissions permissions, Ctx ctx)
    {
        if (sort is null || entity.FindField(sort.Field) is not { } field || permissions.FieldAccess(entity, field.Key) == FieldAccessLevels.Hidden)
        {
            return "r.updated_at DESC, r.id";
        }

        var dir = sort.Descending ? "DESC" : "ASC";
        var expr = field.Key switch
        {
            "created_at" when field.System => "r.created_at",
            "updated_at" when field.System => "r.updated_at",
            _ when field.Type == FieldTypes.AutoNumber => "r.number",
            _ when FieldTypes.IsNumber(field.Type) => $"(r.data->>{ctx.Param(field.Key)})::numeric",
            _ when FieldTypes.UsesChoiceList(field.Type) && field.Type != FieldTypes.MultiSelect && ctx.ChoiceOrder(field) is { } order => $"array_position({order}, r.data->>{ctx.Param(field.Key)})",
            _ => $"(r.data->>{ctx.Param(field.Key)})",
        };
        return $"{expr} {dir} NULLS LAST, r.id";
    }

    public sealed class Ctx(SpecCatalog config, EntitySpec entity, EffectivePermissions permissions)
    {
        private readonly List<NpgsqlParameter> _parameters = [];

        public IReadOnlyList<NpgsqlParameter> Parameters => _parameters;

        public string Param(object value)
        {
            var p = value switch
            {
                double d => new NpgsqlParameter { Value = (decimal)d, NpgsqlDbType = NpgsqlDbType.Numeric },
                long l => new NpgsqlParameter { Value = l, NpgsqlDbType = NpgsqlDbType.Bigint },
                bool b => new NpgsqlParameter { Value = b, NpgsqlDbType = NpgsqlDbType.Boolean },
                string[] arr => new NpgsqlParameter { Value = arr, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text },
                _ => new NpgsqlParameter { Value = value.ToString(), NpgsqlDbType = NpgsqlDbType.Text },
            };
            _parameters.Add(p);
            return $"${_parameters.Count}";
        }

        /// <summary>Choice fields sort by option order (p1 before p4), not alphabetically.</summary>
        public string? ChoiceOrder(FieldSpec field) =>
            field.ChoiceList is not null && config.FindChoiceList(field.ChoiceList) is { } list ? Param(list.Options.Select(o => o.Id).ToArray()) : null;

        /// <summary>Boolean SQL for one filter expression; refuses filters on fields the viewer cannot see.</summary>
        public string Filter(string filter)
        {
            CompiledExpression compiled;
            try
            {
                compiled = Expression.Compile(filter, EntitySchema.For(entity), Limits);
            }
            catch (ExpressionException ex)
            {
                throw new KernelException("invalid_filter", $"The filter is invalid: {ex.Message}", 400, filter, ex.Suggestion);
            }

            foreach (var reference in compiled.References.Where(r => r.StartsWith("record.", StringComparison.Ordinal)))
            {
                var key = reference[7..];
                if (key != "id" && permissions.FieldAccess(entity, key) == FieldAccessLevels.Hidden)
                {
                    // Filtering on a hidden field would leak its values through result membership and counts.
                    throw new KernelException("invalid_filter", $"You cannot filter on {entity.FindField(key)?.Label ?? key}.", 400, filter);
                }
            }

            return Bool(compiled.Root);
        }

        /// <summary>The SQL value of a field for grouping; toggles are never null so they group as true/false.</summary>
        public string GroupKeySql(string key) => Field(key) switch
        {
            (var sql, "bool") => sql,
            (var sql, "number") => $"({sql})::text",
            (var sql, _) => sql,
        };

        /// <summary>Raw SQL value of a field and its kind (text, number, bool, jsonarray).</summary>
        public (string Sql, string Kind) FieldSql(string key) => Field(key);

        /// <summary>Boolean SQL for a node, never NULL.</summary>
        public string Bool(Node node) => node switch
        {
            Binary { Op: "&&" } b => $"({Bool(b.Left)} AND {Bool(b.Right)})",
            Binary { Op: "||" } b => $"({Bool(b.Left)} OR {Bool(b.Right)})",
            Unary { Op: "!" } u => $"(NOT {Bool(u.Operand)})",
            Literal { Value: bool v } => v ? "true" : "false",
            _ => $"COALESCE({Value(node).Sql}, false)",
        };

        private (string Sql, string Kind) Value(Node node)
        {
            switch (node)
            {
                case Literal lit:
                    return lit.Value switch
                    {
                        null => ("NULL", "null"),
                        bool b => (b ? "true" : "false", "bool"),
                        double d => (Param(d), "number"),
                        string s => (Param(s), "text"),
                        _ => throw Unsupported("this literal"),
                    };
                case Select { Operand: Ident { Name: "record" } } sel:
                    return Field(sel.Field);
                case Select { Operand: Ident { Name: "user" } } sel:
                    return sel.Field switch
                    {
                        "id" => (Param(permissions.Facts.PrincipalId.ToString()), "text"),
                        "groups" => (Param(permissions.Facts.Groups.Select(g => g.ToString()).ToArray()), "textarray"),
                        "roles" => (Param(permissions.Facts.Roles.Keys.ToArray()), "textarray"),
                        _ => throw Unsupported($"user.{sel.Field}"),
                    };
                case ListLiteral list:
                    var items = list.Items.Select(i => i is Literal { Value: string s } ? s : i is Literal { Value: double d } ? d.ToString(System.Globalization.CultureInfo.InvariantCulture) : throw Unsupported("non-literal list items")).ToArray();
                    return (Param(items), "textarray");
                case Unary { Op: "!" }:
                case Binary { Op: "&&" or "||" }:
                    return (Bool(node), "bool");
                case Unary { Op: "-" } neg:
                    return ($"(-{Value(neg.Operand).Sql})", "number");
                case Binary bin:
                    return Binary(bin);
                case Conditional c:
                    var t = Value(c.IfTrue);
                    var f = Value(c.IfFalse);
                    return ($"(CASE WHEN {Bool(c.Test)} THEN {t.Sql} ELSE {f.Sql} END)", t.Kind == "null" ? f.Kind : t.Kind);
                case Call call:
                    return Call(call);
                default:
                    throw Unsupported("this expression");
            }
        }

        private (string Sql, string Kind) Field(string key)
        {
            if (key == "id")
            {
                return ("r.id::text", "text");
            }

            var field = entity.FindField(key) ?? throw new KernelException("invalid_filter", $"Unknown field '{key}'.", 400);
            var k = Param(key);
            return field.Type switch
            {
                _ when field.System && key == "created_at" => ("to_char(r.created_at AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.MS\"Z\"')", "text"),
                _ when field.System && key == "updated_at" => ("to_char(r.updated_at AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.MS\"Z\"')", "text"),
                FieldTypes.Integer or FieldTypes.Decimal => ($"(r.data->>{k})::numeric", "number"),
                FieldTypes.Toggle => ($"COALESCE((r.data->>{k})::boolean, false)", "bool"),
                FieldTypes.MultiSelect => ($"(r.data->{k})", "jsonarray"),
                FieldTypes.AutoNumber => ($"(r.data->>{k})", "text"),
                _ => ($"(r.data->>{k})", "text"),
            };
        }

        private (string Sql, string Kind) Binary(Binary bin)
        {
            if (bin.Op == "in")
            {
                var needle = Value(bin.Left);
                var hay = Value(bin.Right);
                return hay.Kind switch
                {
                    "textarray" => ($"({Text(needle)} = ANY({hay.Sql}))", "bool"),
                    "jsonarray" => ($"({hay.Sql} ? {Text(needle)})", "bool"),
                    _ => throw Unsupported("'in' with this right-hand side"),
                };
            }

            var l = Value(bin.Left);
            var r = Value(bin.Right);
            switch (bin.Op)
            {
                case "==":
                case "!=":
                    var not = bin.Op == "!=" ? string.Empty : "NOT ";
                    if (l.Kind == "null" || r.Kind == "null")
                    {
                        var other = l.Kind == "null" ? r : l;
                        return ($"({other.Sql} IS {(bin.Op == "==" ? string.Empty : "NOT ")}NULL)", "bool");
                    }

                    return ($"({Coerce(l, r.Kind)} IS {not}DISTINCT FROM {Coerce(r, l.Kind)})", "bool");
                case "<":
                case "<=":
                case ">":
                case ">=":
                    return ($"({Coerce(l, r.Kind)} {bin.Op} {Coerce(r, l.Kind)})", "bool");
                case "+" when l.Kind == "text" || r.Kind == "text":
                    return ($"({l.Sql} || {r.Sql})", "text");
                case "+":
                case "-":
                case "*":
                    return ($"({l.Sql} {bin.Op} {r.Sql})", "number");
                case "/":
                case "%":
                    // Division by zero yields NULL (row excluded) rather than aborting the whole query.
                    return ($"({l.Sql} {bin.Op} NULLIF({r.Sql}, 0))", "number");
                default:
                    throw Unsupported($"'{bin.Op}'");
            }
        }

        private (string Sql, string Kind) Call(Call call)
        {
            switch (call.Function)
            {
                case "has":
                    var sel = (Select)call.Args[0];
                    if (sel.Operand is not Ident { Name: "record" })
                    {
                        throw Unsupported("has() outside record fields");
                    }

                    _ = entity.FindField(sel.Field) ?? throw new KernelException("invalid_filter", $"Unknown field '{sel.Field}'.", 400);
                    var k = Param(sel.Field);
                    return ($"(r.data ? {k} AND jsonb_typeof(r.data->{k}) <> 'null')", "bool");
                case "size":
                    var target = Value(call.Target ?? call.Args[0]);
                    return target.Kind == "jsonarray" ? ($"COALESCE(jsonb_array_length({target.Sql}), 0)", "number") : ($"COALESCE(length({target.Sql}), 0)", "number");
                case "contains":
                    return ($"(strpos({Value(call.Target!).Sql}, {Value(call.Args[0]).Sql}) > 0)", "bool");
                case "startsWith":
                    return ($"starts_with({Value(call.Target!).Sql}, {Value(call.Args[0]).Sql})", "bool");
                case "endsWith":
                    var s = Value(call.Target!).Sql;
                    var suffix = Value(call.Args[0]).Sql;
                    return ($"(right({s}, length({suffix})) = {suffix})", "bool");
                case "lowerAscii":
                    return ($"lower({Value(call.Target!).Sql})", "text");
                case "upperAscii":
                    return ($"upper({Value(call.Target!).Sql})", "text");
                case "trim":
                    return ($"btrim({Value(call.Target!).Sql})", "text");
                default:
                    throw Unsupported($"{call.Function}() in list filters");
            }
        }

        private static string Text((string Sql, string Kind) v) => v.Kind == "number" ? $"({v.Sql})::text" : v.Sql;

        private static string Coerce((string Sql, string Kind) v, string otherKind) =>
            v.Kind == "number" && otherKind == "text" ? $"({v.Sql})::text" : v.Sql;

        private static KernelException Unsupported(string what) =>
            new("unsupported_filter", $"List filters do not support {what} yet.", 400, null, "Use comparisons, in, has(), contains(), startsWith(), &&, || and !.");
    }
}
