using TeamsGateway.Gateway;

var builder = WebApplication.CreateBuilder(args);

// Aspire ServiceDefaults：OpenTelemetry、health check、HTTP resilience、service discovery
builder.AddServiceDefaults();
builder.AddApplicationServices();

var app = builder.Build();

await app.UseApplicationAsync();

app.Run();
