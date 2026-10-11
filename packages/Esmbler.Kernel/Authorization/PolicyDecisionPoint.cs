using System.Text.Json.Nodes;
using Esmbler.Expressions;
using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Tenancy;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Authorization;

public sealed record EntityAccess(bool Create, string Read, string Update, bool Delete, IReadOnlyList<string> UpdateConditions)
{
    public static EntityAccess NoAccess { get; } = new(false, RecordScopes.None, RecordScopes.None, false, []);
}

/// <summary>One reason in an access explanation.</summary>
public sealed record AccessReason(string Kind, string Text);

/// <summary>
/// Policy decision point (REQ-AUTHZ-002): combines versioned permission-set specs from the pinned configuration
/// with relationship facts. Entity → record → field → action. Agents get the intersection with their scopes.
/// </summary>
public sealed class EffectivePermissions
{
    private static readonly int[] ScopeRank = [0, 1, 2];
    private readonly EffectiveConfig _config;
    private readonly Dictionary<string, EntityAccess> _entities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PermissionSetSpec>> _setsByEntity = new(StringComparer.Ordinal);

    public EffectivePermissions(EffectiveConfig config, AuthorizationFacts facts, RequestContext context)
    {
        _config = config;
        Facts = facts;
        Context = context;
        var roles = facts.Roles.Keys.ToHashSet(StringComparer.Ordinal);
        Sets = config.PermissionSets.Where(ps => ps.Roles.Exists(roles.Contains)).ToList();
        var actions = Sets.SelectMany(s => s.Actions).ToHashSet(StringComparer.Ordinal);
        if (context.AgentId is not null)
        {
            // Agents never administer permissions or publish directly; publishing needs a human approval (REQ-AUTHZ-007/008).
            actions.Remove(PlatformActions.PermissionsAdmin);
            actions.Remove(PlatformActions.ConfigPublish);
            actions.Remove(PlatformActions.ModulesManage);
            actions.Remove(PlatformActions.NotificationsManage);
            actions.Remove(PlatformActions.AlertsRespond);
            actions.Remove(PlatformActions.AlertsManage);
            actions.Remove(PlatformActions.AuditExport);
            actions.Remove(PlatformActions.AuditManage);
            if (!context.HasScope(AgentScopes.ConfigEdit))
            {
                actions.Remove(PlatformActions.ConfigEdit);
            }

            actions.Remove(PlatformActions.RecordsExport);
        }

        Actions = actions;
        foreach (var entity in config.Entities)
        {
            var applicable = Sets.Where(s => s.Entities.Exists(e => e.Entity == entity.Id || e.Entity == "*")).ToList();
            _setsByEntity[entity.Id] = applicable;
            _entities[entity.Id] = Combine(entity.Id, applicable, context);
        }
    }

    public AuthorizationFacts Facts { get; }

    public RequestContext Context { get; }

    public IReadOnlyList<PermissionSetSpec> Sets { get; }

    public IReadOnlySet<string> Actions { get; }

    public IReadOnlyCollection<string> Roles => Facts.Roles.Keys.ToList();

    public bool Can(string action) => Actions.Contains(action);

    public EntityAccess Entity(string entityId) => _entities.GetValueOrDefault(entityId, EntityAccess.NoAccess);

    /// <summary>Whether the principal is related to the record through one of the entity's access fields.</summary>
    public (bool Related, bool CanEdit) Relation(EntitySpec entity, JsonObject data)
    {
        bool related = false, canEdit = false;
        foreach (var access in entity.AccessFields)
        {
            var field = entity.FindField(access.Field);
            if (field?.Type == FieldTypes.Principals)
            {
                // A platform-maintained list of users and groups (approvers, ADR 0016).
                if (data[access.Field] is JsonArray list && list.OfType<JsonValue>().Any(v => v.TryGetValue<string>(out var s) && Guid.TryParse(s, out var p) && (p == Facts.PrincipalId || Facts.Groups.Contains(p))))
                {
                    related = true;
                    canEdit |= access.CanEdit;
                }

                continue;
            }

            if (data[access.Field] is not JsonValue value || !value.TryGetValue<string>(out var raw) || !Guid.TryParse(raw, out var id))
            {
                continue;
            }

            var match = field?.Type == FieldTypes.Group ? Facts.Groups.Contains(id) : id == Facts.PrincipalId;
            if (match)
            {
                related = true;
                canEdit |= access.CanEdit;
            }
        }

        return (related, canEdit);
    }

