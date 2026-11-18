# Episteme 项目 Agent 指南

你正在协助重新开发 Episteme，一个前后端分离的文档与工作空间管理软件。本文是供后续 AI Agent 使用的项目级工作约定；在此文件被显式提供给 Agent 或由工作区指令加载之前，不假定它会被工具自动发现。

## 当前状态与先决条件

- 用户已确认当前设计，后端正在开发；设计文件在 `design/`。
- 前端技术方向已确认采用 Blazor，当前先进行业务原型设计与评审；未经明确要求，不在原型阶段创建 Razor 页面。
- 用户确认后，所有应用代码都放在 `src/`；设计文档放在 `design/`。
- 开始任何改动前检查工作区现状，保留用户已有的未提交修改；不要清理或覆盖与任务无关的文件。

## 技术方向

- ASP.NET Core 10 Web API、EF Core 10、PostgreSQL/Npgsql。
- ASP.NET Core Identity 管理用户和系统角色；OpenIddict 管理标准 OAuth/OIDC token 生命周期。
- Refit 仅用于 .NET API 客户端；MCP 使用官方 C# SDK，并复用应用用例。
- Blazor UI 优先评估成熟组件库（建议 MudBlazor），支持用户自定义主题与外观配置；普通用户和管理员原型分别见 `design/prototype.html`、`design/prototype-admin.html`，组件方案见 `design/frontend-design.md`。
- 每个工作空间独立 Git 仓库；Git 是目录、文件正文和版本历史的事实来源，数据库保存身份、授权、工作空间元数据及必要审计。
- 优先采用成熟库和平台内建功能，不手写密码/token、Git 协议、MCP 协议或 ORM 基础能力。

## 架构与安全约束

- 遵循 `design/architecture.md` 的依赖方向和 `design/domain-model.md` 的领域边界；EF Core、Git、文件系统细节只能在 Infrastructure 层。
- Web API 和 MCP 的资源授权必须一致。每次空间访问都按认证主体、OAuth scope、空间成员角色检查权限。
- 文件操作只能使用经过规范化的工作空间内相对路径；绝不把用户路径直接拼成服务器任意文件系统路径。
- 不返回成功形状来掩盖存储、鉴权或输入错误；使用清晰的错误类型和标准错误响应。
- token、密码、文档正文和密钥不得进入日志或导出的配置文件。
- 文档修改要保留 Git 历史；恢复旧版本应创建新提交，不重写既有历史。

## 工程约定

- 先查现有代码和相关测试，再做最小完整改动；遵循仓库已有风格。
- 新增领域规则和授权边界时，补充针对性测试；至少验证受限用户、越权路径、版本冲突和错误返回。
- 使用项目现有格式化、构建、测试工具；报告实际验证结果，不声称未运行的检查已通过。
- 未获用户确认前，不增加未在设计中约定的产品行为、依赖或角色。

## 参考设计

- [总体架构与组件选型](design/architecture.md)
- [业务实体与规则](design/domain-model.md)
- [实体关系图](design/erd.md)
- [待确认事项](design/decisions.md)
