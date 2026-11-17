# 实体关系图

此图表示 PostgreSQL 中的持久化领域实体。Identity 和 OpenIddict 使用其官方 EF Core store 实体/表；文档、目录和版本不在数据库中，见图后的说明。

```mermaid
erDiagram
    APPLICATION_USER {
        uuid Id PK
        string UserName
        string Email
        string Status
        datetime CreatedAt
    }

    IDENTITY_ROLE {
        uuid Id PK
        string Name
    }

    IDENTITY_USER_ROLE {
        uuid UserId PK, FK
        uuid RoleId PK, FK
    }

    USER_WORKSPACE_SETTINGS {
        uuid UserId PK, FK
        string DefaultWorkspaceDirectory
        jsonb Preferences
    }

    WORKSPACE {
        uuid Id PK
        uuid CreatedByUserId FK
        string Name
        string Slug
        string Type
        string Status
        string StorageKey UK
        string DefaultBranch
        int ConfigSchemaVersion
        jsonb Configuration
        uuid ConcurrencyStamp
        datetime CreatedAt
        datetime UpdatedAt
    }

    WORKSPACE_MEMBER {
        uuid WorkspaceId PK, FK
        uuid UserId PK, FK
        string Role
        string Status
        uuid AddedByUserId FK
        datetime JoinedAt
    }

    AUDIT_EVENT {
        uuid Id PK
        uuid ActorUserId FK
        uuid WorkspaceId FK
        string EventType
        jsonb Details
        datetime OccurredAt
    }

    OPENIDDICT_APPLICATION {
        string Id PK
    }

    OPENIDDICT_AUTHORIZATION {
        string Id PK
        string ApplicationId FK
        string Subject
    }

    OPENIDDICT_SCOPE {
        string Id PK
    }

    OPENIDDICT_TOKEN {
        string Id PK
        string ApplicationId FK
        string AuthorizationId FK
        string Subject
    }

    APPLICATION_USER ||--o{ IDENTITY_USER_ROLE : assigned
    IDENTITY_ROLE ||--o{ IDENTITY_USER_ROLE : includes
    APPLICATION_USER ||--o| USER_WORKSPACE_SETTINGS : configures
    APPLICATION_USER ||--o{ WORKSPACE : creates
    APPLICATION_USER ||--o{ WORKSPACE_MEMBER : joins
    WORKSPACE ||--o{ WORKSPACE_MEMBER : grants_access
    APPLICATION_USER o|--o{ WORKSPACE_MEMBER : adds_member
    APPLICATION_USER o|--o{ AUDIT_EVENT : performs
    WORKSPACE o|--o{ AUDIT_EVENT : relates_to
    OPENIDDICT_APPLICATION o|--o{ OPENIDDICT_AUTHORIZATION : owns
    OPENIDDICT_APPLICATION o|--o{ OPENIDDICT_TOKEN : issues
    OPENIDDICT_AUTHORIZATION o|--o{ OPENIDDICT_TOKEN : scopes
```

## 关系与约束

- `WORKSPACE_MEMBER` 是用户与工作空间的关联实体，主键/唯一约束为 `(WorkspaceId, UserId)`；角色为工作空间级角色。
- `Workspace.CreatedByUserId` 是创建审计关联，不自动代表永远的 Owner；Owner 权限以 `WorkspaceMember.Role` 为准，必须始终至少存在一个有效 Owner。
- `Workspace.StorageKey` 唯一且创建后不可修改，避免用户改名/slug 后仓库物理位置变化。
- 用户和工作空间使用软删除/停用语义；常规外键使用限制删除，避免级联删除仓库、审计或授权数据。
- OpenIddict 表实体字段由官方包的实体模型决定，图中仅表示关键关系，实际列不由项目自行重复定义。
- `WORKSPACE` 的配置存储为 PostgreSQL `jsonb`；配置字段必须有版本号并通过应用层校验。

## Git 仓库（数据库外）

```mermaid
flowchart LR
    Workspace["Workspace row<br/>Id + StorageKey"]
    Root["部署配置的存储根目录"]
    Repo["{StorageKey}/.git"]
    Tree["Git tree<br/>目录 / 文件路径"]
    Blob["Git blob<br/>文档正文"]
    Commit["Git commit<br/>版本 / 操作者摘要"]
    Workspace -->|StorageKey| Root
    Root --> Repo
    Repo --> Tree
    Tree --> Blob
    Repo --> Commit
```
