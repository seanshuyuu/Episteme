# 后端实现与验证进展

## 已完成

- 为当前 Identity、OpenIddict、工作空间、成员模型生成 PostgreSQL 初始迁移，并增加用户工作目录设置表。
- 新增 `GET /api/v1/me/workspace-settings` 和 `PUT /api/v1/me/workspace-settings`；只操作认证主体自己的设置。读取要求 `workspaces.read` scope，修改要求 `workspaces.manage` scope，停用用户不可访问。
- 默认工作目录是工作空间内的相对路径；空字符串表示空间根目录。禁止绝对路径、空路径段、`.`、`..`、`.git` 路径段、反斜线、冒号和控制字符。此设置仅记录路径；未来实际访问文件时仍必须独立鉴权、检查符号链接及验证路径边界。
- 设置以 PostgreSQL upsert 保存，首次并发写入不会产生重复行，更新不会覆盖其他偏好；写入时锁定活跃用户行，避免与停用操作交错。每次成功写入同一事务内增加不含路径内容的账号审计，重复设置也记录一次。
- Refit 客户端和 `.http` 示例包含上述两个接口。
- 新增 `GET /api/v1/workspaces?offset=0&limit=25`，返回 `items`、`offset`、`limit`、`hasMore`；`limit` 范围为 1–100。需要 `workspaces.read` scope，且用户必须处于活跃状态。普通用户只看到有活跃成员关系的活跃空间；系统管理员可以看到所有活跃空间。按 slug 和 ID 排序；使用 offset 分页时，空间在分页期间被增删或改名可能导致跨页重复或遗漏。
- 在开发环境提供 `/openapi/v1.json`，列出当前 API；生产环境默认不公开该文档。
- 新增工作空间元数据更新 `PUT /api/v1/workspaces/{id}` 与归档 `POST /api/v1/workspaces/{id}/archive`。须有 `workspaces.manage` scope，且为活跃用户并拥有 Owner 角色或系统管理员角色；两个接口要求客户端提交查询结果中的 `concurrencyStamp` 作为 `expectedVersion`，版本过期返回 409。
- 修改/归档工作空间时，数据库事务对工作空间和用户行加锁，检查当前成员授权与版本后执行更新，并在同一事务写入不含正文的空间审计事件。归档不会物理删除 Git 仓库。
- 新增 `POST /api/v1/workspaces`，创建记录、首位 Owner、审计和隔离的 Git 仓库；需要活跃账号和 `workspaces.manage` scope。新仓库以 `main` 分支和空的初始提交初始化，路径使用服务端生成的 StorageKey。
- 数据库和 Git 不能共享事务：创建先持久化不可见的 `Provisioning` 状态，再持锁幂等初始化仓库并激活空间；初始化失败返回 503 和 `workspaceId`。可通过 `GET /api/v1/workspaces/provisioning` 查看本人的待初始化空间，通过 `POST /api/v1/workspaces/{id}/provision` 重试；重复初始化不会产生额外提交。正常列表不包含待初始化空间，slug 冲突返回 409。
- 部署必须安装 Git，并设置 `WorkspaceStorageRoot` 为**已存在**、由服务进程独占管理的绝对目录；不要把该目录暴露给用户或其他不可信进程。Git 初始化使用 CliWrap 的参数化调用、禁用全局配置和 hooks、超时及退出码检查；拒绝符号链接仓库和非空的未知目录。
- 增加成员管理：`GET /api/v1/workspaces/{id}/members` 分页列出活跃成员；`PUT /api/v1/workspaces/{id}/members/{userId}` 添加、重新启用成员或调整为 Owner、Editor、Reader；`DELETE` 同一路径移除成员。操作要求 `workspaces.manage` scope、活跃账号以及空间 Owner 或系统管理员权限；设置角色的目标用户必须活跃。工作空间锁串行化成员变更，禁止移除或降级最后一名活跃 Owner 成员。Owner 转移可先授予新 Owner，再移除或降级原 Owner。
- 成员变更在同一数据库事务写入审计事件，记录操作者、目标用户、目标角色及事件类型，不记录正文或 token。部署时须应用 `WorkspaceMemberAudit` 迁移；历史审计事件的目标字段为空。
- 新增只读内容接口：`GET /api/v1/workspaces/{id}/directories?path=...` 和 `GET /api/v1/workspaces/{id}/documents?path=...`。须具备 `workspaces.read` scope、活跃账号和活跃成员关系（系统管理员仍需 scope）；在空间和用户锁内鉴权后读取默认分支的 Git 提交树，响应包含快照 commit ID，不读取未提交工作树内容。目录请求不传 path 表示根目录；路径规则与用户工作目录一致；不跟随 Git 符号链接。文档只支持无 NUL 的有效 UTF-8，最大 1 MiB；目录 Git 输出超过 1 MiB 返回 413，暂不支持分页。二进制、符号链接和其他非普通文件返回 422；缺失路径返回 404。
- `GET /api/v1/workspaces/{id}/documents/history` 按路径查询默认分支的提交 ID，需 `workspaces.read` scope 和活跃成员关系（或系统管理员），分页大小为 1–100，offset 上限 10000。历史不追踪重命名前的路径；不存在或没有历史的路径返回空列表。响应含查询时固定的 HEAD；当前使用 offset 分页，分页之间 HEAD 改变可能导致重复或遗漏。
- `GET /api/v1/workspaces/{id}/documents/version` 可以读取默认分支可达的历史提交中文档，权限与历史查询一致；限制、路径和错误规则与当前文档读取相同。
- 文档 `PUT` / `DELETE` 及 `POST /documents/restore` 要求 `workspaces.write` scope 和 Owner/Editor（或系统管理员），调用者必须提供完整的 `expectedHead`。写入与删除产生新提交；恢复只接受当前 HEAD 的祖先提交并产生新提交，不重写历史。空间行锁与 Git `update-ref` 比较旧 HEAD 防止并发覆盖；无效路径、越权、HEAD 冲突和不支持的目标分别返回错误。新文档的父目录必须存在，文本大小上限为 1 MiB。提交作者使用用户 ID 而非正文；未跟踪的 Git 工作树文件和索引不会被修改，API 始终读取 Git tree，不应将该工作树当作最新快照。
- Git 提交和数据库事务不构成分布式事务：如果客户端取消或连接在提交后中断，结果可能不确定，应重新读取 HEAD 和内容再决定是否重试，不可假定失败意味着没有提交。Git 物理仓库须由服务独占，不能由其他进程直接修改。
- 目录创建、删除、移动与文档移动采用相同授权和 HEAD 检查，并生成单一提交。空目录以 `.gitkeep` 记录；删除含其他条目的目录必须显式指定 `recursive: true`。移动目标须不存在且父目录存在，不允许目录移入自身；大于 1 MiB 的目录树枚举被拒绝。
- 空间清单 `GET /api/v1/workspaces/{id}/manifest` 和 `POST /api/v1/workspaces/import`：默认导出兼容旧客户端的版本 1（名称、类型、描述）；显式指定 `formatVersion=2` 包含非敏感设置，版本 3 额外包含 `directories` 目录骨架。导入支持三个版本，须提供新 slug；导出需要读取权限，导入需要 `workspaces.manage`。版本 3 最多 256 个目录（包含隐式父目录），路径总计最多 65536 UTF-8 字节，使用现有路径规则且禁止 `.gitkeep` 目录，未知字段和不支持的版本被拒绝。导出从同一受锁 Git 提交树读取目录，与工作空间元数据一起获取；它不导出文件名、正文、成员、token 或历史，树遍历输出超过 1 MiB 返回 413。导入持久化待初始化骨架后用私有 Git index 创建单一初始提交；中断后可通过 provisioning 重试，已有 HEAD 时校验其树是否匹配，以免重复提交或激活不匹配的仓库。须应用 `WorkspaceDirectorySkeleton` 迁移；MCP 和 Refit 同样支持版本 3。
- `GET` / `PUT /api/v1/workspaces/{id}/configuration` 可读取或替换版本 1 设置；替换要求 `workspaces.manage`、Owner 或管理员和当前 `expectedVersion`，并记录审计。可选字段为 `defaultExtension`、`language`、`skillFormat`、`citationStyle`、`genre`，值限 1–80 个 ASCII 字母、数字及 `.-_+#`；未知字段或版本被拒绝。只存放非敏感元数据，绝不可放入密码、token 或密钥。MCP 的 `set_workspace_configuration` 和清单导出可使用相同设置和版本约束；Refit 客户端已接入。
- 修正 JWT 验证所需的 IdentityModel 传递依赖版本不一致问题；此前混用 8.0.1 和 8.14.0 在验证真实 JWT 时可能抛出 `MissingMethodException`。
- 新增独立 MCP 宿主，使用官方 `ModelContextProtocol.AspNetCore` SDK 的无状态 Streamable HTTP；工具覆盖空间的创建、查询、更新和归档，初始化重试，成员管理、个人工作目录设置、目录和文档操作、历史及恢复、基础清单导入导出。工具只从已认证的请求主体提取用户与 scope，并复用 API 所用的应用服务，不接受调用者用户 ID 参数。工具错误使用 MCP `isError`，Git 写入仍需 `expectedHead`。配置清单的范围仍与上述基础 API 一致。
- MCP 部署需设置 `ConnectionStrings__Episteme`、`WorkspaceStorageRoot`、`Authentication__Authority` 和 `Authentication__Audience`；后者是该 MCP 端点的公开绝对 URL（例如 `https://mcp.example.com/mcp`），也用于 JWT audience 校验及受保护资源元数据。生产环境必须使用 HTTPS；若使用反向代理，代理到宿主也需使用 HTTPS 并保留公开 Host，因为当前未启用转发头处理。仅对 MCP 所需的可信源开放网络访问。每个 HTTP 请求独立验证 token；SDK 发布 OAuth 受保护资源元数据。
- MCP SDK 的传输日志可能包含完整消息，因此宿主默认禁用 `ModelContextProtocol` 日志类别；部署不得使用 provider 专属日志规则覆盖此限制，也不得启用包含 HTTP 请求体的代理、追踪或日志记录。
- 新增独立身份宿主 `Episteme.Auth`：Identity 登录及注册、失败登录锁定、显式用户同意，OpenIddict 授权码 + PKCE S256、refresh token 轮换和标准撤销端点。授权请求必须指定唯一的 `resource`，签发的 JWT audience 绑定该资源，API 和 MCP 的资源值必须不同。开发环境使用开发证书，非开发环境必须配置签名和加密证书。
- 身份宿主需要 `ConnectionStrings__Episteme`、`Authentication__Issuer`、`Authentication__ApiAudience`、`Authentication__McpAudience`；MCP audience 路径必须为 `/mcp`。可配置 `Authentication__Client__Id` 和 `Authentication__Client__RedirectUri` 注册一个 public PKCE 客户端，重启时如果已有客户端配置不匹配会拒绝启动；未配置时不会自动注册客户端。非开发环境还须配置 `Authentication__SigningCertificate__Path`、`Authentication__SigningCertificate__Password`、`Authentication__EncryptionCertificate__Path`、`Authentication__EncryptionCertificate__Password` 和 `Authentication__DataProtection__KeyRingPath`。后者须是已存在的、专供身份服务写入的绝对目录，且自身不能是符号链接；多个实例须共享目录及加密证书，密钥环以加密证书保护。目录及其祖先必须由可信部署管理并备份；旧密钥依赖旧证书，证书轮换需保留旧证书并先制定迁移方案，当前尚不支持自动轮换。机密应由安全配置提供，不能提交或写入日志。API/MCP 的 Authority 和 Audience 须分别与签发者和对应 resource 完全一致。
- 普通 access token 有效期 10 分钟，refresh token 有效期 7 天，复用宽限为零。MCP 的 token 条目校验默认开启，生产环境不可关闭；API 可设置 `Authentication__RequireTokenEntryValidation=true` 启用相同校验。OpenIddict 验证器通过 discovery/JWKS 验证签名，并检查 token、关联授权条目、账号 token 代次和当前系统角色；启用校验的宿主须连接**与身份服务相同**的 OpenIddict PostgreSQL 数据库，生产环境的 authority 和 audience 必须使用 HTTPS。撤销在后续请求生效；已经通过验证、正在执行的请求不会被中断，数据库不可用时请求失败。角色在每次认证时读取，并非与随后的业务事务原子绑定。
- API 的条目校验为兼容外部发行者默认关闭；MCP 仅开发环境可以显式关闭。关闭时采用 JwtBearer 校验，不检查 token 条目，撤销和系统角色变更可能延迟到令牌过期后生效。外部发行者不能假设共用本地 token 表。工作空间访问另检查用户当前状态。
- 账户设置中的 MCP 选项提供个人开关和 30 天 MCP Token 的一次性展示及撤销；工作空间管理中每个空间的设置提供空间开关、个人权限和成员授权。目前令牌列表包含账号的所有访问和刷新令牌，撤销其他令牌可能影响登录。本地 Docker 配置仅将 MCP 绑定到 `127.0.0.1:18084`；其他设备访问需要自行配置安全的 HTTPS 入口。
- 新增 `GET /api/v1/me/tokens` 和 `DELETE /api/v1/me/tokens/{id}`，要求 `tokens.manage` scope 和活跃用户，只返回调用者的 access/refresh token 元数据，不返回 token 值或授权码；不可访问他人的条目。列表按创建时间倒序、ID 排序，offset 上限 10000、每页 1–100；并发签发或清理可能影响跨页结果。撤销已撤销条目可重复执行；撤销 access token 不自动撤销相关 refresh token，需分别撤销。通过该 API 首次撤销会在同一数据库事务写入不含令牌值的账号审计记录。Refit 客户端及 `.http` 示例已接入。已有配置客户端需通过受控管理流程增加 `tokens.manage` 权限，否则配置检查会拒绝身份宿主启动；需应用 `AccountAuditEvents` 迁移。
- 标准 OAuth `/connect/revoke` 保留 OpenIddict 的客户端和令牌校验以及 RFC 7009 未知令牌响应，在令牌行锁和事务中执行首次撤销并写入独立审计。审计记录客户端 ID、可选令牌主体、令牌条目 ID 和时间，不记录令牌值；客户端调用不假定是用户本人发起。重复或未知令牌不产生额外成功审计；须应用 `OAuthRevocationAudit` 迁移。升级 OpenIddict 时须复核替换的内置撤销处理器与本处理器是否仍保持相同协议语义。
- 每次新的 OAuth 交互同意会创建永久授权条目；`GET /api/v1/me/authorizations` 和 `DELETE /api/v1/me/authorizations/{id}` 使用 `tokens.manage` 查询或撤销本人的条目，首次撤销与账号审计在同一事务提交。关联的 refresh token 将无法使用；启用本地条目验证的 API/MCP 会拒绝关联 access token，默认离线 JWT 模式则只能等 token 过期。历史上未关联永久授权的令牌不在此列表中，应重新认证或分别撤销。须应用 `AuthorizationAudit` 迁移。
- `GET /api/v1/admin/users/{id}/tokens` 和 `DELETE /api/v1/admin/users/{id}/tokens/{tokenId}` 供活跃系统管理员管理指定用户的 access/refresh token 元数据，要求 `users.manage` scope 并重新核验数据库管理员身份。撤销前检查令牌所属用户，在同一事务写入包含操作者与目标用户的审计；不可借由目标 ID 操作其他人的令牌。撤销单一 access token 不会自动撤销 refresh token，资源服务的即时生效仍需启用本地条目验证。
- 增加 `/api/v1/admin/users` 下的创建、列表、详情、状态及 Administrator 角色接口，要求 `users.manage` scope、Administrator 角色及数据库中仍活跃的管理员身份。创建账号使用 Identity 密码验证和哈希，只接受 HTTPS 请求；当前未启用转发头，代理到 API 的连接也必须使用 HTTPS，不得记录请求体。变更写入账号审计。不能停用仍为活跃或初始化中空间 Owner 的用户，须先移交其所有权；不能停用或移除最后一名活跃管理员。角色和状态变更在管理员角色行锁下串行化，授予管理员的目标必须活跃。身份宿主可通过受信任的 `Authentication__BootstrapAdministrator__UserId` 为已存在活跃用户引导首位管理员，或通过 `Authentication__BootstrapAdministrator__Email` 和 `Authentication__BootstrapAdministrator__Password` 创建首位管理员；两种方式不能混用，凭据只可由安全配置提供，不得写入文件、日志或仓库。凭据模式仅用于首次引导，已有管理员时会拒绝启动；成功后立即移除配置及引导密码。引导写入审计；首位登录用户自动引导的条件和风险见下文。已有配置客户端也需增加 `users.manage` 权限。
- `PUT /api/v1/admin/users/{id}/password` 提供管理员协助恢复：需要 HTTPS、`users.manage`、当前活跃管理员，并使用 Identity 的重置令牌提供器及密码策略。重置、token 代次变更和账号审计位于同一事务；不返回或记录密码及临时重置令牌。操作可重置已停用账号密码，但不会启用账号。API 宿主的 Identity 重置令牌仅在同一请求内生成并消费，不提供给客户端；应禁止代理和应用日志记录请求体。
- 通过系统管理接口停用账号会轮换 token 代次和 Identity 安全戳；重新启用后旧授权码和 refresh token 不可交换，启用本地条目验证的 API/MCP 也拒绝旧 access token，身份 cookie 在下一请求校验时失效。离线 JWT 验证不检查代次，旧 access token 在过期前仍可能生效；令牌条目自身状态不因代次变更而改成 revoked。代次与业务事务并非原子校验，已开始执行的请求不会中断。该能力需要 `UserTokenGeneration` 迁移；升级前签发的令牌没有代次，升级后的身份服务及本地条目验证会拒绝它们，用户需要重新登录授权。应协调身份服务和资源服务的部署顺序，避免混合版本持续签发旧令牌。
- 密码变更和管理员重置也会更新 Identity 安全戳；授权码、refresh token 和启用本地验证的 access token 绑定该安全戳的 SHA-256 指纹，旧身份 cookie 也在下一请求被校验。旧版签发的令牌没有指纹，升级后须重新登录授权；升级身份服务及 API/MCP 时应协调版本，离线 JWT 验证仍不能即时拒绝旧 access token。
- 身份宿主支持可选 SMTP 邮箱确认、重新发送确认邮件和自助密码重置，使用 Identity 内置令牌、有效期、密码策略及安全戳。邮件服务默认关闭：注册后无需确认即可登录，找回密码相关端点关闭；启用后必须确认邮箱才能登录。`Active` 是管理状态，邮箱确认是独立的登录条件，未确认账号不能登录。已有未确认账号（包括引导或管理员创建的账号）需先通过重新发送确认邮件完成验证，升级时应提前安排管理员恢复途径。默认模板和 Identity 内置邮件正文目前为英文，模板不展示密码。密码重置会轮换安全戳，既有资源令牌的即时失效仍取决于本地条目验证；已签发令牌不会因开启邮箱确认而自动撤销。
- 没有管理员且没有历史管理员授予审计记录时，第一个成功登录的用户自动成为管理员；无邮件注册成功会自动登录，启用邮件时须先确认邮箱并登录。并发登录在管理员角色行锁下串行化，自动授予与审计同一事务提交；已有管理员或历史授予记录不会触发自动授予。公开部署前应先由可信用户完成首次注册并登录，否则任何能注册登录的人都可能成为首位管理员；已有无管理员且无授予审计记录的部署升级时也有同样风险，可使用现有受信任的引导配置先建立管理员。
- 登录、注册、找回密码、重设密码、重发确认和 token POST 以来源 IP 每实例限速合计每分钟 20 次（超限 429），并保留账号密码失败锁定；此内存限速不是跨实例的分布式防滥用措施，未配置可信转发头时代理后的请求可能共享同一 IP。尚无自动密钥轮换方案。`prompt=login` 和 `max_age` 尚不支持重新认证。当前身份宿主仍不应直接对公网或生产用户开放。

