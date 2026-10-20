var builder = WebApplication.CreateBuilder(args);

// Aspire ServiceDefaults：OpenTelemetry、health check、HTTP resilience、service discovery
builder.AddServiceDefaults();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/", () => "teams-gateway");

app.Run();
