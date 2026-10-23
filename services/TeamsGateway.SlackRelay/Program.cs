using TeamsGateway.SlackRelay;

var builder = Host.CreateApplicationBuilder(args);

// 部署專屬設定（Routes 等不進 repo 的對照）；gitignored，可選
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.AddServiceDefaults();
builder.AddRelayServices();

var host = builder.Build();
host.Run();