### SMTP 邮件配置

在身份宿主的安全配置或密钥管理系统设置以下键，实际凭据不要写入仓库或日志：

| 配置键 | 说明 |
| --- | --- |
| `Authentication__Email__Enabled` | 显式设为 `true` 启用；默认关闭。 |
| `Authentication__Email__Host` | SMTP 服务器主机名或 IP。 |
| `Authentication__Email__Port` | 端口，默认 `587`。 |
| `Authentication__Email__From` | 发件邮箱地址，须为 SMTP 服务允许使用的地址。 |
| `Authentication__Email__UserName` / `Authentication__Email__Password` | SMTP 用户名与密码，必须同时提供；支持无需认证的受信任中继。 |

发送强制使用 STARTTLS 并验证服务器证书，不支持明文 SMTP 或 465 端口隐式 TLS；邮件服务不可用时操作会失败，不会伪装成发送成功。注册时账号可能已保存但邮件发送失败，修复服务后可使用重发确认邮件。启用时 `Authentication:Issuer` 必须是 HTTPS 根地址，身份请求必须直连 HTTPS 且 Host 和端口匹配该地址，以防邮件链接被 Host 头篡改；当前未信任反向代理头，代理需向应用传递正确的 HTTPS 和 Host。生产部署还必须配置现有证书和持久化、受保护且跨实例共享的 Data Protection 密钥环，否则确认和重置链接可能失效。请自行验证真实 SMTP 投递、证书与邮箱提供商策略；自动化测试没有向外部邮箱投递。

