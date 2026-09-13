using Microsoft.Extensions.Options;
using SyncAgent;
using SyncAgent.Api;
using SyncAgent.Configuration;

var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddOptions<SyncAgentOptions>()
    .Bind(builder.Configuration.GetSection(SyncAgentOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<SyncAgentOptions>, SyncAgentOptionsValidator>();

builder.Services.AddHttpClient<ISyncPlatformApiClient, SyncPlatformApiClient>((sp, client) =>
    {
        var options = sp.GetRequiredService<IOptions<SyncAgentOptions>>().Value;
        client.BaseAddress = new Uri(options.ApiBaseUrl);
        client.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
        client.Timeout = TimeSpan.FromSeconds(30);
    })
    .AddStandardResilienceHandler();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
