using Refit;

namespace Episteme.Client;

public interface IEpistemeApi
{
    [Get("/api/v1/mcp/settings/personal")]
    Task<McpPersonalSettingsResponse> GetMcpPersonalSettingsAsync(CancellationToken cancellationToken = default);

    [Put("/api/v1/mcp/settings/personal")]
    Task<McpPersonalSettingsResponse> SetMcpPersonalSettingsAsync(
        [Body] SetMcpPersonalSettingsRequest request, CancellationToken cancellationToken = default);

    [Get("/api/v1/mcp/settings/workspaces/{workspaceId}")]
    Task<McpWorkspaceSettingsResponse> GetMcpWorkspaceSettingsAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    [Put("/api/v1/mcp/settings/workspaces/{workspaceId}/personal")]
    Task<McpWorkspaceSettingsResponse> SetMcpWorkspacePermissionsAsync(Guid workspaceId,
        [Body] SetMcpPermissionsRequest request, CancellationToken cancellationToken = default);

    [Put("/api/v1/mcp/settings/workspaces/{workspaceId}/enabled")]
    Task<McpWorkspaceEnabledResponse> SetMcpWorkspaceEnabledAsync(Guid workspaceId,
        [Body] SetMcpWorkspaceEnabledRequest request, CancellationToken cancellationToken = default);

    [Get("/api/v1/mcp/settings/workspaces/{workspaceId}/members/{memberId}")]
    Task<McpPermissionsResponse> GetMcpMemberGrantAsync(Guid workspaceId, Guid memberId, CancellationToken cancellationToken = default);

    [Put("/api/v1/mcp/settings/workspaces/{workspaceId}/members/{memberId}")]
    Task<McpPermissionsResponse> SetMcpMemberGrantAsync(Guid workspaceId, Guid memberId,
        [Body] SetMcpPermissionsRequest request, CancellationToken cancellationToken = default);

    [Put("/api/v1/admin/users/{userId}/password")]
    Task ResetManagedUserPasswordAsync(Guid userId, [Body] ResetUserPasswordRequest request, CancellationToken cancellationToken = default);
    [Get("/api/v1/admin/users/{userId}/tokens")]
    Task<UserTokenListResponse> ListManagedUserTokensAsync(Guid userId, int offset = 0, int limit = 25, CancellationToken cancellationToken = default);

    [Delete("/api/v1/admin/users/{userId}/tokens/{tokenId}")]
    Task RevokeManagedUserTokenAsync(Guid userId, string tokenId, CancellationToken cancellationToken = default);
    [Get("/api/v1/me/authorizations")]
    Task<UserAuthorizationListResponse> ListUserAuthorizationsAsync(int offset = 0, int limit = 25, CancellationToken cancellationToken = default);

    [Delete("/api/v1/me/authorizations/{authorizationId}")]
    Task RevokeUserAuthorizationAsync(string authorizationId, CancellationToken cancellationToken = default);

    [Get("/api/v1/me/profile")]
    Task<UserProfileResponse> GetUserProfileAsync(CancellationToken cancellationToken = default);

    [Put("/api/v1/me/profile")]
    Task<UserProfileResponse> UpdateUserProfileAsync([Body] UpdateUserProfileRequest request, CancellationToken cancellationToken = default);

    [Put("/api/v1/me/password")]
    Task ChangeUserPasswordAsync([Body] ChangeUserPasswordRequest request, CancellationToken cancellationToken = default);

    [Post("/api/v1/admin/users")]
    Task<ManagedUserResponse> CreateUserAsync([Body] CreateUserRequest request, CancellationToken cancellationToken = default);

    [Get("/api/v1/admin/users/{userId}")]
    Task<ManagedUserResponse> GetUserAsync(Guid userId, CancellationToken cancellationToken = default);

    [Get("/api/v1/workspaces/{workspaceId}/configuration")]
    Task<WorkspaceConfigurationResponse> GetWorkspaceConfigurationAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    [Put("/api/v1/workspaces/{workspaceId}/configuration")]
    Task<WorkspaceConfigurationResponse> SetWorkspaceConfigurationAsync(Guid workspaceId,
        [Body] SetWorkspaceConfigurationRequest request, CancellationToken cancellationToken = default);

