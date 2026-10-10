// Esmbler Gate 0 authorization spike (light): SpiceDB + Authzed.Net with the Tickets model.
// Throwaway code. See ../README.md.
using System.Diagnostics;
using Authzed.Api.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;

var endpoint = args.Length > 0 ? args[0] : "http://localhost:50051";
const string presharedKey = "spike-key";
var results = new List<(string, bool)>();
void Assert(string name, bool ok, string detail = "") { results.Add((name, ok)); Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -- " + detail : "")}"); }
void Metric(string s) => Console.WriteLine($"  [metric] {s}");

// Gotcha: Authzed.Net is generated gRPC stubs only. Auth, channel, retries and timeouts are on you.
// Bearer call credentials over plaintext require UnsafeUseInsecureChannelCallCredentials.
var creds = CallCredentials.FromInterceptor((_, md) => { md.Add("Authorization", $"Bearer {presharedKey}"); return Task.CompletedTask; });
using var channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions
{
    Credentials = ChannelCredentials.Create(ChannelCredentials.Insecure, creds),
    UnsafeUseInsecureChannelCallCredentials = true,
});
var schemaClient = new SchemaService.SchemaServiceClient(channel);
var perms = new PermissionsService.PermissionsServiceClient(channel);

var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "schema", "tickets.zed"));
await schemaClient.WriteSchemaAsync(new WriteSchemaRequest { Schema = schema });

// No stores in SpiceDB: tenant isolation needs ID prefixes (or per-tenant definition prefixes / deployments).
static string Id(string tenant, string id) => $"{tenant}_{id}";
const string A = "acme";
const string B = "globex";

static RelationshipUpdate Touch(string resType, string resId, string rel, string subType, string subId, string? subRel = null, string? caveat = null) => new()
{
    Operation = RelationshipUpdate.Types.Operation.Touch,
    Relationship = new Relationship
    {
        Resource = new ObjectReference { ObjectType = resType, ObjectId = resId },
        Relation = rel,
        Subject = new SubjectReference { Object = new ObjectReference { ObjectType = subType, ObjectId = subId }, OptionalRelation = subRel ?? "" },
        OptionalCaveat = caveat is null ? null : new ContextualizedCaveat { CaveatName = caveat },
    },
};

var updates = new List<RelationshipUpdate>
{
    Touch("group", Id(A, "network-team"), "member", "user", Id(A, "carol")),
    Touch("group", Id(A, "l1-support"), "member", "group", Id(A, "network-team"), "member"),
    Touch("role", Id(A, "major_incident_manager"), "assignee", "user", Id(A, "hank")),
    Touch("group", Id(A, "l1-support"), "member", "user", Id(A, "hank")),
    Touch("entity_type", Id(A, "ticket"), "priority_editor", "user", "*", caveat: "not_major_incident"),
    Touch("entity_type", Id(A, "ticket"), "priority_editor", "role", Id(A, "major_incident_manager"), "assignee"),
    // Record attributes MUST be stored (no contextual relationships in the v1 Check API) => dual-write on every ticket write.
    Touch("ticket", Id(A, "1"), "entity", "entity_type", Id(A, "ticket")),
    Touch("ticket", Id(A, "1"), "requester", "user", Id(A, "alice")),
    Touch("ticket", Id(A, "1"), "assignee", "user", Id(A, "bob")),
    Touch("ticket", Id(A, "1"), "owner_group", "group", Id(A, "l1-support")),
};
var write = await perms.WriteRelationshipsAsync(new WriteRelationshipsRequest { Updates = { updates } });

async Task<bool> Can(string tenant, string user, string perm, string ticket, int priority, Consistency? c = null)
{
    var res = await perms.CheckPermissionAsync(new CheckPermissionRequest
    {
        Resource = new ObjectReference { ObjectType = "ticket", ObjectId = Id(tenant, ticket) },
        Permission = perm,
        Subject = new SubjectReference { Object = new ObjectReference { ObjectType = "user", ObjectId = Id(tenant, user) } },
        Context = new Struct { Fields = { ["priority"] = Value.ForNumber(priority) } },
        Consistency = c ?? new Consistency { AtLeastAsFresh = write.WrittenAt },
    });
    return res.Permissionship == CheckPermissionResponse.Types.Permissionship.HasPermission;
}

