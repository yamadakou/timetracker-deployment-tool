# MCP サーバー（HTTP）のコンテナイメージ（詳細設計 Phase 4a の 7）。stdio のサーバーと CLI は単一ファイルの EXE で配布する
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# 依存関係の復元を、ソースの変更と別のレイヤーにする
COPY global.json ./
COPY src/Timetracker.Common/Timetracker.Common.csproj src/Timetracker.Common/
COPY src/Timetracker.Domain/Timetracker.Domain.csproj src/Timetracker.Domain/
COPY src/Timetracker.Application/Timetracker.Application.csproj src/Timetracker.Application/
COPY src/Timetracker.Infrastructure.Azure/Timetracker.Infrastructure.Azure.csproj src/Timetracker.Infrastructure.Azure/
COPY src/Timetracker.Controller.Mcp/Timetracker.Controller.Mcp.csproj src/Timetracker.Controller.Mcp/
RUN dotnet restore src/Timetracker.Controller.Mcp/Timetracker.Controller.Mcp.csproj
COPY src/ src/
RUN dotnet publish src/Timetracker.Controller.Mcp/Timetracker.Controller.Mcp.csproj --configuration Release --no-restore \
    -p:UseAppHost=false -p:DebugType=None --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# 非 root で動かす（イメージに含まれる app ユーザー）。待ち受けは 8080（ASPNETCORE_HTTP_PORTS の既定）
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "timetracker-mcp.dll", "--transport", "http"]