## 数据库迁移

以下命令使用已安装的 `dotnet-ef` 10.0.0。首先通过安全配置方式设置 `ConnectionStrings__Episteme`；不要将真实连接字符串或密码提交到仓库。迁移应先在备份和测试环境验证，再由具备适当权限的部署流程执行。

```shell
dotnet ef database update \
  --project src/server/Episteme.Infrastructure/Episteme.Infrastructure.csproj \
  --startup-project src/server/Episteme.Infrastructure/Episteme.Infrastructure.csproj
```

应用启动时不会自动迁移数据库。API 和 MCP 不签发令牌，可使用独立身份宿主或兼容的外部 OIDC 签发者。

## 测试

```shell
dotnet test src/Episteme.sln
dotnet build src/Episteme.sln --no-restore
dotnet format src/Episteme.sln --verify-no-changes --no-restore
```

数据库集成测试需要独立的、已应用上述迁移的 PostgreSQL 测试数据库，通过 `EPISTEME_TEST_POSTGRES` 提供连接字符串。未设置时，集成测试明确标记为跳过；不要使用生产数据库。设置后运行 `dotnet test src/Episteme.sln`。测试只创建并清理自己生成的用户和设置记录。

之前在隔离 PostgreSQL 14 实例上成功应用两项迁移，设置接口 GET 和 PUT 的未认证请求均返回 HTTP 401。

