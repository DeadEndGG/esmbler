// Evidence probe for ADR 0006: does wasmtime-dotnet support the Component Model?
// Usage: dotnet run -- <path-to-component.wasm>
using System.Reflection;
using Wasmtime;
using Module = Wasmtime.Module;

var path = args.Length > 0 ? args[0] : "../packages/ticket-validator/component.wasm";
var asm = typeof(Engine).Assembly;
Console.WriteLine($"Wasmtime.Dotnet assembly: {asm.GetName().Version} ({asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion})");

// 1) Public API surface: is there any Component / component Linker / WIT type?
var componentTypes = asm.GetExportedTypes()
    .Where(t => t.Name.Contains("Component", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("Wit", StringComparison.Ordinal))
    .Select(t => t.FullName)
    .ToList();
Console.WriteLine($"Public types containing 'Component'/'Wit': {(componentTypes.Count == 0 ? "<none>" : string.Join(", ", componentTypes))}");
var cmMembers = asm.GetExportedTypes()
    .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
        .Where(m => m.Name.Contains("Component", StringComparison.OrdinalIgnoreCase))
        .Select(m => $"{t.Name}.{m.Name}"))
    .Distinct()
    .ToList();
Console.WriteLine($"Public members containing 'Component': {string.Join(", ", cmMembers)}");

// 2) Try to load the component through the only loading API that exists (core Module).
using var config = new Config().WithComponentModel(true);
using var engine = new Engine(config);
try
{
    using var module = Module.FromFile(engine, path);
    Console.WriteLine($"Loaded as core module?! exports={module.Exports.Count}");
}
catch (Exception ex)
{
    Console.WriteLine($"Module.FromFile(component) failed as expected: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
}

// 3) Core modules (non-component) do work, for comparison.
using var core = Module.FromText(engine, "m", "(module (func (export \"add\") (param i32 i32) (result i32) local.get 0 local.get 1 i32.add))");
using var linker = new Linker(engine);
using var store = new Store(engine);
var add = linker.Instantiate(store, core).GetFunction<int, int, int>("add")!;
Console.WriteLine($"Core module call add(2,3) = {add(2, 3)}");
