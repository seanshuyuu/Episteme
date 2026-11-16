using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Episteme.Server;

/// <summary>
/// Idempotent, migration-free column upgrades for databases created by older builds.
/// EnsureCreated never alters an existing table, so newly introduced non-null columns
/// are added with portable raw SQL at startup. Safe to run on every boot and on both
/// SQLite and PostgreSQL.
/// </summary>
public static class SchemaUpgrade
{
    public static async Task EnsureColumnsAsync(
        IDbContextFactory<EpistemeDbContext> factory, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        var sqlite = (db.Database.ProviderName ?? "")
            .Contains("Sqlite", StringComparison.OrdinalIgnoreCase);

        if (!await HasColumnAsync(conn, sqlite, "Nodes", "SortOrder", ct))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sqlite
                ? "ALTER TABLE \"Nodes\" ADD COLUMN \"SortOrder\" INTEGER NOT NULL DEFAULT 0"
                : "ALTER TABLE \"Nodes\" ADD COLUMN \"SortOrder\" integer NOT NULL DEFAULT 0";
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<bool> HasColumnAsync(
        DbConnection conn, bool sqlite, string table, string column, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sqlite
            ? "SELECT COUNT(*) FROM pragma_table_info('" + table + "') WHERE name = @c"
            : "SELECT COUNT(*) FROM information_schema.columns " +
              "WHERE table_name = '" + table + "' AND column_name = @c";
        var p = cmd.CreateParameter();
        p.ParameterName = "@c";
        p.Value = column;
        cmd.Parameters.Add(p);
        var raw = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(raw ?? 0) > 0;
    }
}