最近一次在隔离 PostgreSQL 17 和真实 Git 上运行：104 项测试全部通过（33 领域、35 应用、36 集成），无跳过；解决方案构建 0 警告、0 错误，格式检查通过，EF 模型无待生成迁移。身份测试覆盖首位管理员凭据引导及弱密码拒绝、他人令牌管理的授权和审计、授权会话跨用户隔离与撤销，以及停用、重新启用、密码变更和管理员重置后的旧令牌及身份 cookie 失效；API 和 MCP 分别使用真实 OpenIddict 签发令牌验证。还覆盖版本 3 清单的目录骨架、恢复幂等性、OAuth 撤销审计失败回滚，以及邮箱确认、重发、密码重置、无效重放、Host 校验、发送失败后的重发恢复和 SMTP 禁止无 STARTTLS 投递。邮件投递通过测试替身验证，尚未验证真实 SMTP 服务。上述离线 JWT 和生产部署限制仍然适用；管理员密码重置的审计失败回滚尚未单独做故障注入测试。

在隔离 PostgreSQL 17 和真实 Git 上，本轮共 87 项测试通过（24 领域、34 应用、29 集成），无跳过；格式检查通过，EF Core 模型无待生成迁移。身份集成测试覆盖真实注册和密码哈希、登录锁定、用户同意及防 CSRF、PKCE、真实 OpenIddict 签发、OIDC discovery/JWKS、API/MCP audience 隔离、scope 拒绝、账号停用、refresh 轮换及撤销、code/refresh 重放；也覆盖本人令牌分页、跨用户拒绝、API/MCP 启用 token 条目校验后的撤销生效、管理员并发保护、账号变更审计、生产自助注册拦截、身份限速及多实例共享加密密钥环。另验证默认 JwtBearer 模式下已撤销 access token 在过期前仍可使用。

