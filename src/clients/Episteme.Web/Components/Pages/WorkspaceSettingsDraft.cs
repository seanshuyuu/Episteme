using Episteme.Client;

namespace Episteme.Web.Components.Pages;

/// <summary>
/// Editable snapshot for the unified workspace settings dialog. Nothing here is sent to the
/// server until the single "save" action is confirmed.
/// </summary>
public sealed class WorkspaceSettingsDraft
{
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Code";
    public string Description { get; set; } = "";
    public Guid ExpectedVersion { get; set; }
    public bool IsOwner { get; set; }
    public string Role { get; set; } = "";

    public string OriginalName { get; set; } = "";
    public string OriginalType { get; set; } = "Code";
    public string OriginalDescription { get; set; } = "";

    public bool McpSettingsLoaded { get; set; }
    public string McpSettingsError { get; set; } = "";
    public string McpMembersError { get; set; } = "";
    public bool McpEnabled { get; set; }
    public bool OriginalMcpEnabled { get; set; }
    public string[] GrantedPermissions { get; set; } = [];
    public HashSet<string> PersonalPermissions { get; } = new(StringComparer.Ordinal);
    public string[] OriginalPersonalPermissions { get; set; } = [];

    public List<WorkspaceMemberResponse> Members { get; } = [];
    public string SelectedMember { get; set; } = "";
    private readonly Dictionary<Guid, HashSet<string>> _memberGrants = [];
    private readonly Dictionary<Guid, string[]> _originalMemberGrants = [];

    public Guid? SelectedMemberId => Guid.TryParse(SelectedMember, out var id) ? id : null;
    public string? SelectedMemberRole => SelectedMemberId is { } id
        ? Members.FirstOrDefault(member => member.UserId == id)?.Role
        : null;

    public static WorkspaceSettingsDraft FromWorkspace(WorkspaceResponse workspace) => new()
    {
        WorkspaceId = workspace.Id,
        Name = workspace.Name,
        Type = workspace.Type,
        Description = workspace.Description ?? "",
        ExpectedVersion = workspace.ConcurrencyStamp,
        IsOwner = workspace.Role == "Owner",
        Role = workspace.Role ?? "",
        OriginalName = workspace.Name,
        OriginalType = workspace.Type,
        OriginalDescription = workspace.Description ?? ""
    };

    public void ApplyMcp(McpWorkspaceSettingsResponse settings)
    {
        McpEnabled = settings.Enabled;
        OriginalMcpEnabled = settings.Enabled;
        GrantedPermissions = settings.GrantedPermissions;
        PersonalPermissions.UnionWith(settings.PersonalPermissions);
        OriginalPersonalPermissions = settings.PersonalPermissions;
        McpSettingsLoaded = true;
    }

    public static bool AllowedForRole(string? role, string permission) =>
        role == "Owner" ||
        (role == "Editor" && permission is not ("UpdateWorkspace" or "ArchiveWorkspace")) ||
        (role == "Reader" && permission is "ReadWorkspace" or "ReadProject" or "ReadDocument");

    public bool CanSelectPersonal(string permission) =>
        IsOwner || (GrantedPermissions.Contains(permission) && AllowedForRole(Role, permission));

    public bool CanGrantFor(Guid memberId, string permission) =>
        AllowedForRole(Members.FirstOrDefault(member => member.UserId == memberId)?.Role, permission);

    public string[] SelectedPersonalPermissions() =>
        PersonalPermissions.Where(CanSelectPersonal).Order(StringComparer.Ordinal).ToArray();

    public bool GrantLoaded(Guid memberId) => _originalMemberGrants.ContainsKey(memberId);

    public void SetGrantOriginal(Guid memberId, string[] permissions)
    {
        _originalMemberGrants[memberId] = permissions;
        _memberGrants[memberId] = new HashSet<string>(permissions, StringComparer.Ordinal);
    }

    public HashSet<string> GrantDraft(Guid memberId)
    {
        if (!_memberGrants.TryGetValue(memberId, out var draft))
        {
            draft = new HashSet<string>(StringComparer.Ordinal);
            _memberGrants[memberId] = draft;
        }
        return draft;
    }

    public void MarkGrantSaved(Guid memberId, string[] permissions) =>
        _originalMemberGrants[memberId] = permissions;

    private string[] FilterGrant(Guid memberId, IEnumerable<string> permissions)
    {
        var role = Members.FirstOrDefault(member => member.UserId == memberId)?.Role;
        return permissions.Where(permission => AllowedForRole(role, permission))
            .Order(StringComparer.Ordinal).ToArray();
    }

    public IEnumerable<(Guid MemberId, string[] Permissions)> ChangedMemberGrants =>
        _memberGrants
            .Select(entry => (entry.Key, Permissions: FilterGrant(entry.Key, entry.Value)))
            .Where(entry => !entry.Permissions.SequenceEqual(FilterGrant(entry.Key,
                _originalMemberGrants.TryGetValue(entry.Key, out var original) ? original : [])));

    public bool BasicChanged => IsOwner &&
        (Name != OriginalName || Type != OriginalType || Description != OriginalDescription);

    public bool EnabledChanged => IsOwner && McpSettingsLoaded && McpEnabled != OriginalMcpEnabled;

    public bool PersonalChanged => McpSettingsLoaded &&
        !PersonalPermissions.SetEquals(OriginalPersonalPermissions);

    public bool HasChanges => BasicChanged || EnabledChanged || PersonalChanged || ChangedMemberGrants.Any();

    public int ChangeCount =>
        (BasicChanged ? 1 : 0) + (EnabledChanged ? 1 : 0) + (PersonalChanged ? 1 : 0) +
        ChangedMemberGrants.Count();

    public void MarkBasicSaved(WorkspaceResponse updated)
    {
        Name = updated.Name;
        Type = updated.Type;
        Description = updated.Description ?? "";
        ExpectedVersion = updated.ConcurrencyStamp;
        OriginalName = Name;
        OriginalType = Type;
        OriginalDescription = Description;
    }

    public void MarkEnabledSaved() => OriginalMcpEnabled = McpEnabled;

    public void MarkPersonalSaved(string[] permissions)
    {
        PersonalPermissions.Clear();
        PersonalPermissions.UnionWith(permissions);
        OriginalPersonalPermissions = permissions;
    }
}
