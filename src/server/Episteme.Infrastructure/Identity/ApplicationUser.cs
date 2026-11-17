using Episteme.Domain.Identity;
using Microsoft.AspNetCore.Identity;

namespace Episteme.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public UserAccountStatus Status { get; set; } = UserAccountStatus.Active;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid TokenGeneration { get; set; } = Guid.NewGuid();
}
