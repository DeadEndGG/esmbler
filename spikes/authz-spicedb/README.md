# SpiceDB authorization spike (light)

Throwaway comparison spike for [ADR 0004](../../docs/adr/0004-authorization-engine.md). It runs a subset of the Tickets model on SpiceDB with the official `Authzed.Net` client.

## How to run

```bash
export PATH=/root/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1
docker run -d --rm --name spicedb-spike -p 50051:50051 authzed/spicedb:latest serve --grpc-preshared-key spike-key --datastore-engine memory
cd spikes/authz-spicedb/src && dotnet run
docker stop spicedb-spike
```

The schema is `schema/tickets.zed`. It mirrors `../authz-openfga/model/tickets.fga` and uses a caveat for the priority rule.

## Results observed (2026-10-10, SpiceDB v1.56.2)

11/11 PASS. The checks covered requester, nested group, a negative case, the caveat priority guard for both a non-MIM and a MIM user, tenant isolation by ID prefix, revocation (with the `DeletedAt` ZedToken and with `fully_consistent`), LookupResources, and fail closed (`RpcException(Unavailable)`).

- CheckPermission over gRPC, nested group, 1000 sequential calls: **1.89 ms mean, 1.44 ms p50, 4.85 ms p95**. That is faster than OpenFGA's HTTP path uncached on the same contended host.

## Findings

- **No contextual relationships.** `CheckPermissionRequest` carries only caveat `Context`. Ticket requester, assignee and owner_group therefore had to be written as relationships, which means dual-writing on every record write. That is the main design point that counts against SpiceDB for us.
- **No stores or multi-tenancy primitive.** Tenant isolation relies on ID prefixes (`acme_…`), per-tenant definition prefixes, or separate deployments or datastores. Every call site must remember the prefix.
- **The client is generated stubs only.** `Authzed.Net` 1.6.0 is official (owner `authzed`), published 2026-05-26 with 673k downloads, and targets net8.0 only. It contains the generated gRPC/protobuf stubs and nothing more: no client wrapper, retry, or DI helpers. You wire `GrpcChannel` and call credentials yourself, and plaintext needs `UnsafeUseInsecureChannelCallCredentials = true`. It also pulls in **`Grpc.Core` 2.46.6**, the legacy native C-core package, as a dependency. The assembly version is `0.1.0.0`.
- **Quantization gotcha (seen once, intermittent).** A `minimize_latency` CheckPermission right after `WriteSchema` failed with `FailedPrecondition: object definition 'ticket' not found` because it read a quantized snapshot taken before the schema write. Use `at_least_as_fresh` with the write's ZedToken.
- **Community alternative:** the `SpiceDb` package (JalexSocial, 1.6.2, 437k downloads, last published 2025-06-17) is not official.
