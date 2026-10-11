using Esmbler.Hosting;
using Esmbler.Kernel.Infrastructure.Events;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddEsmblerPlatform(builder.Configuration);
builder.Services.AddHostedService<OutboxDispatcherService>();
builder.Services.AddHostedService<JobRunnerService>();
builder.Services.AddHostedService<Esmbler.Kernel.Infrastructure.Services.AuditMaintenanceService>();
var host = builder.Build();
await host.RunAsync();
