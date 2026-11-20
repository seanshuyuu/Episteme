FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ ./
RUN dotnet publish clients/Episteme.Web/Episteme.Web.csproj -c Release -o /publish/web \
    && dotnet publish server/Episteme.Auth/Episteme.Auth.csproj -c Release -o /publish/auth \
    && dotnet publish server/Episteme.Api/Episteme.Api.csproj -c Release -o /publish/api \
    && dotnet publish server/Episteme.McpServer/Episteme.McpServer.csproj -c Release -o /publish/mcp \
    && dotnet publish tools/Episteme.Migrate/Episteme.Migrate.csproj -c Release -o /publish/migrate

FROM mcr.microsoft.com/dotnet/sdk:10.0
WORKDIR /app
COPY --from=build /publish/ /app/
COPY deploy/start.sh /app/start.sh
RUN sed -i 's/\r$//' /app/start.sh
