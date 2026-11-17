# Episteme 业务与技术设计

本文档集记录重新开发的业务边界、领域模型、组件选型和目录布局。用户已确认当前方案并同意开始后端开发；实际实现状态见 [后端实现与验证进展](./backend-progress.md)。前端浏览器和文档阅读界面暂不开发。

## 文档索引

- [总体架构与组件选型](./architecture.md)
- [业务实体与规则](./domain-model.md)
- [实体关系图](./erd.md)
- [待确认事项](./decisions.md)
- [后端实现与验证进展](./backend-progress.md)

## 设计基线

1. 后端采用 ASP.NET Core 10 Web API、EF Core 10、PostgreSQL；具体 NuGet 版本在编码时按 .NET 10 的稳定兼容版本锁定。
2. PostgreSQL 保存用户、工作空间、成员授权、用户偏好和审计元数据；文档正文及目录树由每个工作空间的 Git 仓库保存。
3. 所有来自 API 或 MCP 的文件路径都是工作空间内的相对路径，服务端存储根目录由部署配置管理，不接受任意绝对路径。
4. ASP.NET Core Identity 负责用户与系统角色；OpenIddict 负责标准 OAuth 2.1 / OpenID Connect token 生命周期；工作空间内使用 Owner、Editor、Reader 角色。
5. MCP 作为独立宿主暴露受 OAuth 保护的工具，并复用 Application 层用例；Refit 用于 .NET API 客户端，不用于服务端内部调用自身 API。
6. 配置文件导入/导出与文档内容/完整 Git 历史备份是不同能力；本提案默认配置文件不携带正文、密钥、访问令牌或成员授权。

## 开发阶段

按 [总体架构与组件选型](./architecture.md) 的 `src/` 目录结构组织代码；偏离已确认方案的重大设计变更仍需用户确认。
