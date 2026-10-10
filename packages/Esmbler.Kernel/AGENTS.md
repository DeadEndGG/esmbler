# Esmbler.Kernel

Pure kernel logic (no database or network): configuration composition/validation/compilation, record contract (normalization, write pipeline), policy decision point, request context, ports.

- Owns: `Configuration/` (Composer, ConfigValidator, DependencyGraph, ConfigCompiler, PlatformModule), `Records/` (FieldNormalizer, RecordWriteEngine, `IRecordWriteHook`), `Authorization/` (EffectivePermissions, `IRelationshipGraph`, in-memory fake), `Tenancy/RequestContext`.
- Approvals (ADR 0016): the composer applies `approval_policy.*` ops and catalog item `approvalPolicy`; `ConfigValidator` checks policies, approvers, stage conditions, the request entity's approval binding (request/approved/declined transitions, a read-only `principals` approvers field that should be a non-editable access field) and refuses workflow migrations that would strand pending approvals (`config.strands_approvals`). Access fields of type `principals` match the caller or their groups in `EffectivePermissions`; keep the SQL filter in `Esmbler.Kernel.Infrastructure/Records/QueryCompiler.cs` in step.
- Extension points: `IRecordWriteHook` (platform services plug in: workflow 100, requests 150, assignment 200, rules 300). `RecordWriteEngine.PrepareAsync` takes the record's pinned workflow version and a system-actor flag (ADR 0015).
- Invariants: knows no business module; no Npgsql/EF/OpenFGA (architecture test); every validation message says what, where, and how to fix.
- Tests: `dotnet test tests/Esmbler.UnitTests`.
