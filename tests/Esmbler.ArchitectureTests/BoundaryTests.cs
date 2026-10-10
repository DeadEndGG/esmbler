using System.Reflection;
using System.Xml.Linq;
using NetArchTest.Rules;

namespace Esmbler.ArchitectureTests;

/// <summary>
/// REQ-MOD-002 / ADR 0012: module boundaries and dependency direction, enforced in CI.
/// Modules may use only the public SDK, the expression language, their own projects, and other modules' Contracts.
/// </summary>
public class BoundaryTests
{
    private static readonly string[] Internal =
    [
        "Esmbler.Kernel", "Esmbler.Kernel.Infrastructure", "Esmbler.Platform", "Esmbler.Hosting", "Esmbler.Api", "Esmbler.Mcp", "Esmbler.Worker",
    ];

    public static TheoryData<string> ModuleAssemblies()
    {
        var data = new TheoryData<string>();
        foreach (var project in ModuleProjects().Where(p => !p.EndsWith(".Contracts", StringComparison.Ordinal)))
        {
            data.Add(project);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ModuleAssemblies))]
    public void Modules_do_not_depend_on_kernel_platform_or_hosts(string assemblyName)
    {
        var result = Types.InAssembly(Assembly.Load(assemblyName)).ShouldNot().HaveDependencyOnAny(Internal).GetResult();
        Assert.True(result.IsSuccessful, $"{assemblyName} uses internals: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Sdk_depends_on_nothing_internal()
    {
        var result = Types.InAssembly(typeof(Esmbler.Sdk.Specs.SpecCatalog).Assembly).ShouldNot().HaveDependencyOnAny([.. Internal, "Esmbler.Modules"]).GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Kernel_does_not_depend_on_platform_modules_infrastructure_or_hosts()
    {
        var result = Types.InAssembly(typeof(Esmbler.Kernel.Configuration.Composer).Assembly)
            .ShouldNot().HaveDependencyOnAny("Esmbler.Platform", "Esmbler.Modules", "Esmbler.Kernel.Infrastructure", "Esmbler.Hosting", "Esmbler.Api", "Esmbler.Mcp", "Npgsql", "Microsoft.EntityFrameworkCore", "OpenFga")
            .GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Assistant_changes_workspaces_only_through_mcp()
    {
        // REQ-AISAFE-*, REQ-AUTHZ-007: the assistant has no path to the kernel; it holds only an agent token.
        var result = Types.InAssembly(typeof(Esmbler.Assistant.IPlanner).Assembly)
            .ShouldNot().HaveDependencyOnAny([.. Internal, "Esmbler.Modules", "Esmbler.Sdk", "Npgsql", "Microsoft.EntityFrameworkCore", "OpenFga"])
            .GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Only_the_assistant_calls_the_model_provider()
    {
        // ADR 0013: one adapter owns the Anthropic dependency; everything else reaches the model only through it.
        foreach (var assembly in new[] { "Esmbler.Kernel", "Esmbler.Kernel.Infrastructure", "Esmbler.Platform", "Esmbler.Hosting", "Esmbler.Mcp", "Esmbler.Sdk" })
        {
            var result = Types.InAssembly(Assembly.Load(assembly)).ShouldNot().HaveDependencyOn("Anthropic").GetResult();
            Assert.True(result.IsSuccessful, $"{assembly}: {string.Join(", ", result.FailingTypeNames ?? [])}");
        }
    }

    [Fact]
    public void Platform_does_not_depend_on_modules_or_infrastructure()
    {
        var result = Types.InAssembly(Assembly.Load("Esmbler.Platform"))
            .ShouldNot().HaveDependencyOnAny("Esmbler.Modules", "Esmbler.Kernel.Infrastructure", "Esmbler.Hosting", "Npgsql", "OpenFga")
            .GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Module_project_references_are_restricted_to_public_projects()
    {
        var violations = new List<string>();
        foreach (var csproj in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "modules"), "*.csproj", SearchOption.AllDirectories))
        {
            var module = Path.GetFileNameWithoutExtension(csproj);
            var family = module.EndsWith(".Contracts", StringComparison.Ordinal) ? module[..^".Contracts".Length] : module;
            foreach (var reference in XDocument.Load(csproj).Descendants("ProjectReference").Select(r => Path.GetFileNameWithoutExtension(r.Attribute("Include")!.Value.Replace('\\', '/'))))
            {
                var allowed = reference is "Esmbler.Sdk" or "Esmbler.Expressions"
                    || reference == family || reference == family + ".Contracts"
                    || (reference.StartsWith("Esmbler.Modules.", StringComparison.Ordinal) && reference.EndsWith(".Contracts", StringComparison.Ordinal));
                if (!allowed)
                {
                    violations.Add($"{module} → {reference}");
                }
            }

            if (XDocument.Load(csproj).Descendants("PackageReference").Any())
            {
                violations.Add($"{module} has package references; modules use only the SDK");
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Platform_code_does_not_hard_code_business_module_ids()
    {
        // ADR 0012: the kernel, platform services and MCP server know no business module. Module IDs belong in specs,
        // module Contracts, or the composition root (Esmbler.Hosting). Comments and "e.g." text inside descriptions are fine.
        string[] roots = ["packages/Esmbler.Kernel", "packages/Esmbler.Kernel.Infrastructure", "packages/Esmbler.Platform", "apps/Esmbler.Mcp"];
        var offenders = new List<string>();
        foreach (var root in roots)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), root), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
                {
                    continue;
                }

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].TrimStart();
                    if (!line.StartsWith("//", StringComparison.Ordinal) && line.Contains("\"core.tickets", StringComparison.Ordinal))
                    {
                        offenders.Add($"{relative}:{i + 1}");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0, "Business module IDs in platform code: " + string.Join(", ", offenders));
    }

    private static IEnumerable<string> ModuleProjects() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "modules"), "*.csproj", SearchOption.AllDirectories).Select(Path.GetFileNameWithoutExtension)!;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Esmbler.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