本轮在隔离 PostgreSQL 和 Git 上完成清单往返及权限测试，全部 76 项测试通过（24 领域、30 应用、22 集成），无跳过；构建 0 警告、0 错误，格式检查通过。测试生成的 JWT 仅用于验证资源服务器，不能代替身份签发服务。

MCP 验证：隔离 PostgreSQL、真实 Git 仓库和真实 JwtBearer 验证下，Streamable HTTP 集成测试覆盖受保护资源发现、匿名及错误 audience 拒绝、工具发现、创建空间、文档读写与历史、旧 HEAD 冲突、路径穿越、清单未知字段、scope 和成员权限、工作目录设置。全部 77 项测试通过（24 领域、30 应用、23 集成），无跳过；构建、格式和 EF 模型检查通过。测试令牌由测试代码签发，不代表已实现 OpenIddict 签发链路。

前一轮验证：解决方案构建通过（0 警告、0 错误）；64 项测试全部通过（24 领域、23 应用、17 集成），无跳过；格式检查通过；EF Core 无待生成模型迁移。

成员管理验证：在隔离 PostgreSQL 实例应用全部迁移后，65 项测试通过（24 领域、23 应用、18 集成），无跳过；覆盖成员管理 HTTP 授权、角色变更、审计和并发移除最后 Owner。构建和格式检查通过，EF 模型无待生成迁移。HTTP 测试使用测试认证方案，并非真实 JWT。