    [Get("/api/v1/admin/users")]
    Task<ManagedUserListResponse> ListUsersAsync(int offset = 0, int limit = 25, CancellationToken cancellationToken = default);

    [Get("/api/v1/admin/overview")]
    Task<UserAdministrationOverviewResponse> GetAdminOverviewAsync(CancellationToken cancellationToken = default);

    [Get("/api/v1/admin/users/{userId}/workspaces")]
    Task<AdminOwnedWorkspaceListResponse> ListUserOwnedWorkspacesAsync(Guid userId, int offset = 0,
        int limit = 25, CancellationToken cancellationToken = default);

    [Put("/api/v1/admin/users/{userId}/status")]
    Task<ManagedUserResponse> SetUserStatusAsync(Guid userId, [Body] SetUserStatusRequest request, CancellationToken cancellationToken = default);

    [Put("/api/v1/admin/users/{userId}/administrator")]
    Task<ManagedUserResponse> SetAdministratorAsync(Guid userId, [Body] SetAdministratorRequest request, CancellationToken cancellationToken = default);

    [Get("/api/v1/me/tokens")]
    Task<UserTokenListResponse> ListUserTokensAsync(int offset = 0, int limit = 25, CancellationToken cancellationToken = default);

    [Delete("/api/v1/me/tokens/{tokenId}")]
    Task RevokeUserTokenAsync(string tokenId, CancellationToken cancellationToken = default);

    [Get("/api/v1/workspaces/{workspaceId}/manifest")]
    Task<WorkspaceManifestResponse> ExportWorkspaceManifestAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    [Get("/api/v1/workspaces/{workspaceId}/manifest?formatVersion=2")]
    Task<WorkspaceManifestResponse> ExportWorkspaceManifestV2Async(Guid workspaceId, CancellationToken cancellationToken = default);

    [Get("/api/v1/workspaces/{workspaceId}/manifest?formatVersion=3")]
    Task<WorkspaceManifestResponse> ExportWorkspaceManifestV3Async(Guid workspaceId, CancellationToken cancellationToken = default);

    [Post("/api/v1/workspaces/import")]
    Task<WorkspaceResponse> ImportWorkspaceManifestAsync([Body] ImportWorkspaceManifestRequest request,
        CancellationToken cancellationToken = default);
    [Put("/api/v1/workspaces/{workspaceId}/directories")]
    Task<WorkspaceCommitResponse> CreateDirectoryAsync(Guid workspaceId, string path,
        [Body] DeleteWorkspaceDocumentRequest request, CancellationToken cancellationToken = default);
    [Delete("/api/v1/workspaces/{workspaceId}/directories")]
    Task<WorkspaceCommitResponse> DeleteDirectoryAsync(Guid workspaceId, string path,
        [Body] DeleteWorkspaceDirectoryRequest request, CancellationToken cancellationToken = default);
    [Post("/api/v1/workspaces/{workspaceId}/directories/move")]
    Task<WorkspaceCommitResponse> MoveDirectoryAsync(Guid workspaceId, string path,
        [Body] MoveWorkspacePathRequest request, CancellationToken cancellationToken = default);
    [Post("/api/v1/workspaces/{workspaceId}/documents/move")]
    Task<WorkspaceCommitResponse> MoveDocumentAsync(Guid workspaceId, string path,
        [Body] MoveWorkspacePathRequest request, CancellationToken cancellationToken = default);
    [Put("/api/v1/workspaces/{workspaceId}/documents")]
    Task<WorkspaceCommitResponse> PutDocumentAsync(Guid workspaceId, string path,
        [Body] PutWorkspaceDocumentRequest request, CancellationToken cancellationToken = default);

    [Put("/api/v1/workspaces/{workspaceId}/documents/binary")]
    Task<WorkspaceCommitResponse> UploadDocumentAsync(Guid workspaceId, string path,
        [Body] PutWorkspaceBinaryDocumentRequest request, CancellationToken cancellationToken = default);

    [Get("/api/v1/workspaces/{workspaceId}/documents/binary")]
    Task<WorkspaceBinaryDocumentResponse> DownloadDocumentAsync(Guid workspaceId, string path,
        CancellationToken cancellationToken = default);

    [Delete("/api/v1/workspaces/{workspaceId}/documents")]
    Task<WorkspaceCommitResponse> DeleteDocumentAsync(Guid workspaceId, string path,
        [Body] DeleteWorkspaceDocumentRequest request, CancellationToken cancellationToken = default);

