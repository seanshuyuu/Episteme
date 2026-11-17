using Episteme.Application.Workspaces;
using Episteme.Domain.Identity;
using Episteme.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Episteme.Infrastructure.Persistence;

public sealed class EfWorkspaceListingRepository(EpistemeDbContext dbContext) : IWorkspaceListingRepository
{
    public async Task<IReadOnlyList<WorkspaceSummary>?> ListForActiveUserAsync(
        Guid userId,
        bool isSystemAdministrator,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        // Fetch account state and the page in one database statement, including when
        // there are no visible workspaces, so a disabled user is not treated as empty.
        var result = await dbContext.Users.AsNoTracking()
            .Where(user => user.Id == userId && user.Status == UserAccountStatus.Active)
            .Select(user => new
            {
                Items = dbContext.Workspaces
                    .AsNoTracking()
                    .Where(workspace => workspace.Status == WorkspaceStatus.Active &&
                        (isSystemAdministrator || dbContext.WorkspaceMembers.Any(member =>
                            member.WorkspaceId == workspace.Id &&
                            member.UserId == user.Id &&
                            member.Status == WorkspaceMemberStatus.Active)))
                    .OrderBy(workspace => workspace.Slug)
                    .ThenBy(workspace => workspace.Id)
                    .Skip(offset)
                    .Take(count)
                    .Select(workspace => new WorkspaceSummary(
                        workspace.Id,
                        workspace.Name,
                        workspace.Slug,
                        workspace.Type,
                        workspace.Description,
                        workspace.DefaultBranch,
                        workspace.ConcurrencyStamp))
                    .ToList()
            })
            .SingleOrDefaultAsync(cancellationToken);

        return result?.Items;
    }
}
