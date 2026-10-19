# MCP サーバー（HTTP）のコンテナイメージ（詳細設計 Phase 4a の 7）。stdio のサーバーと CLI は単一ファイルの EXE で配布する
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# 依存関係の復元を、ソースの変更と別のレイヤーにする
COPY global.json ./
COPY src/TTDeployTool.Common/TTDeployTool.Common.csproj src/TTDeployTool.Common/
COPY src/TTDeployTool.Domain/TTDeployTool.Domain.csproj src/TTDeployTool.Domain/
COPY src/TTDeployTool.Application/TTDeployTool.Application.csproj src/TTDeployTool.Application/
COPY src/TTDeployTool.Infrastructure.Azure/TTDeployTool.Infrastructure.Azure.csproj src/TTDeployTool.Infrastructure.Azure/
COPY src/TTDeployTool.Controller.Mcp/TTDeployTool.Controller.Mcp.csproj src/TTDeployTool.Controller.Mcp/
RUN dotnet restore src/TTDeployTool.Controller.Mcp/TTDeployTool.Controller.Mcp.csproj
COPY src/ src/
# サーバーが報告する版（リリースではタグの版を渡す。ARG は RUN の環境変数になるため、MSBuild の Version と同じ名前にしない）
ARG MCP_VERSION=0.0.0
RUN dotnet publish src/TTDeployTool.Controller.Mcp/TTDeployTool.Controller.Mcp.csproj --configuration Release --no-restore \
    -p:UseAppHost=false -p:DebugType=None -p:Version=$MCP_VERSION --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# 非 root で動かす（イメージに含まれる app ユーザー）。待ち受けは 8080（ASPNETCORE_HTTP_PORTS の既定）
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ttdeploytool-mcp.dll", "--transport", "http"]