    [Post("/api/v1/workspaces/{workspaceId}/documents/restore")]
    Task<WorkspaceCommitResponse> RestoreDocumentAsync(Guid workspaceId, string path,
        [Body] RestoreWorkspaceDocumentRequest request, CancellationToken cancellationToken = default);

    [Get("/api/v1/workspaces/{workspaceId}/directories")]
    Task<WorkspaceDirectoryResponse> ListDirectoryAsync(Guid workspaceId, string path = "",
        CancellationToken cancellationToken = default);

    [Get("/api/v1/workspaces/{workspaceId}/documents")]
    Task<WorkspaceDocumentResponse> ReadDocumentAsync(Guid workspaceId, string path,
        CancellationToken cancellationToken = default);

    [Get("/api/v1/workspaces/{workspaceId}/documents/history")]
    Task<WorkspaceDocumentHistoryResponse> GetDocumentHistoryAsync(Guid workspaceId, string path,
        int offset = 0, int limit = 25, CancellationToken cancellationToken = default);

    [Get("/api/v1/workspaces/{workspaceId}/documents/version")]
    Task<WorkspaceDocumentResponse> ReadDocumentVersionAsync(Guid workspaceId, string path, string commitId,
        CancellationToken cancellationToken = default);

    [Get("/api/v1/workspaces/{workspaceId}/members")]
    Task<WorkspaceMemberListResponse> ListWorkspaceMembersAsync(Guid workspaceId, int offset = 0,
        int limit = 25, CancellationToken cancellationToken = default);

    [Put("/api/v1/workspaces/{workspaceId}/members/{memberId}")]
    Task<WorkspaceMemberResponse> SetWorkspaceMemberAsync(Guid workspaceId, Guid memberId,
        [Body] SetWorkspaceMemberRequest request, CancellationToken cancellationToken = default);

    [Delete("/api/v1/workspaces/{workspaceId}/members/{memberId}")]
    Task RemoveWorkspaceMemberAsync(Guid workspaceId, Guid memberId, CancellationToken cancellationToken = default);

    [Post("/api/v1/workspaces")]
    Task<WorkspaceResponse> CreateWorkspaceAsync([Body] CreateWorkspaceRequest request, CancellationToken cancellationToken = default);

    [Post("/api/v1/workspaces/{workspaceId}/provision")]
    Task<WorkspaceResponse> RetryWorkspaceProvisioningAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    [Get("/api/v1/workspaces/provisioning")]
    Task<WorkspaceListResponse> ListPendingWorkspacesAsync(int offset = 0, int limit = 25, CancellationToken cancellationToken = default);

    [Get("/api/v1/workspaces")]
    Task<WorkspaceListResponse> ListWorkspacesAsync(
        int offset = 0,
        int limit = 25,
        CancellationToken cancellationToken = default);

    [Get("/api/v1/workspaces/{workspaceId}/projects")]
    Task<ProjectListResponse> ListProjectsAsync(Guid workspaceId, int offset = 0, int limit = 100,
        CancellationToken cancellationToken = default);

    [Post("/api/v1/workspaces/{workspaceId}/projects")]
    Task<ProjectResponse> CreateProjectAsync(Guid workspaceId, [Body] CreateProjectRequest request,
        CancellationToken cancellationToken = default);

    [Put("/api/v1/workspaces/{workspaceId}/projects/{projectId}")]
    Task<ProjectResponse> UpdateProjectAsync(Guid workspaceId, Guid projectId,
        [Body] UpdateProjectRequest request, CancellationToken cancellationToken = default);

    [Post("/api/v1/workspaces/{workspaceId}/projects/{projectId}/status")]
    Task<ProjectResponse> SetProjectStatusAsync(Guid workspaceId, Guid projectId,
        [Body] SetProjectStatusRequest request, CancellationToken cancellationToken = default);

    [Delete("/api/v1/workspaces/{workspaceId}/projects/{projectId}")]
    Task DeleteProjectAsync(Guid workspaceId, Guid projectId,
        [Body] DeleteProjectRequest request, CancellationToken cancellationToken = default);

