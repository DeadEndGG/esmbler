using Esmbler.Kernel.Configuration;

namespace Esmbler.UnitTests.Configuration;

public class TicketsReleaseTests
{
    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.1.0")]
    [InlineData("1.2.0")]
    public void Release_compiles_without_errors(string version)
    {
        var result = CompilerFixture.Compile(version);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors.Select(e => $"{e.Code} {e.Target}: {e.Message}")));
    }

    [Fact]
    public void Registry_loads_gate1_catalog()
    {
        Assert.True(CompilerFixture.Registry.All.Count >= 60);
        Assert.NotNull(CompilerFixture.Registry.Find("layout.card"));
        Assert.NotNull(CompilerFixture.Registry.FieldType("short_text"));
    }

    [Fact]
    public void Content_hash_is_deterministic()
    {
        var a = CompilerFixture.Compile("1.0.0");
        var b = CompilerFixture.Compile("1.0.0");
        Assert.Equal(a.Manifest!.ContentHash, b.Manifest!.ContentHash);
        Assert.NotEqual(a.Manifest.ContentHash, CompilerFixture.Compile("1.1.0").Manifest!.ContentHash);
    }
}
