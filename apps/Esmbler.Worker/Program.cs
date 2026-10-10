using Esmbler.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddEsmblerPlatform(builder.Configuration);
var host = builder.Build();
await host.RunAsync();