    [Post("/api/v1/workspaces/{workspaceId}/projects/{projectId}/promote-folder")]
    Task<ProjectResponse> PromoteProjectFolderAsync(Guid workspaceId, Guid projectId,
        [Body] PromoteFolderRequest request, CancellationToken cancellationToken = default);

    [Post("/api/v1/workspaces/{workspaceId}/projects/{projectId}/demote")]
    Task<ProjectResponse> DemoteProjectAsync(Guid workspaceId, Guid projectId,
        CancellationToken cancellationToken = default);

    [Get("/api/v1/workspaces/{workspaceId}")]
    Task<WorkspaceResponse> GetWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    [Put("/api/v1/workspaces/{workspaceId}")]
    Task<WorkspaceResponse> UpdateWorkspaceAsync(Guid workspaceId, [Body] UpdateWorkspaceRequest request,
        CancellationToken cancellationToken = default);

    [Post("/api/v1/workspaces/{workspaceId}/archive")]
    Task ArchiveWorkspaceAsync(Guid workspaceId, [Body] ArchiveWorkspaceRequest request,
        CancellationToken cancellationToken = default);

    [Get("/api/v1/me/workspace-settings")]
    Task<UserWorkspaceSettingsResponse> GetUserWorkspaceSettingsAsync(CancellationToken cancellationToken = default);

