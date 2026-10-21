using TeamsGateway.Gateway;

var builder = WebApplication.CreateBuilder(args);

// Aspire ServiceDefaults：OpenTelemetry、health check、HTTP resilience、service discovery
builder.AddServiceDefaults();
builder.AddApplicationServices();

var app = builder.Build();

await app.UseApplicationAsync();

app.Run();

// 讓 functional test 的 WebApplicationFactory<Program> 參照得到
public partial class Program;
