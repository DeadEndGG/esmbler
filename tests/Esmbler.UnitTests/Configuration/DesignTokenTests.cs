using System.Text.RegularExpressions;

namespace Esmbler.UnitTests.Configuration;

/// <summary>
/// REQ-DS-001, design lint DL-007: every token a registry element lists, and every choice option tone in the
/// first-party module releases, names a semantic token defined in the web token file (`--esm-*`).
/// </summary>
public partial class DesignTokenTests
{
    private static readonly HashSet<string> Defined = LoadDefinedTokens();

    [Fact]
    public void Registry_elements_reference_only_defined_tokens()
    {
        var missing = CompilerFixture.Registry.All
            .SelectMany(e => e.Tokens.Select(t => (Element: e.Id, Token: t)))
            .Where(x => !Defined.Contains(ToProperty(x.Token)))
            .Select(x => $"{x.Element}: {x.Token}")
            .ToList();
        Assert.Empty(missing);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.1.0")]
    [InlineData("1.2.0")]
    [InlineData("1.3.0")]
    [InlineData("1.4.0")]
    [InlineData("1.5.0")]
    [InlineData("1.6.0")]
    [InlineData("1.7.0")]
    [InlineData("1.8.0")]
    [InlineData("1.9.0")]
    public void Choice_option_tones_are_accent_tokens(string version)
    {
        var bad = new[] { CompilerFixture.PlatformSpec, CompilerFixture.Tickets(version) }
            .SelectMany(m => m.ChoiceLists)
            .SelectMany(c => c.Options.Where(o => o.Tone is not null).Select(o => (List: c.Id, o.Id, Tone: o.Tone!)))
            .Where(x => !x.Tone.StartsWith("accent.", StringComparison.Ordinal) || !Defined.Contains(ToProperty(x.Tone)))
            .Select(x => $"{x.List}/{x.Id}: {x.Tone}")
            .ToList();
        Assert.Empty(bad);
    }

    [Fact]
    public void Token_names_follow_the_naming_rule()
    {
        Assert.All(CompilerFixture.Registry.All.SelectMany(e => e.Tokens), t => Assert.Matches("^[a-z]+(\\.[a-z0-9-]+)+$", t));
    }

    private static string ToProperty(string token) => token.Replace('.', '-');

    private static HashSet<string> LoadDefinedTokens()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Esmbler.slnx")))
        {
            dir = dir.Parent;
        }

        var css = File.ReadAllText(Path.Combine(dir!.FullName, "apps", "web", "src", "styles", "tokens.css"));
        return [.. CustomProperty().Matches(css).Select(m => m.Groups[1].Value)];
    }

    [GeneratedRegex("--esm-([a-z0-9-]+)\\s*:")]
    private static partial Regex CustomProperty();
}