    [Put("/api/v1/me/workspace-settings")]
    Task<UserWorkspaceSettingsResponse> UpdateUserWorkspaceSettingsAsync(
        [Body] UpdateUserWorkspaceSettingsRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record UserAuthorizationListResponse(IReadOnlyList<UserAuthorizationResponse> Items, int Offset, int Limit, bool HasMore);
public sealed record UserProfileResponse(string? DisplayName, string? Email, string? UserName);
public sealed record UpdateUserProfileRequest(string? DisplayName);
public sealed record ChangeUserPasswordRequest(string CurrentPassword, string NewPassword)
{
    public override string ToString() => "ChangeUserPasswordRequest { Password = [REDACTED] }";
}
public sealed record ResetUserPasswordRequest(string Password)
{
    public override string ToString() => "ResetUserPasswordRequest { Password = [REDACTED] }";
}
public sealed record UserAuthorizationResponse(string Id, string? ClientId, string Status, DateTimeOffset? CreatedAt);

public sealed record WorkspaceResponse(
    Guid Id,
    string Name,
    string Slug,
    string Type,
    string? Description,
    string DefaultBranch,
    Guid ConcurrencyStamp,
    string? Role = null,
    string? OwnerName = null,
    string Status = "Active",
    DateTimeOffset CreatedAt = default);

public sealed record WorkspaceConfiguration(int SchemaVersion, string? DefaultExtension = null, string? Language = null,
    string? SkillFormat = null, string? CitationStyle = null, string? Genre = null);
public sealed record WorkspaceConfigurationResponse(WorkspaceConfiguration Configuration, Guid ConcurrencyStamp);
public sealed record SetWorkspaceConfigurationRequest(WorkspaceConfiguration Configuration, Guid ExpectedVersion);
public sealed record WorkspaceManifestResponse(int FormatVersion, string Name, string Type, string? Description,
    WorkspaceConfiguration? Configuration = null, IReadOnlyList<string>? Directories = null);
public sealed record ImportWorkspaceManifestRequest(string Slug, WorkspaceManifestResponse Manifest);

public sealed record UpdateWorkspaceRequest(string Name, string Type, string? Description, Guid ExpectedVersion);
public sealed record CreateWorkspaceRequest(string Name, string Slug, string Type, string? Description);
public sealed record ArchiveWorkspaceRequest(Guid ExpectedVersion);
public sealed record ProjectResponse(
    Guid Id,
    Guid WorkspaceId,
    string Name,
    string Slug,
    string? Description,
    string Status,
    string DefaultBranch,
    Guid ConcurrencyStamp,
    int FileCount,
    int FolderCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
public sealed record ProjectListResponse(IReadOnlyList<ProjectResponse> Items, int Offset, int Limit, bool HasMore);
public sealed record CreateProjectRequest(string Name, string Slug, string? Description);
public sealed record UpdateProjectRequest(string Name, string Slug, string? Description, Guid ExpectedVersion);
public sealed record SetProjectStatusRequest(string Status, Guid ExpectedVersion);
public sealed record DeleteProjectRequest(Guid ExpectedVersion);
public sealed record PromoteFolderRequest(string FolderPath);
public sealed record SetWorkspaceMemberRequest(string Role);
public sealed record WorkspaceMemberResponse(Guid UserId, string Role, DateTimeOffset JoinedAt);
public sealed record WorkspaceMemberListResponse(IReadOnlyList<WorkspaceMemberResponse> Items, int Offset, int Limit, bool HasMore);
public sealed record WorkspaceContentEntryResponse(string Name, string Kind);
public sealed record WorkspaceDirectoryResponse(string CommitId, IReadOnlyList<WorkspaceContentEntryResponse> Entries);
public sealed record WorkspaceDocumentResponse(string CommitId, string Content);
public sealed record WorkspaceBinaryDocumentResponse(string CommitId, byte[] Content);
public sealed record PutWorkspaceDocumentRequest(string ExpectedHead, string Content, string? Comment = null);
public sealed record PutWorkspaceBinaryDocumentRequest(string ExpectedHead, byte[] Content);
public sealed record DeleteWorkspaceDocumentRequest(string ExpectedHead);
public sealed record DeleteWorkspaceDirectoryRequest(string ExpectedHead, bool Recursive);
public sealed record MoveWorkspacePathRequest(string ExpectedHead, string DestinationPath);
public sealed record RestoreWorkspaceDocumentRequest(string ExpectedHead, string SourceCommit);
public sealed record WorkspaceCommitResponse(string CommitId);
public sealed record WorkspaceHistoryEntryResponse(
    string CommitId, string Message, string AuthorName, DateTimeOffset CreatedAt);
public sealed record WorkspaceDocumentHistoryResponse(
    string HeadCommitId, IReadOnlyList<WorkspaceHistoryEntryResponse> Commits, int Offset, int Limit, bool HasMore);

public sealed record WorkspaceListResponse(
    IReadOnlyList<WorkspaceResponse> Items,
    int Offset,
    int Limit,
    bool HasMore);

public sealed record UpdateUserWorkspaceSettingsRequest(string DefaultWorkspaceDirectory);

public sealed record UserWorkspaceSettingsResponse(string DefaultWorkspaceDirectory);

public sealed record McpPersonalSettingsResponse(bool Enabled, bool AllowCreateWorkspace);
public sealed record SetMcpPersonalSettingsRequest(bool Enabled, bool AllowCreateWorkspace);
public sealed record McpWorkspaceSettingsResponse(bool Enabled, string[] PersonalPermissions, string[] GrantedPermissions, bool IsOwner);
public sealed record SetMcpWorkspaceEnabledRequest(bool Enabled);
public sealed record McpWorkspaceEnabledResponse(bool Enabled);
public sealed record SetMcpPermissionsRequest(string[] Permissions);
public sealed record McpPermissionsResponse(string[] Permissions);

public sealed record UserTokenResponse(string Id, string? ClientId, string Type, string Status,
    DateTimeOffset? CreatedAt, DateTimeOffset? ExpiresAt);
public sealed record UserTokenListResponse(IReadOnlyList<UserTokenResponse> Items, int Offset, int Limit, bool HasMore);
public sealed record SetUserStatusRequest(string Status);
public sealed record CreateUserRequest(string Email, string Password)
{
    public override string ToString() => "CreateUserRequest { Password = [REDACTED] }";
}
public sealed record SetAdministratorRequest(bool IsAdministrator);
public sealed record ManagedUserResponse(Guid Id, string? UserName, string? Email, string Status,
    DateTimeOffset CreatedAt, bool IsAdministrator, int OwnedWorkspaceCount = 0, string? DisplayName = null);
public sealed record ManagedUserListResponse(IReadOnlyList<ManagedUserResponse> Items, int Offset, int Limit, bool HasMore);
public sealed record UserAdministrationOverviewResponse(int TotalUsers, int ActiveUsers, int TotalWorkspaces,
    int ProvisioningWorkspaces, int AdministratorCount, IReadOnlyList<ManagedUserResponse> RecentUsers);
public sealed record AdminOwnedWorkspaceResponse(Guid Id, string Name, string Type, string Status, DateTimeOffset CreatedAt);
public sealed record AdminOwnedWorkspaceListResponse(
    IReadOnlyList<AdminOwnedWorkspaceResponse> Items, int Offset, int Limit, bool HasMore);
