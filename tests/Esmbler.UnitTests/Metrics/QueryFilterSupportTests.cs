using Esmbler.Expressions;
using Esmbler.Kernel;
using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Infrastructure.Records;
using Esmbler.Kernel.Records;
using Esmbler.Kernel.Tenancy;
using Esmbler.UnitTests.Configuration;

namespace Esmbler.UnitTests.Metrics;

/// <summary>
/// REQ-WS-004 (ADR 0019): metric filters are validated with <see cref="QueryFilterSupport"/> and run through
/// <see cref="QueryCompiler"/>. The two must agree, or a metric that passed validation would fail on a dashboard.
/// </summary>
public class QueryFilterSupportTests
{
    private const string Ticket = "core.tickets.ticket";

    public static TheoryData<string> Filters => new()
    {
        "record.priority == 'p1'",
        "!(record.status in ['resolved', 'closed'])",
        "has(record.assignee) && record.assignee == user.id",
        "record.title.contains('vpn') || record.title.startsWith('VPN')",
        "record.title.endsWith('down')",
        "record.title.lowerAscii() == 'x' || record.title.upperAscii() == 'X' || record.title.trim() == 'x'",
        "size(record.title) > 3",
        "record.assignment_group in user.groups",
        "record.priority == 'p1' ? record.impact == 'high' : true",
        "-1 < 0",
        "prior.status == 'resolved'",
        "now > record.created_at",
        "record.title.matches('x')",
    };

    [Theory]
    [MemberData(nameof(Filters))]
    public void Filters_the_validator_accepts_compile_to_sql_and_the_ones_it_refuses_do_not(string filter)
    {
        var config = CompilerFixture.Compile("1.6.0").Config;
        var entity = config.FindEntity(Ticket)!;
        var ctx = new RequestContext { TenantId = Guid.NewGuid(), EnvironmentId = Guid.NewGuid(), PrincipalId = Guid.NewGuid() };
        var facts = AuthorizationFacts.None(ctx.PrincipalId) with { Roles = new Dictionary<string, IReadOnlyList<string>> { ["core.tickets.agent"] = ["direct"] } };
        var perms = new EffectivePermissions(config, facts, ctx);

        var supported = QueryFilterSupport.Unsupported(Expression.Compile(filter).Root) is null;
        var compiled = Record.Exception(() => QueryCompiler.Where(new RecordQuery { Entity = Ticket, Filters = [filter] }, config, entity, perms, new QueryCompiler.Ctx(config, entity, perms)));
        if (supported)
        {
            Assert.Null(compiled);
        }
        else
        {
            Assert.IsType<KernelException>(compiled);
        }
    }
}
