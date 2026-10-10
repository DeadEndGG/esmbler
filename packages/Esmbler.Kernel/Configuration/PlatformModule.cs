using Esmbler.Sdk.Modules;
using Esmbler.Sdk.Specs;

namespace Esmbler.Kernel.Configuration;

/// <summary>The always-installed platform layer: the tenant administrator role and its permission set.</summary>
public sealed class PlatformModule : IModuleDefinition
{
    public const string ModuleId = "core.platform";
    public const string TenantAdminRole = "core.platform.tenant_admin";

    private static readonly ModuleSpec Release = new()
    {
        Id = ModuleId,
        Version = "1.0.0",
        Name = "Platform",
        Description = "Kernel-provided roles and permissions present in every tenant.",
        Roles = [new RoleSpec { Id = TenantAdminRole, Label = "Tenant administrator", Description = "Full access within the tenant, including permission administration." }],
        PermissionSets =
        [
            new PermissionSetSpec
            {
                Id = "core.platform.ps_tenant_admin",
                Label = "Tenant administrators",
                Roles = [TenantAdminRole],
                Entities = [new EntityPermissionSpec { Entity = "*", Create = true, Read = RecordScopes.All, Update = RecordScopes.All, Delete = true }],
                Actions = [.. PlatformActions.All],
            },
        ],
    };

    public string Id => ModuleId;

    public string Name => "Platform";

    public IReadOnlyList<ModuleSpec> Releases { get; } = [Release];
}
