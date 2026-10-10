using System.Reflection;
using System.Text.Json;
using Esmbler.Sdk.Events;
using Esmbler.Sdk.Specs;

namespace Esmbler.Sdk.Modules;

/// <summary>
/// A module ships releases (immutable spec catalogs) and may subscribe to events through public contracts only.
/// First-party modules are built exactly like third-party ones (dogfooding rule).
/// </summary>
public interface IModuleDefinition
{
    string Id { get; }

    string Name { get; }

    /// <summary>All shipped releases, oldest first.</summary>
    IReadOnlyList<ModuleSpec> Releases { get; }

    void Configure(IModuleBuilder builder)
    {
    }
}

public interface IModuleBuilder
{
    /// <summary>Subscribes a durable, idempotent consumer (at-least-once delivery, deduplicated by event ID).</summary>
    void Subscribe(string consumer, string eventType, Func<EventEnvelope, IModuleContext, CancellationToken, Task> handler);
}

/// <summary>Services available to module code. Everything is scoped to the event's tenant/environment and a module principal.</summary>
public interface IModuleContext
{
    string TenantId { get; }

    string EnvironmentId { get; }

    IRecordApi Records { get; }
}

/// <summary>Public record API for module code; enforces the same validation, rules, and audit as any caller.</summary>
public interface IRecordApi
{
    Task<JsonElement?> GetAsync(string entity, Guid id, CancellationToken cancellationToken);

    Task UpdateAsync(string entity, Guid id, int expectedRevision, IReadOnlyDictionary<string, JsonElement> changes, string reason, CancellationToken cancellationToken);
}

public static class ModuleReleaseLoader
{
    /// <summary>Loads every embedded resource named "&lt;prefix&gt;&lt;version&gt;/module.json" from an assembly.</summary>
    public static IReadOnlyList<ModuleSpec> FromEmbedded(Assembly assembly, string prefix)
    {
        var releases = new List<ModuleSpec>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith("/module.json", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            releases.Add(JsonSerializer.Deserialize<ModuleSpec>(stream, SpecJson.Options) ?? throw new InvalidOperationException($"Empty release {name}."));
        }

        return releases.OrderBy(r => SemVer.Parse(r.Version)).ToList();
    }
}
