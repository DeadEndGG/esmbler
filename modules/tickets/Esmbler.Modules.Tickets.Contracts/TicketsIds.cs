namespace Esmbler.Modules.Tickets.Contracts;

/// <summary>Stable public identifiers of the Tickets module. Other modules may depend on these, never on internals.</summary>
public static class TicketsIds
{
    public const string Module = "core.tickets";
    public const string Ticket = "core.tickets.ticket";
    public const string RecordView = "core.tickets.record";
    public const string ListView = "core.tickets.list";
    public const string QuickCreateView = "core.tickets.quick_create";
    public const string Workflow = "core.tickets.lifecycle";
    public const string AgentRole = "core.tickets.agent";
    public const string RequesterRole = "core.tickets.requester";
    public const string AdminRole = "core.tickets.admin";
}
