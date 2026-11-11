using Episteme.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Episteme.Server;

public sealed class EpistemeDbContext(DbContextOptions<EpistemeDbContext> options) : DbContext(options)
{
    public DbSet<Space> Spaces => Set<Space>();
    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<ContentType> ContentTypes => Set<ContentType>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Part> Parts => Set<Part>();
    public DbSet<Annotation> Annotations => Set<Annotation>();
    public DbSet<Blob> Blobs => Set<Blob>();
    public DbSet<Tree> Trees => Set<Tree>();
    public DbSet<Commit> Commits => Set<Commit>();
    public DbSet<RefRow> Refs => Set<RefRow>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Space>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.WorkspaceId, x.Slug }).IsUnique();
        });

        b.Entity<Node>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.SpaceId, x.Path }).IsUnique();
        });

        b.Entity<ContentType>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.SpaceId, x.Slug, x.Version }).IsUnique();
        });

        b.Entity<Document>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.SpaceId, x.NodeId });
            e.HasIndex(x => new { x.SpaceId, x.Branch });
            e.HasMany(x => x.Parts).WithOne().HasForeignKey(p => p.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Part>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.DocumentId, x.Ord }).IsUnique();
        });

        b.Entity<Annotation>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.PartId);
        });

        b.Entity<Blob>(e => e.HasKey(x => x.Hash));

        b.Entity<Tree>(e => e.HasKey(x => x.Hash));

        b.Entity<Commit>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.SpaceId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.SpaceId, x.CreatedAt });
        });

        b.Entity<RefRow>(e => e.HasKey(x => new { x.SpaceId, x.Name }));

        b.Entity<AuditLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.SpaceId, x.CreatedAt });
        });

        // Npgsql maps DateTime to "timestamp with time zone" and rejects Kind != Utc;
        // SQLite reads back Unspecified. One converter keeps both providers identical.
        var utc = new ValueConverter<DateTime, DateTime>(
            v => v.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v, DateTimeKind.Utc),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        foreach (var entity in b.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                if (property.ClrType == typeof(DateTime)) property.SetValueConverter(utc);
    }
}