本轮内容读取验证：在独立 PostgreSQL 和实际 Git 仓库上，72 项测试通过（24 领域、29 应用、19 集成），无跳过；覆盖提交快照、角色及 scope、路径穿越、符号链接、文档大小边界、目录输出上限和历史分页。解决方案构建通过（0 警告、0 错误），格式检查通过，EF 模型无待生成迁移。HTTP 使用测试认证方案，仍未验证真实 JWT。

文档写入验证：独立 PostgreSQL 和 Git 上 74 项测试通过（24 领域、30 应用、20 集成），无跳过；验证创建、更新、删除、历史读取与恢复、并发 HEAD 冲突、Reader/Editor 与 scope、路径及符号链接限制。构建 0 警告 0 错误、格式检查通过、EF 无待生成迁移。认证仍为测试方案，并非真实 JWT。

目录及移动操作验证：在隔离 PostgreSQL 和真实 Git 仓库上，74 项测试通过（无跳过），覆盖创建目录、移动文档和目录、非递归删除冲突及递归删除；构建和格式检查通过。认证仍使用测试方案。

## 仍未完成

真实 SMTP 服务投递验证、其他管理操作的审计，以及上面列出的身份生产加固和部署流程仍需完成。即时 token 条目验证需显式开启且依赖共用数据库。现阶段不能作为完整生产后端交付。前端浏览器和文档阅读界面按用户要求暂不开发。
