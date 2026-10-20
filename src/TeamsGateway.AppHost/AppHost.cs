var builder = DistributedApplication.CreateBuilder(args);

// 只在本機開發用：起 gateway 服務並接上 Aspire dashboard（log / trace / metrics）。
// 正式部署只部 gateway 的 container，不部 AppHost。
builder.AddProject<Projects.TeamsGateway_Service>("gateway");

builder.Build().Run();
