using Esmbler.Kernel.Configuration;
using Esmbler.Modules.Tickets;
using Esmbler.Sdk.Overlay;
using Esmbler.Sdk.Registry;
using Esmbler.Sdk.Specs;

namespace Esmbler.UnitTests.Configuration;

internal static class CompilerFixture
{
    public static readonly ElementRegistry Registry = ElementRegistry.LoadBuiltIn();

    public static readonly ConfigCompiler Compiler = new(new ConfigValidator(Registry, new JsonSchemaPropsValidator()));

    public static ModuleSpec Tickets(string version) => new TicketsModule().Releases.Single(r => r.Version == version);

    public static ModuleSpec PlatformSpec => new PlatformModule().Releases[0];

    public static CompileResult Compile(string ticketsVersion, params OverlayOp[] ops) =>
        Compiler.Compile([PlatformSpec, Tickets(ticketsVersion)], ops);
}
