using Refit;

namespace Episteme.Client;

public interface IEpistemeApi
{
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
public sealed record SetWorkspaceMemberRequest(string Role);
public sealed record WorkspaceMemberResponse(Guid UserId, string Role, DateTimeOffset JoinedAt);
public sealed record WorkspaceMemberListResponse(IReadOnlyList<WorkspaceMemberResponse> Items, int Offset, int Limit, bool HasMore);
public sealed record WorkspaceContentEntryResponse(string Name, string Kind);
public sealed record WorkspaceDirectoryResponse(string CommitId, IReadOnlyList<WorkspaceContentEntryResponse> Entries);
public sealed record WorkspaceDocumentResponse(string CommitId, string Content);
public sealed record PutWorkspaceDocumentRequest(string ExpectedHead, string Content);
public sealed record DeleteWorkspaceDocumentRequest(string ExpectedHead);
public sealed record DeleteWorkspaceDirectoryRequest(string ExpectedHead, bool Recursive);
public sealed record MoveWorkspacePathRequest(string ExpectedHead, string DestinationPath);
public sealed record RestoreWorkspaceDocumentRequest(string ExpectedHead, string SourceCommit);
public sealed record WorkspaceCommitResponse(string CommitId);
public sealed record WorkspaceDocumentHistoryResponse(
    string HeadCommitId, IReadOnlyList<string> Commits, int Offset, int Limit, bool HasMore);

public sealed record WorkspaceListResponse(
    IReadOnlyList<WorkspaceResponse> Items,
    int Offset,
    int Limit,
    bool HasMore);

public sealed record UpdateUserWorkspaceSettingsRequest(string DefaultWorkspaceDirectory);

public sealed record UserWorkspaceSettingsResponse(string DefaultWorkspaceDirectory);

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
    DateTimeOffset CreatedAt, bool IsAdministrator, int OwnedWorkspaceCount = 0);
public sealed record ManagedUserListResponse(IReadOnlyList<ManagedUserResponse> Items, int Offset, int Limit, bool HasMore);
public sealed record UserAdministrationOverviewResponse(int TotalUsers, int ActiveUsers, int TotalWorkspaces,
    int ProvisioningWorkspaces, int AdministratorCount, IReadOnlyList<ManagedUserResponse> RecentUsers);
public sealed record AdminOwnedWorkspaceResponse(Guid Id, string Name, string Type, string Status, DateTimeOffset CreatedAt);
public sealed record AdminOwnedWorkspaceListResponse(
    IReadOnlyList<AdminOwnedWorkspaceResponse> Items, int Offset, int Limit, bool HasMore);
