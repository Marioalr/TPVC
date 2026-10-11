using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Models;
using TPVC.Services;

// El SQL dinámico de este archivo solo usa nombres de tablas, columnas y clases del modelo de EF;
// nunca datos capturados por el usuario.
#pragma warning disable EF1002, EF1003

namespace TPVC.Data;

/// <summary>
/// Registro de cambios por secuencia (paso 8.5 del plan de la Fase 8). Un trigger en cada tabla sincronizada anota en
/// "SyncChangeLog" qué fila se creó, cambió o borró, con un Seq que solo crece; la subida manda lo que tenga Seq mayor
/// que el último que subió (<see cref="DbMeta.LastPushedSeq"/>). A diferencia del cursor de reloj de antes, no depende
/// de la hora del equipo y también ve los borrados en cascada (SQLite dispara el trigger de la tabla hija).
/// Los triggers también ponen la lápida de cada borrado.
/// </summary>
public static class SyncChangeLog
{
    public const string Table = "SyncChangeLog";

    /// <summary>
    /// Con una fila aquí, los triggers no anotan nada. La bajada la pone y la quita dentro de su transacción
    /// (<see cref="SaveRemoteChangesAsync"/>): lo recibido no vuelve a subir y ninguna otra conexión ve la fila.
    /// </summary>
    public const string ApplyStateTable = "SyncApplyState";

    internal const string TriggerPrefix = "TPVC_sync_";

    private const string EmptySyncId = "00000000-0000-0000-0000-000000000000";

    private static readonly string[] TableStatements =
    {
        // AUTOINCREMENT: un Seq nunca se repite, aunque se borre el más alto. Los triggers borran la anotación anterior
        // de la fila y la vuelven a insertar: una fila ya anotada recibe un Seq nuevo (si se conservara la vieja, su
        // Seq quedaría por debajo del cursor y la edición no subiría)
        $"CREATE TABLE IF NOT EXISTS {Q(Table)} (\"Seq\" INTEGER PRIMARY KEY AUTOINCREMENT, \"EntityType\" TEXT NOT NULL, " +
        "\"SyncId\" TEXT NOT NULL, \"Deleted\" INTEGER NOT NULL DEFAULT 0, UNIQUE (\"EntityType\", \"SyncId\"))",
        $"CREATE TABLE IF NOT EXISTS {Q(ApplyStateTable)} (\"Applying\" INTEGER PRIMARY KEY NOT NULL)",
        DbMeta.CreateTableSql
    };

    private static string Q(string identifier) => SyncSchema.Q(identifier);

    // =========================================================================
    // TRIGGERS
    // =========================================================================

    /// <summary>Un trigger del registro. Su nombre lleva un hash de su definición.</summary>
    internal sealed record Trigger(string Name, string Table, string Sql);

    /// <summary>
    /// Tres triggers por tabla sincronizada: AFTER INSERT, AFTER UPDATE OF las columnas que viajan (hoy ninguna es
    /// derivada) más la llave y la marca, y AFTER DELETE, que además pone la lápida.
    /// </summary>
    internal static IReadOnlyList<Trigger> BuildTriggers(IEnumerable<TableMap> tables)
    {
        var triggers = new List<Trigger>();
        foreach (var map in tables)
        {
            var entity = map.ClrType.Name;
            var columns = new[] { nameof(ISyncEntity.SyncId), nameof(ISyncEntity.UpdatedAtUtc) }
                .Concat(map.Columns.Select(c => c.Name))
                .Concat(map.References.Select(r => r.LocalColumn))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(Q);

            triggers.Add(Build(map.Table, "ins", "AFTER INSERT", "NEW", LogSql(entity, "NEW", deleted: false)));
            triggers.Add(Build(map.Table, "upd", $"AFTER UPDATE OF {string.Join(", ", columns)}", "NEW", LogSql(entity, "NEW", deleted: false)));
            triggers.Add(Build(map.Table, "del", "AFTER DELETE", "OLD",
                LogSql(entity, "OLD", deleted: true) +
                $" INSERT INTO {Q(SyncSchema.TombstonesTable)} (\"EntityType\", \"SyncId\", \"DeletedAtUtc\") " +
                $"VALUES ('{entity}', upper(OLD.\"SyncId\"), {TombstoneStampSql});"));
        }
        return triggers;
    }

