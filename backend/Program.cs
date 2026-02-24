using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

var useKeyVaultOnStartup = builder.Configuration.GetValue<bool?>("KEY_VAULT_LOAD_ON_STARTUP") ?? false;
var keyVaultUri = builder.Configuration["KEY_VAULT_URI"];
if (useKeyVaultOnStartup && !string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
}

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

builder.Services.AddHttpClient<GoogleTokenValidator>();
builder.Services.AddHttpClient<IOpenAiExpenseParser, OpenAiExpenseParser>();

builder.Build().Run();
