using TeamsGateway.SlackRelay;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddRelayServices();

var host = builder.Build();
host.Run();
