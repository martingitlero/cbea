using GameBackend.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Reward configuration is server-owned and comes from app settings, never from
// the request body. See infra/main.bicep for where these are declared.
builder.Services.AddSingleton(DailyRewardOptions.FromEnvironment());

builder.Services.AddSingleton<ISystemClock, SystemClock>();

// Singleton, not scoped: these fakes ARE the persistence tier for this stage.
// A per-invocation lifetime would reset the ledger between calls and defeat the
// idempotency guarantee the tests assert.
builder.Services.AddSingleton<IDailyRewardStateStore, InMemoryDailyRewardStateStore>();
builder.Services.AddSingleton<ICurrencyWallet, InMemoryCurrencyWallet>();

builder.Services.AddScoped<IDailyRewardService, DailyRewardService>();

builder.Build().Run();
