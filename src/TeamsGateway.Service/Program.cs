using TeamsGateway.Service.Data;
using TeamsGateway.Service.Teams;

var builder = WebApplication.CreateBuilder(args);

// Aspire ServiceDefaults：OpenTelemetry、health check、HTTP resilience、service discovery
builder.AddServiceDefaults();
builder.AddGatewayDatabase();
builder.AddTeams();

var app = builder.Build();

await app.Services.MigrateGatewayDatabaseAsync();

app.MapDefaultEndpoints();
app.MapTeamsAdmin();

app.MapGet("/", () => "teams-gateway");

app.Run();
