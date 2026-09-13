using Microsoft.Extensions.Options;
using SyncAgent;
using SyncAgent.Configuration;

var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddOptions<SyncAgentOptions>()
    .Bind(builder.Configuration.GetSection(SyncAgentOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<SyncAgentOptions>, SyncAgentOptionsValidator>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
