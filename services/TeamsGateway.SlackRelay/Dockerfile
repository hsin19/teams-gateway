# build context = repo 根目錄（monorepo，要 restore 共用專案）
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Packages.props Directory.Build.props ./
COPY services/TeamsGateway.SlackRelay/TeamsGateway.SlackRelay.csproj services/TeamsGateway.SlackRelay/
COPY packages/TeamsGateway.ServiceDefaults/TeamsGateway.ServiceDefaults.csproj packages/TeamsGateway.ServiceDefaults/
COPY packages/TeamsGateway.Contracts/TeamsGateway.Contracts.csproj packages/TeamsGateway.Contracts/
RUN dotnet restore services/TeamsGateway.SlackRelay/TeamsGateway.SlackRelay.csproj

COPY packages/ packages/
COPY services/TeamsGateway.SlackRelay/ services/TeamsGateway.SlackRelay/
RUN dotnet publish services/TeamsGateway.SlackRelay/TeamsGateway.SlackRelay.csproj \
    -c Release -o /app --no-restore -p:UseAppHost=false

# 參照 ServiceDefaults（FrameworkReference AspNetCore.App），要 aspnet runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
# Routes（appsettings.Local.json）不烤進 image：runtime 掛檔進來（見 compose）
ENTRYPOINT ["dotnet", "TeamsGateway.SlackRelay.dll"]