    // Los SyncId se anotan como los guarda EF Core (en mayúsculas); se pasan a minúsculas al subir.
    // Borrar y volver a insertar en lugar de INSERT OR REPLACE: en un trigger que dispara una acción de llave foránea
    // (el borrado en cascada de las filas hijas, el SET NULL del lote de un movimiento) SQLite cambia el OR REPLACE
    // por ABORT y el borrado entero fallaría con "UNIQUE constraint failed". El efecto es el mismo: un Seq nuevo
    private static string LogSql(string entity, string row, bool deleted) =>
        $"DELETE FROM {Q(Table)} WHERE \"EntityType\" = '{entity}' AND \"SyncId\" = upper({row}.\"SyncId\"); " +
        $"INSERT INTO {Q(Table)} (\"EntityType\", \"SyncId\", \"Deleted\") VALUES ('{entity}', upper({row}.\"SyncId\"), {(deleted ? 1 : 0)});";

    private static Trigger Build(string table, string kind, string timing, string row, string body)
    {
        var definition = $"{timing} ON {Q(table)} FOR EACH ROW " +
                         $"WHEN NOT EXISTS (SELECT 1 FROM {Q(ApplyStateTable)}) AND ifnull({row}.\"SyncId\", '') NOT IN ('', '{EmptySyncId}') " +
                         $"BEGIN {body} END";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definition)))[..8].ToLowerInvariant();
        var name = $"{TriggerPrefix}{table}_{kind}_{hash}";
        return new Trigger(name, table, $"CREATE TRIGGER {Q(name)} {definition}");
    }

    private static readonly string ClockOffsetSql =
        $"ifnull((SELECT CAST(\"Value\" AS INTEGER) FROM {Q(DbMeta.Table)} WHERE \"Key\" = '{DbMeta.ClockOffsetMs}'), 0)";

    /// <summary>Marca de la lápida que pone el trigger de borrado (la hora de SQLite, con el desfase de DbMeta).</summary>
    private static readonly string TombstoneStampSql =
        StampSql("OLD.\"UpdatedAtUtc\"", "(strftime('%Y-%m-%d %H:%M:%f', 'now') || '0000')", ClockOffsetSql);

    /// <summary>
    /// La regla de <see cref="SyncRules.NextLocalStamp"/> (contracts/golden/stamping.json) en SQL: la hora más el
    /// desfase, pero siempre después de la marca guardada. Exacta a 7 decimales, en el formato de 27 caracteres del
    /// protocolo: SQLite solo suma milisegundos, así que suma sobre los primeros 23 caracteres y conserva los 4
    /// dígitos restantes. <paramref name="now27Sql"/> es una hora ya en ese formato.
    /// </summary>
    internal static string StampSql(string storedSql, string now27Sql, string offsetMsSql)
    {
        var stored = Normalize(storedSql);
        var now = AddMilliseconds(now27Sql, offsetMsSql);
        return $"(CASE WHEN {storedSql} IS NOT NULL AND {stored} >= {now} THEN {AddMilliseconds(stored, "1")} ELSE {now} END)";
    }

    /// <summary>Una marca como la guarda EF Core ("…:ss" o "…:ss.F" a "…:ss.FFFFFFF") llevada a 27 caracteres.</summary>
    private static string Normalize(string sql) =>
        $"(CASE WHEN length({sql}) = 19 THEN {sql} || '.0000000' ELSE substr({sql} || '0000000', 1, 27) END)";

    private static string AddMilliseconds(string stamp27Sql, string msSql) =>
        $"(strftime('%Y-%m-%d %H:%M:%f', substr({stamp27Sql}, 1, 23), printf('%+.3f seconds', ({msSql}) / 1000.0)) || substr({stamp27Sql}, 24, 4))";

    // =========================================================================
    // INSTALACIÓN
    // =========================================================================

    /// <summary>
    /// Crea (o pone al día) las tablas del registro y sus triggers. Corre en cada arranque, fuera del atajo de
    /// user_version de <see cref="DatabaseInitializer.EnsureSchema"/>: un respaldo viejo restaurado también los
    /// recibe. Un trigger cuyo nombre (con el hash de su definición) ya no coincide se borra y se vuelve a crear, todo
    /// en una transacción: CREATE TRIGGER IF NOT EXISTS nunca vería una columna nueva. La primera vez, en una base
    /// con datos, antes se hace un respaldo.
    /// </summary>
    public static void EnsureInstalled(AppDbContext db, bool backupBeforeFirstInstall = true)
    {
        var tables = SyncSchema.FromModel(db.Model).Tables;
        var wanted = BuildTriggers(tables);
        var wantedNames = wanted.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var connection = db.Database.GetDbConnection();
        bool openedHere = connection.State != ConnectionState.Open;
        if (openedHere) db.Database.OpenConnection();
        try
        {
            var existingTables = Names(connection, "SELECT name FROM sqlite_master WHERE type = 'table'");
            var existingTriggers = Names(connection,
                $"SELECT name FROM sqlite_master WHERE type = 'trigger' AND substr(name, 1, {TriggerPrefix.Length}) = '{TriggerPrefix}'");
            bool firstInstall = !existingTables.Contains(Table);

            if (!firstInstall && existingTables.Contains(ApplyStateTable) && existingTables.Contains(DbMeta.Table)
                && existingTriggers.SetEquals(wantedNames) && DbMeta.Get(db, DbMeta.DbInstanceId) != null)
            {
                // Nunca debería quedar una (la bajada la pone y la quita en la misma transacción), pero con ella los
                // triggers no anotarían nada
                db.Database.ExecuteSqlRaw($"DELETE FROM {Q(ApplyStateTable)};");
                return;
            }

            if (firstInstall && backupBeforeFirstInstall && HasData(connection, tables))
            {
                BackupBeforeFirstInstall(connection.DataSource);
            }

            using var tx = db.Database.BeginTransaction();
            foreach (var sql in TableStatements) db.Database.ExecuteSqlRaw(sql + ";");
            foreach (var stale in existingTriggers.Where(n => !wantedNames.Contains(n)))
            {
                db.Database.ExecuteSqlRaw($"DROP TRIGGER IF EXISTS {Q(stale)};");
            }
            foreach (var trigger in wanted.Where(t => !existingTriggers.Contains(t.Name)))
            {
                db.Database.ExecuteSqlRaw(trigger.Sql + ";");
            }
            db.Database.ExecuteSqlRaw($"DELETE FROM {Q(ApplyStateTable)};");
            db.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO \"DbMeta\" (\"Key\", \"Value\") VALUES ({0}, {1});",
                DbMeta.DbInstanceId, Guid.NewGuid().ToString());
            tx.Commit();

            var replaced = existingTriggers.Count(n => !wantedNames.Contains(n));
            AppLogger.Info(firstInstall
                ? $"Registro de cambios de la sincronización instalado ({wanted.Count} triggers)."
                : $"Registro de cambios: {replaced} triggers reemplazados por su versión nueva.");
        }
        finally
        {
            if (openedHere) db.Database.CloseConnection();
        }
    }

    private static bool HasData(DbConnection connection, IEnumerable<TableMap> tables)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT " + string.Join(" OR ", tables.Select(t => $"EXISTS (SELECT 1 FROM {Q(t.Table)})")) + ";";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>
    /// Respaldo antes de instalar los triggers por primera vez. Si falla, se anota y se sigue: el médico no se queda sin
    /// abrir el consultorio por eso (los triggers solo agregan, y el respaldo diario corre igual).
    /// </summary>
    private static void BackupBeforeFirstInstall(string? databasePath)
    {
        if (string.IsNullOrEmpty(databasePath) || !File.Exists(databasePath)) return;
        try
        {
            new BackupService(databasePath).CreateNamedBackup("antes-registro-de-cambios");
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo respaldar la base antes de instalar el registro de cambios", ex);
        }
    }

    private static HashSet<string> Names(DbConnection connection, string sql)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0)) names.Add(reader.GetString(0));
        }
        return names;
    }

    // =========================================================================
    // BAJADA
    // =========================================================================

    /// <summary>
    /// Guarda lo que llegó de la nube con los triggers apagados (una fila en <see cref="ApplyStateTable"/> dentro de la
    /// misma transacción): lo recibido no se anota para volver a subir y sus borrados no dejan lápida.
    /// </summary>
    /// <param name="meta">Claves de <see cref="DbMeta"/> que se guardan en la misma transacción (el cursor de bajada de la
    /// página: nunca queda adelante de lo aplicado).</param>
    public static async Task<int> SaveRemoteChangesAsync(AppDbContext db, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? meta = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync($"INSERT OR IGNORE INTO {Q(ApplyStateTable)} (\"Applying\") VALUES (1);", ct);
        var saved = await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlRawAsync($"DELETE FROM {Q(ApplyStateTable)};", ct);
        foreach (var (key, value) in meta ?? new Dictionary<string, string>())
        {
            await DbMeta.SetAsync(db, key, value, ct);
        }
        await tx.CommitAsync(ct);
        return saved;
    }

    /// <summary>
    /// Anota una fila para subir aunque los triggers estén apagados: lo que se decide durante la bajada y sí debe
    /// subir (el resultado de una combinación, paso 8.22; una reasignación, 8.A). Quien la llama le pone a la fila
    /// una marca nueva.
    /// </summary>
    public static Task MarkForUploadAsync(AppDbContext db, string entityType, Guid syncId, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(
            $"INSERT OR REPLACE INTO {Q(Table)} (\"EntityType\", \"SyncId\", \"Deleted\") VALUES ({{0}}, {{1}}, 0);",
            new object[] { entityType, syncId.ToString().ToUpperInvariant() }, ct);

    // =========================================================================
    // SUBIDA
    // =========================================================================

    /// <summary>Una fila del registro.</summary>
    internal sealed class Entry
    {
        public long Seq { get; set; }
        public string EntityType { get; set; } = string.Empty;
        public string SyncId { get; set; } = string.Empty;
        public long Deleted { get; set; }
    }

    /// <summary>Lo anotado después de <paramref name="afterSeq"/>, en orden.</summary>
    internal static Task<List<Entry>> ReadSinceAsync(AppDbContext db, long afterSeq, CancellationToken ct) =>
        db.Database.SqlQueryRaw<Entry>(
                $"SELECT \"Seq\", \"EntityType\", \"SyncId\", \"Deleted\" FROM {Q(Table)} WHERE \"Seq\" > {{0}} ORDER BY \"Seq\"", afterSeq)
            .ToListAsync(ct);

    /// <summary>Consulta de las filas de una tabla anotadas en (<c>{1}</c>, <c>{2}</c>]; <c>{0}</c> es su EntityType.</summary>
    internal static string LoggedRowsSql(string table) =>
        $"SELECT * FROM {Q(table)} WHERE upper(\"SyncId\") IN " +
        $"(SELECT \"SyncId\" FROM {Q(Table)} WHERE \"EntityType\" = {{0}} AND \"Seq\" > {{1}} AND \"Seq\" <= {{2}})";

    /// <summary>Las lápidas locales de lo anotado en (<c>{0}</c>, <c>{1}</c>].</summary>
    internal const string LoggedTombstonesSql =
        "SELECT * FROM \"SyncTombstones\" AS t WHERE upper(t.\"SyncId\") IN " +
        "(SELECT \"SyncId\" FROM \"SyncChangeLog\" AS c WHERE c.\"EntityType\" = t.\"EntityType\" AND c.\"Seq\" > {0} AND c.\"Seq\" <= {1})";

    /// <summary>¿Hay algo anotado después de <paramref name="afterSeq"/>?</summary>
    public static async Task<bool> HasEntriesAfterAsync(AppDbContext db, long afterSeq, CancellationToken ct = default) =>
        (await db.Database.SqlQueryRaw<long>(
                $"SELECT EXISTS (SELECT 1 FROM {Q(Table)} WHERE \"Seq\" > {{0}}) AS Value", afterSeq)
            .ToListAsync(ct)).FirstOrDefault() != 0;

    /// <summary>Los SyncId (en mayúsculas) anotados después de <paramref name="afterSeq"/>: lo que todavía no sube.</summary>
    internal static async Task<HashSet<string>> PendingSyncIdsAsync(AppDbContext db, string entityType, long afterSeq, CancellationToken ct) =>
        (await db.Database.SqlQueryRaw<string>(
                $"SELECT \"SyncId\" AS Value FROM {Q(Table)} WHERE \"EntityType\" = {{0}} AND \"Seq\" > {{1}}", entityType, afterSeq)
            .ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>El cursor de subida; null si esta base nunca ha subido con el registro (la siguiente subida manda todo).</summary>
    public static async Task<long?> ReadPushCursorAsync(AppDbContext db, CancellationToken ct = default) =>
        long.TryParse(await DbMeta.GetAsync(db, DbMeta.LastPushedSeq, ct), NumberStyles.None, CultureInfo.InvariantCulture, out var seq)
            ? seq
            : null;

    public static Task SavePushCursorAsync(AppDbContext db, long seq, CancellationToken ct = default) =>
        DbMeta.SetAsync(db, DbMeta.LastPushedSeq, seq.ToString(CultureInfo.InvariantCulture), ct);

    /// <summary>
    /// Anota todas las filas y lápidas que no estén ya en el registro (las que ya están conservan su Seq). Para la
    /// primera subida con este cursor (al actualizar, o con una base restaurada) y la "Sincronización completa":
    /// todo sube una vez, y la nube descarta lo que ya tenía.
    /// </summary>
    internal static async Task BackfillAsync(AppDbContext db, IEnumerable<TableMap> tables, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var map in tables)
        {
            await db.Database.ExecuteSqlRawAsync(
                $"INSERT OR IGNORE INTO {Q(Table)} (\"EntityType\", \"SyncId\", \"Deleted\") " +
                $"SELECT '{map.ClrType.Name}', upper(\"SyncId\"), 0 FROM {Q(map.Table)} WHERE ifnull(\"SyncId\", '') NOT IN ('', '{EmptySyncId}');", ct);
        }

        // Lápidas de filas que ya no existen aquí (si la fila existe, ya quedó anotada arriba)
        await db.Database.ExecuteSqlRawAsync(
            $"INSERT OR IGNORE INTO {Q(Table)} (\"EntityType\", \"SyncId\", \"Deleted\") " +
            $"SELECT DISTINCT \"EntityType\", upper(\"SyncId\"), 1 FROM {Q(SyncSchema.TombstonesTable)};", ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Después de restaurar un respaldo: es otro archivo (identidad nueva), la siguiente subida manda todo y la bajada
    /// empieza de cero, como antes con los cursores de la configuración.
    /// </summary>
    public static void ResetAfterRestore(AppDbContext db)
    {
        DbMeta.Remove(db, DbMeta.LastPushedSeq);
        DbMeta.RemoveByPrefix(db, DbMeta.PullCursorPrefix);
        DbMeta.RemoveByPrefix(db, DbMeta.PullColumnsPrefix);
        DbMeta.Set(db, DbMeta.DbInstanceId, Guid.NewGuid().ToString());
    }
}
