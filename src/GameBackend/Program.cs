using Azure.Monitor.OpenTelemetry.Exporter;
using GameBackend.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
{
    builder.Services.AddOpenTelemetry()
        .UseFunctionsWorkerDefaults()
        .UseAzureMonitorExporter();
}

// Reward configuration is server-owned and comes from app settings, never from
// the request body. See infra/main.bicep for where these are declared.
builder.Services.AddSingleton(DailyRewardOptions.FromEnvironment());

builder.Services.AddSingleton<ISystemClock, SystemClock>();

// Singleton, not scoped: these fakes ARE the persistence tier for this stage.
// A per-invocation lifetime would reset the ledger between calls and silently
// defeat the idempotency guarantee the tests assert. Swapping in a durable
// store (Cosmos / Table Storage) is a registration change only — no call site
// in DailyRewardService needs to move.
builder.Services.AddSingleton<IDailyRewardStateStore, InMemoryDailyRewardStateStore>();
builder.Services.AddSingleton<ICurrencyWallet, InMemoryCurrencyWallet>();

builder.Services.AddScoped<IDailyRewardService, DailyRewardService>();

builder.Build().Run();
