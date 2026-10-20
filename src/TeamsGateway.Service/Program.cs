using TeamsGateway.Service.Data;

var builder = WebApplication.CreateBuilder(args);

// Aspire ServiceDefaults：OpenTelemetry、health check、HTTP resilience、service discovery
builder.AddServiceDefaults();
builder.AddGatewayDatabase();

var app = builder.Build();

await app.Services.MigrateGatewayDatabaseAsync();

app.MapDefaultEndpoints();

app.MapGet("/", () => "teams-gateway");

app.Run();