    public bool CanRead(EntitySpec entity, JsonObject data)
    {
        if (!Context.HasScope(AgentScopes.RecordsRead))
        {
            return false;
        }

        var access = Entity(entity.Id);
        return access.Read == RecordScopes.All || (access.Read == RecordScopes.Related && Relation(entity, data).Related);
    }

    public bool CanUpdate(EntitySpec entity, JsonObject data)
    {
        if (!Context.HasScope(AgentScopes.RecordsWrite))
        {
            return false;
        }

        var access = Entity(entity.Id);
        var allowed = access.Update == RecordScopes.All || (access.Update == RecordScopes.Related && Relation(entity, data).CanEdit);
        if (!allowed)
        {
            return false;
        }

        if (access.UpdateConditions.Count == 0)
        {
            return true;
        }

        var vars = Variables(entity, data);
        return access.UpdateConditions.Any(c => Expression.Compile(c, EntitySchema.For(entity)).EvaluateBool(vars));
    }

    public bool CanCreate(EntitySpec entity) => Context.HasScope(AgentScopes.RecordsWrite) && Entity(entity.Id).Create;

    /// <summary>Field access: most permissive level across permission sets that grant access to the entity.</summary>
    public string FieldAccess(EntitySpec entity, string fieldKey)
    {
        var best = -1;
        foreach (var set in _setsByEntity.GetValueOrDefault(entity.Id, []))
        {
            var entityGrant = set.Entities.FirstOrDefault(e => e.Entity == entity.Id) ?? set.Entities.First(e => e.Entity == "*");
            if (entityGrant.Read == RecordScopes.None && !entityGrant.Create)
            {
                continue;
            }

            var specific = set.Fields.FirstOrDefault(f => f.Entity == entity.Id && f.Field == fieldKey)
                ?? set.Fields.FirstOrDefault(f => f.Entity == entity.Id && f.Field == "*");
            var level = specific?.Access ?? (entityGrant.Update != RecordScopes.None || entityGrant.Create ? FieldAccessLevels.Write : FieldAccessLevels.Read);
            best = Math.Max(best, Rank(level));
        }

        var result = best switch { 2 => FieldAccessLevels.Write, 1 => FieldAccessLevels.Read, _ => FieldAccessLevels.Hidden };
        if (result == FieldAccessLevels.Write && !Context.HasScope(AgentScopes.RecordsWrite))
        {
            result = FieldAccessLevels.Read;
        }

        return result;
    }

    public IReadOnlyDictionary<string, string> FieldAccessMap(EntitySpec entity) =>
        entity.Fields.ToDictionary(f => f.Key, f => FieldAccess(entity, f.Key), StringComparer.Ordinal);

    public Dictionary<string, object?> Variables(EntitySpec entity, JsonObject data, JsonObject? prior = null, string? now = null) => new(StringComparer.Ordinal)
    {
        ["record"] = Values.MapFromJson(data),
        ["prior"] = Values.MapFromJson(prior),
        ["user"] = UserVariable(),
        ["now"] = now,
    };

