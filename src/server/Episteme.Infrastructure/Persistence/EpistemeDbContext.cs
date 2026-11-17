using Episteme.Domain.Workspaces;
using Episteme.Domain.Identity;
using Episteme.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Episteme.Infrastructure.Persistence;

public sealed class EpistemeDbContext(DbContextOptions<EpistemeDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<UserWorkspaceSettings> UserWorkspaceSettings => Set<UserWorkspaceSettings>();
    public DbSet<WorkspaceAuditEvent> WorkspaceAuditEvents => Set<WorkspaceAuditEvent>();
    public DbSet<AccountAuditEvent> AccountAuditEvents => Set<AccountAuditEvent>();
    public DbSet<OAuthRevocationAuditEvent> OAuthRevocationAuditEvents => Set<OAuthRevocationAuditEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.UseOpenIddict();

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(user => user.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(user => user.CreatedAt).IsRequired();
        });

        builder.Entity<Workspace>(entity =>
        {
            entity.ToTable("Workspaces");
            entity.HasKey(workspace => workspace.Id);
            entity.Property(workspace => workspace.Name).HasMaxLength(120).IsRequired();
            entity.Property(workspace => workspace.Slug).HasMaxLength(80).IsRequired();
            entity.HasIndex(workspace => workspace.Slug).IsUnique();
            entity.Property(workspace => workspace.Type).HasConversion<string>().HasMaxLength(32);
            entity.Property(workspace => workspace.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(workspace => workspace.StorageKey).HasMaxLength(32).IsRequired();
            entity.HasIndex(workspace => workspace.StorageKey).IsUnique();
            entity.Property(workspace => workspace.DefaultBranch).HasMaxLength(255).IsRequired();
            entity.Property(workspace => workspace.ConfigurationJson).HasColumnType("jsonb").IsRequired();
            entity.Property(workspace => workspace.ProvisioningDirectoriesJson).HasColumnType("jsonb");
            entity.Property(workspace => workspace.ConcurrencyStamp).IsConcurrencyToken();
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(workspace => workspace.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<WorkspaceMember>(entity =>
        {
            entity.ToTable("WorkspaceMembers");
            entity.HasKey(member => new { member.WorkspaceId, member.UserId });
            entity.Property(member => member.Role).HasConversion<string>().HasMaxLength(32);
            entity.Property(member => member.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasOne<Workspace>()
                .WithMany()
                .HasForeignKey(member => member.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(member => member.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(member => member.AddedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<UserWorkspaceSettings>(entity =>
        {
            entity.ToTable("UserWorkspaceSettings");
            entity.HasKey(settings => settings.UserId);
            entity.Property(settings => settings.DefaultWorkspaceDirectory).HasMaxLength(1024).IsRequired();
            entity.Property(settings => settings.PreferencesJson).HasColumnType("jsonb").IsRequired();
            entity.HasOne<ApplicationUser>()
                .WithOne()
                .HasForeignKey<UserWorkspaceSettings>(settings => settings.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<WorkspaceAuditEvent>(entity =>
        {
            entity.ToTable("WorkspaceAuditEvents");
            entity.HasKey(audit => audit.Id);
            entity.Property(audit => audit.EventType).HasMaxLength(80).IsRequired();
            entity.Property(audit => audit.TargetRole).HasConversion<string>().HasMaxLength(32);
            entity.HasIndex(audit => new { audit.WorkspaceId, audit.OccurredAt });
            entity.HasOne<Workspace>().WithMany().HasForeignKey(audit => audit.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(audit => audit.ActorUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AccountAuditEvent>(entity =>
        {
            entity.ToTable("AccountAuditEvents");
            entity.HasKey(audit => audit.Id);
            entity.Property(audit => audit.EventType).HasMaxLength(80).IsRequired();
            entity.Property(audit => audit.TargetTokenId).HasMaxLength(36);
            entity.Property(audit => audit.TargetAuthorizationId).HasMaxLength(36);
            entity.HasIndex(audit => new { audit.TargetUserId, audit.OccurredAt });
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(audit => audit.ActorUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(audit => audit.TargetUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<OAuthRevocationAuditEvent>(entity =>
        {
            entity.ToTable("OAuthRevocationAuditEvents");
            entity.HasKey(audit => audit.Id);
            entity.Property(audit => audit.ClientId).HasMaxLength(100).IsRequired();
            entity.Property(audit => audit.Subject).HasMaxLength(400);
            entity.Property(audit => audit.TokenId).IsRequired();
            entity.HasIndex(audit => new { audit.Subject, audit.OccurredAt });
        });
    }
}
