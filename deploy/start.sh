#!/usr/bin/env bash
set -u

HOME=/state/auth DOTNET_CLI_HOME=/state/auth ASPNETCORE_URLS=http://0.0.0.0:18081 \
    dotnet /app/auth/Episteme.Auth.dll --contentRoot /app/auth &
auth=$!
HOME=/state/api DOTNET_CLI_HOME=/state/api ASPNETCORE_URLS=http://0.0.0.0:18082 \
    dotnet /app/api/Episteme.Api.dll --contentRoot /app/api &
api=$!

wait_for_port() {
    for _ in $(seq 1 60); do
        if (echo >"/dev/tcp/127.0.0.1/$1") >/dev/null 2>&1; then
            return 0
        fi
        sleep 1
    done
    return 1
}

wait_for_port 18081 || exit 1
wait_for_port 18082 || exit 1

HOME=/state/api DOTNET_CLI_HOME=/state/api ASPNETCORE_URLS=http://0.0.0.0:18084 \
    Authentication__Audience="${Authentication__McpAudience}" Authentication__RequireTokenEntryValidation=true \
    dotnet /app/mcp/Episteme.McpServer.dll --contentRoot /app/mcp &
mcp=$!
wait_for_port 18084 || exit 1

HOME=/state/web DOTNET_CLI_HOME=/state/web ASPNETCORE_URLS=http://0.0.0.0:18080 \
    dotnet /app/web/Episteme.Web.dll --contentRoot /app/web &
web=$!

stop_children() {
    kill "$web" "$auth" "$api" "$mcp" 2>/dev/null || true
    wait "$web" "$auth" "$api" "$mcp" 2>/dev/null || true
}
trap 'stop_children; exit 0' TERM INT
wait -n "$web" "$auth" "$api" "$mcp"
status=$?
stop_children
exit "$status"
