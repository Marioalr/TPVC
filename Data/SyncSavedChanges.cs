using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace TPVC.Data;

/// <summary>Un cambio guardado para volver a aplicarlo (paso 8.9): una fila completa o un borrado.</summary>
public sealed class SavedChange
{
    public long Id { get; set; }

    /// <summary><see cref="SyncSavedChanges.SourceThisDevice"/>, <see cref="SyncSavedChanges.SourceCloud"/> o el nombre de un archivo.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>La generación de la nube en que se escribió (o la que tenía la nube antes de reemplazarla).</summary>
    public string? Generation { get; set; }

    public string EntityType { get; set; } = string.Empty;

    /// <summary>En minúsculas, como viaja.</summary>
    public string SyncId { get; set; } = string.Empty;

    /// <summary><see cref="SyncSavedChanges.KindRow"/> o <see cref="SyncSavedChanges.KindTombstone"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>La fila con el formato de la nube (columnas con valores Hrana en JSON) o, en un borrado, su DeletedAtUtc.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>El protocolo con que se escribió la fila y la revisión del contrato (sus columnas).</summary>
    public int Protocol { get; set; }

    public int Contract { get; set; }

    public string SavedAtUtc { get; set; } = string.Empty;
}

/// <summary>
/// Cambios guardados para «Volver a aplicar» (paso 8.9, tabla local "SyncSavedChanges", no se sincroniza): lo que este
/// equipo no alcanzó a subir antes de que otra PC reemplazara la nube con un respaldo, y lo que la nube tenía y el
/// respaldo no (en la PC que la reemplazó). Vive en la base, fuera de las tablas que se borran al volver a bajar la nube.
/// </summary>
public static class SyncSavedChanges
{
    public const string Table = "SyncSavedChanges";
    public const string KindRow = "row";
    public const string KindTombstone = "tombstone";

    /// <summary>Lo que este equipo no alcanzó a subir.</summary>
    public const string SourceThisDevice = "este-equipo";

    /// <summary>Lo que la nube tenía y el respaldo no (en la PC que la reemplazó).</summary>
    public const string SourceCloud = "nube";

    internal const string CreateTableSql =
        "CREATE TABLE IF NOT EXISTS \"SyncSavedChanges\" (\"Id\" INTEGER PRIMARY KEY AUTOINCREMENT, \"Source\" TEXT NOT NULL, " +
        "\"Generation\" TEXT, \"EntityType\" TEXT NOT NULL, \"SyncId\" TEXT NOT NULL, \"Kind\" TEXT NOT NULL, \"Payload\" TEXT NOT NULL, " +
        "\"Protocol\" INTEGER NOT NULL, \"Contract\" INTEGER NOT NULL, \"SavedAtUtc\" TEXT NOT NULL, UNIQUE (\"Source\", \"EntityType\", \"SyncId\"))";

    private const string UpsertSql =
        "INSERT INTO \"SyncSavedChanges\" (\"Source\", \"Generation\", \"EntityType\", \"SyncId\", \"Kind\", \"Payload\", \"Protocol\", \"Contract\", \"SavedAtUtc\") " +
        "VALUES ($source, $generation, $entity, $syncId, $kind, $payload, $protocol, $contract, $saved) ON CONFLICT(\"Source\", \"EntityType\", \"SyncId\") DO UPDATE SET " +
        "\"Generation\" = excluded.\"Generation\", \"Kind\" = excluded.\"Kind\", \"Payload\" = excluded.\"Payload\", \"Protocol\" = excluded.\"Protocol\", " +
        "\"Contract\" = excluded.\"Contract\", \"SavedAtUtc\" = excluded.\"SavedAtUtc\"";

    private const string SelectSql =
        "SELECT \"Id\", \"Source\", \"Generation\", \"EntityType\", \"SyncId\", \"Kind\", \"Payload\", \"Protocol\", \"Contract\", \"SavedAtUtc\" FROM \"SyncSavedChanges\"";

    public static void EnsureTable(AppDbContext db) => db.Database.ExecuteSqlRaw(CreateTableSql);

    /// <summary>Guarda (o reemplaza: el mismo registro de la misma fuente se guarda una vez) los cambios.</summary>
    public static async Task SaveAsync(AppDbContext db, IEnumerable<SavedChange> changes, CancellationToken ct = default)
    {
        foreach (var c in changes)
        {
            await db.Database.ExecuteSqlRawAsync(UpsertSql, Parameters(c), ct);
        }
    }

    private static SqliteParameter[] Parameters(SavedChange c) => new[]
    {
        new SqliteParameter("$source", c.Source),
        new SqliteParameter("$generation", (object?)c.Generation ?? DBNull.Value),
        new SqliteParameter("$entity", c.EntityType),
        new SqliteParameter("$syncId", c.SyncId.ToLowerInvariant()),
        new SqliteParameter("$kind", c.Kind),
        new SqliteParameter("$payload", c.Payload),
        new SqliteParameter("$protocol", c.Protocol),
        new SqliteParameter("$contract", c.Contract),
        new SqliteParameter("$saved", c.SavedAtUtc)
    };

    public static async Task<List<SavedChange>> ReadAllAsync(AppDbContext db, CancellationToken ct = default) =>
        await db.Database.SqlQueryRaw<SavedChange>(SelectSql + " ORDER BY \"Id\"").ToListAsync(ct);

    public static async Task<int> CountAsync(AppDbContext db, CancellationToken ct = default) =>
        (await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM \"SyncSavedChanges\"").ToListAsync(ct)).First();

    public static Task DeleteAsync(AppDbContext db, IEnumerable<long> ids, CancellationToken ct = default)
    {
        var list = ids.ToList();
        if (list.Count == 0) return Task.CompletedTask;
        return db.Database.ExecuteSqlRawAsync($"DELETE FROM \"SyncSavedChanges\" WHERE \"Id\" IN ({string.Join(", ", list)})", ct);
    }

    public static Task DeleteAllAsync(AppDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync("DELETE FROM \"SyncSavedChanges\"", ct);

    /// <summary>Escribe los cambios en un archivo SQLite aparte (la misma tabla), para guardarlo o llevarlo a otro equipo.</summary>
    public static void WriteFile(string path, IEnumerable<SavedChange> changes)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        connection.Open();
        using var tx = connection.BeginTransaction();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = CreateTableSql;
            create.ExecuteNonQuery();
        }
        foreach (var c in changes)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = UpsertSql;
            insert.Parameters.AddRange(Parameters(c));
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Lee los cambios de un archivo escrito con <see cref="WriteFile"/>.</summary>
    public static List<SavedChange> ReadFile(string path)
    {
        var list = new List<SavedChange>();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " ORDER BY \"Id\"";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new SavedChange
            {
                Id = reader.GetInt64(0),
                Source = reader.GetString(1),
                Generation = reader.IsDBNull(2) ? null : reader.GetString(2),
                EntityType = reader.GetString(3),
                SyncId = reader.GetString(4),
                Kind = reader.GetString(5),
                Payload = reader.GetString(6),
                Protocol = reader.GetInt32(7),
                Contract = reader.GetInt32(8),
                SavedAtUtc = reader.GetString(9)
            });
        }
        return list;
    }
}
