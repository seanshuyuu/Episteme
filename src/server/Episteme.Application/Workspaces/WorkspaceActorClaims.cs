using System.Security.Claims;
namespace Episteme.Application.Workspaces;

public static class WorkspaceActorClaims
{
    public static bool TryGetActor(ClaimsPrincipal principal, out WorkspaceActor? actor)
    {
        var subject = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(subject, out var userId) || userId == Guid.Empty)
        {
            actor = null;
            return false;
        }
        var scopes = principal.FindAll("scope").Concat(principal.FindAll("scp"))
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
        var administrator = principal.Claims.Any(c => (c.Type == ClaimTypes.Role || c.Type == "role") &&
            string.Equals(c.Value, "Administrator", StringComparison.Ordinal));
        actor = new WorkspaceActor(userId, scopes, administrator);
        return true;
    }
}
