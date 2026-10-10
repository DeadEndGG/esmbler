# Esmbler.Hosting

Composition root shared by the API, MCP, and worker hosts: service registration, JWT authentication, configuration binding, and the demo tenant seed (`DemoTenants.cs`, which names Tickets IDs only through `TicketsIds`).

- Invariants: secrets only from environment/configuration; API tokens use audience `esmbler-api`, MCP tokens `esmbler-mcp` (no passthrough).
