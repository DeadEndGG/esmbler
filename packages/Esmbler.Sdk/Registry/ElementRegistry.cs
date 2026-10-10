using System.Reflection;
using System.Text.Json;
using Esmbler.Sdk.Specs;

namespace Esmbler.Sdk.Registry;

/// <summary>Registry + Extension Point pattern: elements are registered, never hard-wired.</summary>
public interface IElementRegistry
{
    IReadOnlyCollection<ElementDefinition> All { get; }

    ElementDefinition? Find(string id);

    /// <summary>Registry element describing a field type, e.g. short_text → field.short_text.</summary>
    ElementDefinition? FieldType(string fieldType);
}

public sealed class ElementRegistry : IElementRegistry
{
    private readonly Dictionary<string, ElementDefinition> _elements;

    public ElementRegistry(IEnumerable<ElementDefinition> elements)
    {
        _elements = elements.ToDictionary(e => e.Id, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<ElementDefinition> All => _elements.Values;

    public ElementDefinition? Find(string id) => _elements.GetValueOrDefault(id);

    public ElementDefinition? FieldType(string fieldType) => Find("field." + fieldType);

    /// <summary>Loads the built-in Gate 1 catalog embedded in this assembly.</summary>
    public static ElementRegistry LoadBuiltIn()
    {
        var assembly = typeof(ElementRegistry).Assembly;
        var elements = new List<ElementDefinition>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("registry/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var element = JsonSerializer.Deserialize<ElementDefinition>(stream, SpecJson.Options)
                ?? throw new InvalidOperationException($"Empty element definition {name}.");
            elements.Add(element);
        }

        return new ElementRegistry(elements);
    }

    public static string ReadEmbeddedSchema(string fileName)
    {
        var assembly = typeof(ElementRegistry).Assembly;
        using var stream = assembly.GetManifestResourceStream("schemas/" + fileName)
            ?? throw new FileNotFoundException($"Schema {fileName} is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static Assembly SdkAssembly => typeof(ElementRegistry).Assembly;
}
