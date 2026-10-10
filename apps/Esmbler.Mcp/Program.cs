using Esmbler.Hosting;

if (await HealthCheckCommand.TryRunAsync(args) is { } exitCode)
{
    return exitCode;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEsmblerPlatform(builder.Configuration);

var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

await app.RunAsync();
return 0;
