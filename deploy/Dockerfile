FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore is its own layer: NuGet downloads are cached and only rerun when a csproj changes.
COPY src/Episteme.Core/Episteme.Core.csproj ./src/Episteme.Core/
COPY src/Episteme.Server/Episteme.Server.csproj ./src/Episteme.Server/
RUN dotnet restore src/Episteme.Server/Episteme.Server.csproj
COPY src/ ./src/
RUN dotnet publish src/Episteme.Server/Episteme.Server.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_URLS=http://+:8080 \
    EPISTEME_DATA_DIR=/app/data \
    TZ=Asia/Shanghai
RUN mkdir -p /app/data && chown "$APP_UID" /app/data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Episteme.Server.dll"]
