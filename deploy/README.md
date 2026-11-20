# Docker 本机试用部署

此配置仅用于本机访问，使用 Development、HTTP 和开发证书，不适合公网或生产环境。邮件服务未启用。使用原生 WSL Docker 时，请从 WSL 执行命令，并使用 WSL 数据目录路径。

访问 <http://localhost:18080/>。尚无管理员和历史管理员授权记录时，第一个成功登录的用户将成为系统管理员；请先由可信用户注册并登录。未启用邮件时账号注册后自动启用。

## 当前部署

持久化目录为 `E:\Documents\DockerDocs\episteme`：

- `database`：PostgreSQL 数据。
- `workspaces`：工作空间及 Git 数据。
- `web-state`、`auth-state`、`api-state`：各服务的 Data Protection 密钥及其他用户状态。
- `deployment.env`：数据目录及数据库密码。

密钥和密码未加密存储，请限制该目录的 Windows 访问权限，不要提交、分享或覆盖 `deployment.env`。Docker 自身管理的镜像、容器可写层及容器日志仍使用 Docker Desktop 的存储位置，并非 E 盘绑定目录。

在仓库根目录执行以下 PowerShell 命令。原生 WSL Docker 必须使用 WSL 路径覆盖环境文件中的 Windows 数据目录；更新代码后先构建，再启动，迁移会在 Web 服务启动前自动执行：

```powershell
wsl.exe --exec env EPISTEME_DATA_ROOT=/mnt/e/Documents/DockerDocs/episteme docker compose --env-file /mnt/e/Documents/DockerDocs/episteme/deployment.env -f /mnt/f/Code/Episteme/deploy/compose.yaml build migrate
wsl.exe --exec env EPISTEME_DATA_ROOT=/mnt/e/Documents/DockerDocs/episteme docker compose --env-file /mnt/e/Documents/DockerDocs/episteme/deployment.env -f /mnt/f/Code/Episteme/deploy/compose.yaml up -d --no-build
```

查看状态和日志：

```powershell
wsl.exe --exec env EPISTEME_DATA_ROOT=/mnt/e/Documents/DockerDocs/episteme docker compose --env-file /mnt/e/Documents/DockerDocs/episteme/deployment.env -f /mnt/f/Code/Episteme/deploy/compose.yaml ps -a
wsl.exe --exec env EPISTEME_DATA_ROOT=/mnt/e/Documents/DockerDocs/episteme docker compose --env-file /mnt/e/Documents/DockerDocs/episteme/deployment.env -f /mnt/f/Code/Episteme/deploy/compose.yaml logs --tail 100
```

停止和重新启动：

```powershell
wsl.exe --exec env EPISTEME_DATA_ROOT=/mnt/e/Documents/DockerDocs/episteme docker compose --env-file /mnt/e/Documents/DockerDocs/episteme/deployment.env -f /mnt/f/Code/Episteme/deploy/compose.yaml stop
wsl.exe --exec env EPISTEME_DATA_ROOT=/mnt/e/Documents/DockerDocs/episteme docker compose --env-file /mnt/e/Documents/DockerDocs/episteme/deployment.env -f /mnt/f/Code/Episteme/deploy/compose.yaml up -d --no-build
```

Web、认证、API 和 MCP 在同一个容器内运行，任一进程退出时容器会重启。数据库不对宿主机发布端口。MCP 仅绑定本机 `127.0.0.1:18084`，地址为 `http://localhost:18084/mcp`，不能直接从其他设备访问。

登录后在右上角账户菜单选择“设置”→“MCP 设置”，开启个人开关并管理令牌。在“工作空间管理”中打开对应空间卡片的“设置”→“MCP 设置”，由空间所有者开启空间并配置权限；共享成员也可在空间设置中选择自己的权限，但须先获得所有者的逐成员授权。默认不允许访问。生成的 MCP Token 有效期 30 天，只在生成时显示一次，可复制并撤销。当前令牌列表也包含登录所用的访问和刷新令牌，撤销前请确认，以免影响登录。撤销的 MCP Token 在后续请求立即失效；已开始的请求不会被中断。

在支持 Streamable HTTP 的 MCP 客户端中，将服务 URL 设为 `http://localhost:18084/mcp`，并设置 HTTP 请求头 `Authorization: Bearer <MCP Token>`。Token 是凭据，不要写入仓库、日志或共享配置。

本机 HTTP 模式只能用于可信本机。向其他设备开放前，需自行配置 HTTPS、可信的认证地址和对应 MCP audience，保护令牌并限制访问来源；不能把本机 HTTP 端口直接暴露给公网。

## 备份

先停止部署，再一起备份整个 `E:\Documents\DockerDocs\episteme` 目录，包括 `deployment.env` 和所有状态目录；确认备份完成后重新启动。数据库与工作空间必须保持同一时间点，勿在 PostgreSQL 运行时直接复制其数据目录。恢复前停止服务，使用同一份备份恢复全部数据及配置。妥善保护备份中的密码和密钥。

面向其他设备或正式使用前，需要独立设计 HTTPS、正式签名证书、邮件、访问控制、备份恢复和运维监控；不要直接开放这些 HTTP 端口。