Assert("view T1 alice (requester) => true", await Can(A, "alice", "can_view", "1", 3));
Assert("view T1 carol (nested group) => true", await Can(A, "carol", "can_view", "1", 3));
Assert("view T1 erin => false", !await Can(A, "erin", "can_view", "1", 3));
Assert("edit T1(P3) bob => true", await Can(A, "bob", "can_edit", "1", 3));
Assert("edit T1 as P1 bob => false (caveat)", !await Can(A, "bob", "can_edit", "1", 1));
Assert("edit T1 as P1 hank (MIM) => true", await Can(A, "hank", "can_edit", "1", 1));
Assert("tenant B: carol view => false (prefixed ids)", !await Can(B, "carol", "can_view", "1", 3));

// Revocation with a ZedToken from the delete (read-your-writes) and fully consistent.
var del = await perms.DeleteRelationshipsAsync(new DeleteRelationshipsRequest
{
    RelationshipFilter = new RelationshipFilter { ResourceType = "group", OptionalResourceId = Id(A, "l1-support"), OptionalRelation = "member",
        OptionalSubjectFilter = new SubjectFilter { SubjectType = "group", OptionalSubjectId = Id(A, "network-team") } },
});
Assert("after revoke: carol view (at_least_as_fresh deleted_at) => false", !await Can(A, "carol", "can_view", "1", 3, new Consistency { AtLeastAsFresh = del.DeletedAt }));
Assert("after revoke: carol view (fully_consistent) => false", !await Can(A, "carol", "can_view", "1", 3, new Consistency { FullyConsistent = true }));

// LookupResources (server streaming) for the list path.
var lr = perms.LookupResources(new LookupResourcesRequest
{
    ResourceObjectType = "group", Permission = "member",
    Subject = new SubjectReference { Object = new ObjectReference { ObjectType = "user", ObjectId = Id(A, "hank") } },
    Consistency = new Consistency { FullyConsistent = true },
});
var groups = new List<string>();
await foreach (var r in lr.ResponseStream.ReadAllAsync()) groups.Add(r.ResourceObjectId);
Assert("LookupResources(group, member, hank) => [acme_l1-support]", groups.SequenceEqual([Id(A, "l1-support")]), string.Join(",", groups));

// Gotcha observed: MinimizeLatency right after WriteSchema can read a quantized snapshot that predates
// the schema ("object definition `ticket` not found"). Record it, then time with at_least_as_fresh.
try
{
    await Can(A, "hank", "can_view", "1", 3, new Consistency { MinimizeLatency = true });
    Metric("minimize_latency immediately after schema write: OK");
}
catch (RpcException ex) { Metric($"minimize_latency immediately after schema write: {ex.StatusCode} {ex.Status.Detail}"); }
var sw = new double[1000];
for (var i = 0; i < sw.Length; i++)
{
    var t = Stopwatch.GetTimestamp();
    await Can(A, "hank", "can_view", "1", 3, new Consistency { AtLeastAsFresh = del.DeletedAt });
    sw[i] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
}
Array.Sort(sw);
Metric($"CheckPermission can_view (nested group), 1000 sequential: mean {sw.Average():F2} ms p50 {sw[500]:F2} p95 {sw[950]:F2}");

// Fail closed: dead endpoint throws RpcException(Unavailable); deadline enforces a bound.
using var dead = GrpcChannel.ForAddress("http://localhost:50099");
try
{
    await new PermissionsService.PermissionsServiceClient(dead).CheckPermissionAsync(new CheckPermissionRequest(), deadline: DateTime.UtcNow.AddSeconds(2));
    Assert("dead endpoint throws", false);
}
catch (RpcException ex) { Assert("dead endpoint throws RpcException", true, $"{ex.StatusCode}"); }

var failed = results.Count(r => !r.Item2);
Console.WriteLine($"\n=== SUMMARY: {results.Count - failed}/{results.Count} PASS, {failed} FAIL ===");
return failed == 0 ? 0 : 1;
