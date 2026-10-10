using Esmbler.Kernel.Configuration;
using Esmbler.Kernel.Infrastructure.Configuration;
using Esmbler.Kernel.Infrastructure.Services;
using Esmbler.Modules.Tickets.Contracts;

namespace Esmbler.Hosting;

/// <summary>
/// Two demo tenants (REQ-ACC-01 fixture). Alex belongs to both to show explicit tenant switching. Lives in the composition
/// root because it names business modules; the kernel's provisioning service stays module-agnostic (ADR 0012).
/// </summary>
public sealed class DemoTenants(ProvisioningService provisioning, ModuleCatalog catalog)
{
    private const string Password = ProvisioningService.DemoPassword;

    /// <summary>Provisions acme and globex unless acme already exists. Returns whether anything was created.</summary>
    public async Task<bool> EnsureAsync(CancellationToken ct)
    {
        if (await provisioning.TenantExistsAsync("acme", ct))
        {
            return false;
        }

        const string Admin = PlatformModule.TenantAdminRole;
        const string Agent = TicketsIds.AgentRole;
        const string Requester = TicketsIds.RequesterRole;
        // Acme starts on Tickets 1.0.0 to demonstrate upgrades; Globex runs the newest release (service requests and
        // approvals: Jamie approves requests).
        (string, string)[] first = [(TicketsIds.Module, "1.0.0")];
        (string, string)[] latest = [(TicketsIds.Module, catalog.Latest(TicketsIds.Module).Version)];
        await provisioning.ProvisionAsync(new TenantSeed(
            "acme",
            "Acme Corp",
            [
                new UserSeed("alex", "alex@acme.example", "Alex Rivera", Password, [Admin], ["service_desk"]),
                new UserSeed("sam", "sam@acme.example", "Sam Okafor", Password, [], ["service_desk", "network"]),
                new UserSeed("dana", "dana@acme.example", "Dana Kim", Password, [Requester], []),
                new UserSeed("jordan", "jordan@acme.example", "Jordan Lee", Password, [Requester], []),
                new UserSeed("riley", "riley@acme.example", "Riley Chen", Password, [Requester], []),
            ],
            [new GroupSeed("service_desk", "Service Desk", [Agent]), new GroupSeed("network", "Network", [Agent])],
            first), ct);
        await provisioning.ProvisionAsync(new TenantSeed(
            "globex",
            "Globex Inc",
            [
                new UserSeed("alex", "alex@acme.example", "Alex Rivera", Password, [Admin], []),
                new UserSeed("morgan", "morgan@globex.example", "Morgan Diaz", Password, [Admin], ["service_desk"]),
                new UserSeed("sam", "casey@globex.example", "Casey Novak", Password, [], ["network"]),
                new UserSeed("dana", "taylor@globex.example", "Taylor Brooks", Password, [Requester], []),
                new UserSeed("jordan", "jamie@globex.example", "Jamie Ortiz", Password, [Requester, TicketsIds.ApproverRole], []),
                new UserSeed("riley", "quinn@globex.example", "Quinn Patel", Password, [Requester], []),
            ],
            [new GroupSeed("service_desk", "Service Desk", [Agent]), new GroupSeed("network", "Network", [Agent])],
            latest), ct);
        return true;
    }
}
