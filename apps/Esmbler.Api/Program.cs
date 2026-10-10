using System.Text.Json.Serialization;
using Esmbler.Api.Endpoints;
using Esmbler.Hosting;
using Esmbler.Hosting.Auth;
using Esmbler.Kernel.Infrastructure.Persistence;
using Esmbler.Kernel.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

if (await HealthCheckCommand.TryRunAsync(args) is { } exitCode)
{
    return exitCode;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEsmblerPlatform(builder.Configuration);
builder.Services.AddEsmblerAuth(Audiences.Api);
builder.Services.AddExceptionHandler<KernelExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddSignalR();
builder.Services.AddHostedService<DraftNotificationListener>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.SnakeCaseLower));
});

var app = builder.Build();
var options = app.Services.GetRequiredService<PlatformOptions>();

if (args.Contains("migrate") || options.MigrateOnStartup)
{
    var migrator = options.Database.Migrator ?? throw new InvalidOperationException("Esmbler:Database:Migrator is not configured.");
    await app.Services.GetRequiredService<MigrationRunner>().RunAsync(migrator, CancellationToken.None);
    if (args.Contains("migrate"))
    {
        return 0;
    }
}

if (args.Contains("seed") || options.SeedDemoData)
{
    var created = await app.Services.GetRequiredService<ProvisioningService>().EnsureDemoTenantsAsync(CancellationToken.None);
    app.Logger.LogInformation("Demo tenants: {Outcome}", created ? "provisioned acme and globex" : "already present");
    if (args.Contains("seed"))
    {
        return 0;
    }
}

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi("/openapi/{documentName}.json");
app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/health/ready", async (Database db, CancellationToken ct) =>
{
    await db.AnonymousAsync(async ctx =>
    {
        await using var cmd = ctx.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT 1";
        return await cmd.ExecuteScalarAsync(ct);
    }, ct);
    return Results.Ok(new { status = "ready" });
}).AllowAnonymous();

app.MapAuth();
app.MapAppAndConfig();
app.MapRecords();
app.MapBuilder();
app.MapHub<LiveHub>("/hubs/live");

await app.RunAsync();
return 0;

/// <summary>Entry point marker for WebApplicationFactory in integration tests.</summary>
public partial class Program;