    public IReadOnlyDictionary<string, object?> UserVariable() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["id"] = Facts.PrincipalId.ToString(),
        ["roles"] = Facts.Roles.Keys.Cast<object?>().ToList(),
        ["groups"] = Facts.Groups.Select(g => (object?)g.ToString()).ToList(),
    };

    /// <summary>"Why can X see/edit this?" (REQ-AUTHZ-009). Lists the facts and specs that produced the decision.</summary>
    public IReadOnlyList<AccessReason> Explain(EntitySpec entity, JsonObject? data, string? fieldKey = null)
    {
        var reasons = new List<AccessReason>();
        if (Facts.Roles.Count == 0)
        {
            reasons.Add(new AccessReason("role", "Has no roles in this tenant."));
        }

        foreach (var (role, sources) in Facts.Roles)
        {
            var label = _config.Roles.Find(r => r.Id == role)?.Label ?? role;
            reasons.Add(new AccessReason("role", $"Has role {label} ({string.Join(", ", sources.Select(s => s == "direct" ? "assigned directly" : $"via {s.Replace("group:", "group ", StringComparison.Ordinal)}"))})."));
        }

        foreach (var set in _setsByEntity.GetValueOrDefault(entity.Id, []))
        {
            var grant = set.Entities.FirstOrDefault(e => e.Entity == entity.Id) ?? set.Entities.First(e => e.Entity == "*");
            reasons.Add(new AccessReason("permission_set", $"Permission set {set.Label} grants {entity.PluralLabel}: create {(grant.Create ? "yes" : "no")}, read {grant.Read}, update {grant.Update}."));
        }

        var access = Entity(entity.Id);
        if (data is not null)
        {
            var (related, canEdit) = Relation(entity, data);
            foreach (var af in entity.AccessFields)
            {
                var f = entity.FindField(af.Field);
                if (f?.Type == FieldTypes.Principals && data[af.Field] is JsonArray list
                    && list.OfType<JsonValue>().Any(x => x.TryGetValue<string>(out var s) && Guid.TryParse(s, out var p) && (p == Facts.PrincipalId || Facts.Groups.Contains(p))))
                {
                    reasons.Add(new AccessReason("relationship", $"Is listed in {f.Label} (as an approver or their delegate, or through a group)."));
                    continue;
                }

                if (data[af.Field] is JsonValue v && v.TryGetValue<string>(out var raw) && Guid.TryParse(raw, out var id)
                    && (f?.Type == FieldTypes.Group ? Facts.Groups.Contains(id) : id == Facts.PrincipalId))
                {
                    reasons.Add(new AccessReason("relationship", $"Is related to this record as {f?.Label ?? af.Field}{(f?.Type == FieldTypes.Group ? " (member of that group)" : string.Empty)}."));
                }
            }

            var canRead = access.Read == RecordScopes.All || (access.Read == RecordScopes.Related && related);
            reasons.Add(new AccessReason("decision", canRead
                ? $"Can see this record (read scope: {access.Read})."
                : access.Read == RecordScopes.Related ? "Cannot see this record: read is limited to related records and none of the access fields match." : "Cannot see records of this type."));
            var canUpdate = access.Update == RecordScopes.All || (access.Update == RecordScopes.Related && canEdit);
            reasons.Add(new AccessReason("decision", canUpdate ? $"Can edit this record (update scope: {access.Update})." : "Cannot edit this record."));
        }

        if (fieldKey is not null)
        {
            reasons.Add(new AccessReason("field", $"Field {entity.FindField(fieldKey)?.Label ?? fieldKey}: {FieldAccess(entity, fieldKey)}."));
        }

        if (Context.AgentId is not null)
        {
            reasons.Add(new AccessReason("agent", $"Acting as an AI agent limited to scopes: {string.Join(", ", Context.Scopes ?? new HashSet<string>())}. Agents never publish or change permissions themselves."));
        }

        return reasons;
    }

    private EntityAccess Combine(string entityId, List<PermissionSetSpec> sets, RequestContext context)
    {
        if (sets.Count == 0)
        {
            return EntityAccess.NoAccess;
        }

        bool create = false, delete = false;
        string read = RecordScopes.None, update = RecordScopes.None;
        var conditions = new List<string>();
        var unconditional = false;
        foreach (var set in sets)
        {
            var grant = set.Entities.FirstOrDefault(e => e.Entity == entityId) ?? set.Entities.First(e => e.Entity == "*");
            create |= grant.Create;
            delete |= grant.Delete;
            read = Max(read, grant.Read);
            if (Rank(grant.Update) > 0)
            {
                if (grant.UpdateCondition is null)
                {
                    unconditional = true;
                }
                else
                {
                    conditions.Add(grant.UpdateCondition);
                }
            }

            update = Max(update, grant.Update);
        }

        if (context.AgentId is not null)
        {
            if (!context.HasScope(AgentScopes.RecordsWrite))
            {
                create = false;
                delete = false;
                update = RecordScopes.None;
            }

            if (!context.HasScope(AgentScopes.RecordsRead))
            {
                read = RecordScopes.None;
            }

            delete = false;
        }

        return new EntityAccess(create, read, update, delete, unconditional ? [] : conditions);
    }

    private static string Max(string a, string b) => ScopeValue(a) >= ScopeValue(b) ? a : b;

    private static int ScopeValue(string scope) => scope switch { RecordScopes.All => ScopeRank[2], RecordScopes.Related => ScopeRank[1], _ => ScopeRank[0] };

    private static int Rank(string level) => level switch
    {
        FieldAccessLevels.Write or RecordScopes.All => 2,
        FieldAccessLevels.Read or RecordScopes.Related => 1,
        _ => 0,
    };
}
