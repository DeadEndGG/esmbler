# Esmbler.Hosting

Composition root shared by the API, MCP, and worker hosts: service registration, JWT authentication, configuration binding, and the demo tenant seed (`DemoTenants.cs`, which names Tickets IDs only through `TicketsIds`: Acme on Tickets 1.0.0, Globex on the newest release with Jamie as approver). `ServiceRegistration` registers the kernel services, event consumers (including `ApprovalConsumer`) and job handlers (`upgrade.run`, `workflow.migrate`, `approval.expire`); a new consumer or job kind is registered here.

- Invariants: secrets only from environment/configuration; API tokens use audience `esmbler-api`, MCP tokens `esmbler-mcp` (no passthrough).
