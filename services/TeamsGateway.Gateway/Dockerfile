# build context = repo 根目錄（monorepo，要 restore 共用專案）
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先 copy manifest 做 restore，吃 layer cache
COPY global.json Directory.Packages.props Directory.Build.props ./
COPY services/TeamsGateway.Gateway/TeamsGateway.Gateway.csproj services/TeamsGateway.Gateway/
COPY packages/TeamsGateway.ServiceDefaults/TeamsGateway.ServiceDefaults.csproj packages/TeamsGateway.ServiceDefaults/
COPY packages/TeamsGateway.Contracts/TeamsGateway.Contracts.csproj packages/TeamsGateway.Contracts/
RUN dotnet restore services/TeamsGateway.Gateway/TeamsGateway.Gateway.csproj

# copy 原始碼 publish
COPY packages/ packages/
COPY services/TeamsGateway.Gateway/ services/TeamsGateway.Gateway/
RUN dotnet publish services/TeamsGateway.Gateway/TeamsGateway.Gateway.csproj \
    -c Release -o /app --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# healthcheck 用（image 預設沒有 curl）
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app ./

# cookie / DataProtection key / SQLite 都落這，掛 volume 才能跨重啟
# （埠不用設：.NET 8+ 的 aspnet image 預設就聽 8080）
ENV Gateway__DataDir=/data
EXPOSE 8080
VOLUME /data
ENTRYPOINT ["dotnet", "TeamsGateway.Gateway.dll"]
