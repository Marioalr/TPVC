using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TPVC.Models;

namespace TPVC.Services;

/// <summary>Una columna del modelo que viaja tal cual (no es llave ni referencia).</summary>
internal sealed class SyncColumn
{
    public required string Name { get; init; }
    public required Type ClrType { get; init; }
    public required bool IsNullable { get; init; }
    public required PropertyInfo Property { get; init; }
    public required string RemoteType { get; init; }
}

/// <summary>Una referencia a otra tabla sincronizada: viaja como el SyncId de su principal ("ProductSyncId").</summary>
internal sealed class SyncReference
{
    public required PropertyInfo Property { get; init; }
    public required Type PrincipalType { get; init; }
    public required string RemoteColumn { get; init; }

    /// <summary>La columna en la base local (el Id entero: "ProductId").</summary>
    public required string LocalColumn { get; init; }
    public required bool IsRequired { get; init; }

    /// <summary>Qué hace EF al borrar el principal; null = referencia suave, sin llave foránea.</summary>
    public required DeleteBehavior? DeleteBehavior { get; init; }
}

/// <summary>Una tabla sincronizada (una entidad <see cref="ISyncEntity"/> del modelo de EF).</summary>
internal sealed class TableMap
{
    public required Type ClrType { get; init; }
    public required string Table { get; init; }
    public required List<SyncColumn> Columns { get; init; }
    public required List<SyncReference> References { get; init; }

    /// <summary>Folios únicos en la base local (texto, una columna): se renombran con "-R1" al recibir uno repetido.</summary>
    public required List<PropertyInfo> UniqueProperties { get; init; }

    /// <summary>Todos los índices únicos de la base local, salvo el de SyncId (nombres de columna).</summary>
    public required List<string[]> UniqueColumns { get; init; }

    public IEnumerable<string> RemoteColumnNames =>
        Columns.Select(c => c.Name).Concat(References.Select(r => r.RemoteColumn));

    public string UpsertSql
    {
        get
        {
            var q = SyncSchema.Q;
            var cols = RemoteColumnNames.ToList();
            var insertCols = string.Join(", ", new[] { "\"SyncId\"", "\"UpdatedAtUtc\"", "\"SyncedAtUtc\"", "\"OriginDeviceId\"" }
                .Concat(cols.Select(q)));
            var values = string.Join(", ", new[] { "?", "?", TursoSyncService.ServerNowSql, "?" }.Concat(cols.Select(_ => "?")));
            var updates = string.Join(", ", new[] { "UpdatedAtUtc", "SyncedAtUtc", "OriginDeviceId" }.Concat(cols)
                .Select(c => $"{q(c)} = excluded.{q(c)}"));
            // Un registro que otro equipo ya borró (y su borrado es igual o más nuevo) no se vuelve
            // a crear: la subida reenvía filas con marca adelantada que pudieron borrarse en la nube. El último
            // argumento es la generación del equipo (cerca de generación, paso 8.9)
            return $"INSERT INTO {q(Table)} ({insertCols}) SELECT {values} " +
                   $"WHERE NOT EXISTS (SELECT 1 FROM {q(SyncSchema.TombstonesTable)} WHERE \"EntityType\" = ? AND \"SyncId\" = ? AND \"DeletedAtUtc\" >= ?) " +
                   $"AND {SyncSchema.GenerationFenceSql} " +
                   $"ON CONFLICT(\"SyncId\") DO UPDATE SET {updates} " +
                   $"WHERE excluded.\"UpdatedAtUtc\" > {q(Table)}.\"UpdatedAtUtc\";";
        }
    }
}

/// <summary>
/// El modelo de EF visto como el protocolo de sincronización v2: qué tablas viajan, en qué orden, con qué
/// columnas y referencias, y el DDL de la nube con su huella. Lo usan el motor (<see cref="TursoSyncService"/>)
/// y el contrato exportado (<see cref="SyncContract"/>), así los dos describen lo mismo.
/// </summary>
internal sealed class SyncSchema
{
    public const string TombstonesTable = "SyncTombstones";
    public const string MetaTable = "_tpvc_meta";

    /// <summary>
    /// Referencias enteras sin relación declarada en EF que también deben traducirse a SyncId.
    /// </summary>
    private static readonly (Type Entity, string Property, Type Principal)[] SoftReferences =
    {
        (typeof(SaleItem), nameof(SaleItem.ProductId), typeof(Product)),
        (typeof(SaleItem), nameof(SaleItem.ClinicalServiceId), typeof(ClinicalService)),
    };

    /// <summary>Tablas en orden de dependencias: la subida va en este orden.</summary>
    public required IReadOnlyList<TableMap> Tables { get; init; }

    /// <summary>DDL de la nube, en orden; la primera instrucción crea <see cref="MetaTable"/>.</summary>
    public required IReadOnlyList<string> Statements { get; init; }

    /// <summary>Huella del DDL (y de las versiones del protocolo y del contrato) que se guarda en _tpvc_meta.schema.</summary>
    public required string Fingerprint { get; init; }

    public static string Q(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    public static SyncSchema FromModel(IModel model)
    {
        var tables = OrderByDependencies(BuildTableMaps(model));
        var statements = BuildStatements(tables).ToList();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"v{TursoSyncService.RemoteSchemaVersion}|c{SyncContract.Revision}|" + string.Join("|", statements))))[..16];
        return new SyncSchema { Tables = tables, Statements = statements, Fingerprint = fingerprint };
    }

    private static List<TableMap> BuildTableMaps(IModel model)
    {
        var maps = new List<TableMap>();

        foreach (var entityType in model.GetEntityTypes().Where(t => typeof(ISyncEntity).IsAssignableFrom(t.ClrType)))
        {
            var table = entityType.GetTableName();
            if (string.IsNullOrEmpty(table)) continue;
            var storeObject = StoreObjectIdentifier.Table(table, entityType.GetSchema());

            var references = new List<SyncReference>();
            var referenceProps = new HashSet<string>();

            foreach (var fk in entityType.GetForeignKeys())
            {
                if (fk.Properties.Count != 1 || !typeof(ISyncEntity).IsAssignableFrom(fk.PrincipalEntityType.ClrType)) continue;
                var prop = fk.Properties[0];
                if (prop.PropertyInfo == null) continue;
                references.Add(new SyncReference
                {
                    Property = prop.PropertyInfo,
                    PrincipalType = fk.PrincipalEntityType.ClrType,
                    RemoteColumn = ReferenceColumnName(prop.Name),
                    LocalColumn = prop.GetColumnName(storeObject) ?? prop.Name,
                    IsRequired = !prop.IsNullable,
                    DeleteBehavior = fk.DeleteBehavior
                });
                referenceProps.Add(prop.Name);
            }

            foreach (var (entity, propertyName, principal) in SoftReferences.Where(r => r.Entity == entityType.ClrType))
            {
                var prop = entityType.FindProperty(propertyName);
                if (prop?.PropertyInfo == null || referenceProps.Contains(propertyName)) continue;
                references.Add(new SyncReference
                {
                    Property = prop.PropertyInfo,
                    PrincipalType = principal,
                    RemoteColumn = ReferenceColumnName(propertyName),
                    LocalColumn = prop.GetColumnName(storeObject) ?? propertyName,
                    IsRequired = false,
                    DeleteBehavior = null
                });
                referenceProps.Add(propertyName);
            }

            var columns = entityType.GetProperties()
                .Where(p => !p.IsPrimaryKey()
                            && p.PropertyInfo != null
                            && p.Name != nameof(ISyncEntity.SyncId)
                            && p.Name != nameof(ISyncEntity.UpdatedAtUtc)
                            && !referenceProps.Contains(p.Name))
                .Select(p => new SyncColumn
                {
                    Name = p.GetColumnName(storeObject) ?? p.Name,
                    ClrType = p.ClrType,
                    IsNullable = p.IsNullable,
                    Property = p.PropertyInfo!,
                    RemoteType = RemoteTypeFor(p.ClrType)
                })
                .ToList();

            var uniqueIndexes = entityType.GetIndexes()
                .Where(i => i.IsUnique && !(i.Properties.Count == 1 && i.Properties[0].Name == nameof(ISyncEntity.SyncId)))
                .ToList();

            var uniqueProps = uniqueIndexes
                .Where(i => i.Properties.Count == 1 && i.Properties[0].ClrType == typeof(string) && i.Properties[0].PropertyInfo != null)
                .Select(i => i.Properties[0].PropertyInfo!)
                .ToList();

            maps.Add(new TableMap
            {
                ClrType = entityType.ClrType,
                Table = table,
                Columns = columns,
                References = references,
                UniqueProperties = uniqueProps,
                UniqueColumns = uniqueIndexes
                    .Select(i => i.Properties.Select(p => p.GetColumnName(storeObject) ?? p.Name).ToArray())
                    .ToList()
            });
        }

        return maps;
    }

    /// <summary>ProductId → ProductSyncId, PatientRecordId → PatientRecordSyncId...</summary>
    public static string ReferenceColumnName(string propertyName) =>
        (propertyName.EndsWith("Id", StringComparison.Ordinal) ? propertyName[..^2] : propertyName) + "SyncId";

    public static string RemoteTypeFor(Type clrType)
    {
        var t = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (t == typeof(bool) || t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t.IsEnum) return "INTEGER";
        if (t == typeof(double) || t == typeof(float)) return "REAL";
        if (t == typeof(byte[])) return "BLOB";
        return "TEXT"; // string, decimal (exacto), DateTime, Guid
    }

    /// <summary>Principales antes que dependientes (Categoría → Producto → Lote → Movimiento...).</summary>
    private static List<TableMap> OrderByDependencies(List<TableMap> maps)
    {
        var ordered = new List<TableMap>();
        var pending = maps.OrderBy(m => m.Table, StringComparer.Ordinal).ToList();
        while (pending.Count > 0)
        {
            var next = pending.FirstOrDefault(m => m.References.All(r =>
                r.PrincipalType == m.ClrType || ordered.Any(o => o.ClrType == r.PrincipalType) || pending.All(p => p.ClrType != r.PrincipalType)))
                ?? pending[0];
            ordered.Add(next);
            pending.Remove(next);
        }
        return ordered;
    }

    // =========================================================================
    // ESQUEMA REMOTO
    // =========================================================================

    /// <summary>DDL de una tabla sincronizada en la nube: su CREATE TABLE y el índice de la descarga.</summary>
    public static IEnumerable<string> TableStatements(TableMap map)
    {
        var cols = map.Columns.Select(c => $"{Q(c.Name)} {c.RemoteType}")
            .Concat(map.References.Select(r => $"{Q(r.RemoteColumn)} TEXT"));
        yield return $"CREATE TABLE IF NOT EXISTS {Q(map.Table)} (\"SyncId\" TEXT PRIMARY KEY NOT NULL, \"UpdatedAtUtc\" TEXT NOT NULL, " +
                     $"\"SyncedAtUtc\" TEXT NOT NULL, \"OriginDeviceId\" TEXT, {string.Join(", ", cols)})";
        yield return $"CREATE INDEX IF NOT EXISTS {Q($"IX_{map.Table}_SyncedAtUtc")} ON {Q(map.Table)} (\"SyncedAtUtc\")";
    }

    public static readonly string[] TombstoneStatements =
    {
        $"CREATE TABLE IF NOT EXISTS {Q(TombstonesTable)} (\"EntityType\" TEXT NOT NULL, \"SyncId\" TEXT NOT NULL, " +
        "\"DeletedAtUtc\" TEXT NOT NULL, \"SyncedAtUtc\" TEXT NOT NULL, \"OriginDeviceId\" TEXT, PRIMARY KEY (\"EntityType\", \"SyncId\"))",
        $"CREATE INDEX IF NOT EXISTS \"IX_SyncTombstones_SyncedAtUtc\" ON {Q(TombstonesTable)} (\"SyncedAtUtc\")"
    };

    /// <summary>
    /// La cerca de generación (paso 8.9): la escritura solo procede si la nube sigue en la generación del equipo (último
    /// argumento; null si la nube todavía no tiene). Si otro equipo reemplazó la nube con un respaldo, lo que este venía
    /// subiendo no cae en la nube restaurada.
    /// </summary>
    public static readonly string GenerationFenceSql = $"(SELECT \"Value\" FROM {Q(MetaTable)} WHERE \"Key\" = 'generation') IS ?";

    /// <summary>La generación de la nube; la última instrucción de cada petición de la subida y de cada página de la bajada.</summary>
    public static readonly string GenerationSql = $"SELECT \"Value\" AS \"generation\" FROM {Q(MetaTable)} WHERE \"Key\" = 'generation';";

    /// <summary>
    /// Subida de una lápida (args: EntityType, SyncId, DeletedAtUtc, OriginDeviceId, generación): la nube se queda con el
    /// borrado más nuevo.
    /// </summary>
    public static readonly string TombstoneUpsertSql =
        $"INSERT INTO {Q(TombstonesTable)} (\"EntityType\", \"SyncId\", \"DeletedAtUtc\", \"SyncedAtUtc\", \"OriginDeviceId\") " +
        $"SELECT ?, ?, ?, {TursoSyncService.ServerNowSql}, ? WHERE {GenerationFenceSql} ON CONFLICT(\"EntityType\", \"SyncId\") DO UPDATE SET " +
        "\"DeletedAtUtc\" = excluded.\"DeletedAtUtc\", \"SyncedAtUtc\" = excluded.\"SyncedAtUtc\", \"OriginDeviceId\" = excluded.\"OriginDeviceId\" " +
        $"WHERE excluded.\"DeletedAtUtc\" > {Q(TombstonesTable)}.\"DeletedAtUtc\";";

    /// <summary>Después de subir una lápida (args: SyncId, DeletedAtUtc, generación): borra la fila si no es más nueva que el borrado.</summary>
    public static string DeleteTombstonedRowSql(string table) =>
        $"DELETE FROM {Q(table)} WHERE \"SyncId\" = ? AND \"UpdatedAtUtc\" <= ? AND {GenerationFenceSql};";

    /// <summary>«Reemplazar la nube con este respaldo» (paso 8.9; arg: la generación nueva): vacía una tabla de la nube.</summary>
    public static string DeleteAllFencedSql(string table) => $"DELETE FROM {Q(table)} WHERE {GenerationFenceSql};";

    public static readonly string MetaStatement =
        $"CREATE TABLE IF NOT EXISTS {Q(MetaTable)} (\"Key\" TEXT PRIMARY KEY NOT NULL, \"Value\" TEXT)";

    public const string ClinicProfilesStatement =
        "CREATE TABLE IF NOT EXISTS ClinicProfiles (Id INTEGER PRIMARY KEY, DoctorName TEXT, ProfessionalLicense TEXT, Specialty TEXT, " +
        "University TEXT, ClinicName TEXT, Address TEXT, Phone TEXT, Email TEXT, TaxId TEXT, AdditionalNotes TEXT, UpdatedAtUtc TEXT NOT NULL)";

    /// <summary>
    /// Columnas del latido que agregó el paso 8.10. Todas aceptan nulo: el upsert de una PC v1.17–v1.26 (con sus columnas
    /// explícitas) sigue igual. En una nube que ya tenía la tabla llegan con <c>ALTER TABLE</c>, revisado con
    /// <c>pragma_table_info</c>, porque el ciclo de columnas nuevas solo cubre las tablas del modelo de EF.
    /// </summary>
    public static readonly (string Name, string Type)[] RegisteredDevicesAddedColumns =
    {
        ("Platform", "TEXT"),
        ("SyncProtocol", "INTEGER"),
        ("HandoffProtocol", "INTEGER"),
        ("ContractRevision", "INTEGER"),
        ("LastPushUtc", "TEXT"),
        ("PendingCount", "INTEGER"),
        ("OldestPendingUtc", "TEXT"),
        ("InstanceNonce", "TEXT")
    };

    public static readonly string RegisteredDevicesStatement =
        "CREATE TABLE IF NOT EXISTS RegisteredDevices (DeviceId TEXT PRIMARY KEY, DeviceName TEXT NOT NULL, DeviceRole TEXT NOT NULL, " +
        "AppVersion TEXT NOT NULL, LastSeenUtc TEXT NOT NULL, " +
        string.Join(", ", RegisteredDevicesAddedColumns.Select(c => $"{c.Name} {c.Type}")) + ")";

    public const string DeviceRegistryTable = "DeviceRegistry";

    /// <summary>
    /// Paso 8.10: la autoridad sobre el rol, el código (P1, T1, C1…) y la revocación de cada equipo. Solo la escribe la PC
    /// y sus filas nunca se borran: quitar un equipo pone RevokedAtUtc y RevokedReason, y el siguiente código de cada
    /// letra es el mayor registrado + 1, así que nunca se reutiliza.
    /// </summary>
    public static readonly string[] DeviceRegistryStatements =
    {
        $"CREATE TABLE IF NOT EXISTS {DeviceRegistryTable} (DeviceId TEXT PRIMARY KEY NOT NULL, DeviceCode TEXT NOT NULL, Role TEXT NOT NULL, " +
        "DisplayName TEXT NOT NULL, Model TEXT, PairedAtUtc TEXT NOT NULL, RevokedAtUtc TEXT, RevokedReason TEXT)",
        $"CREATE UNIQUE INDEX IF NOT EXISTS UX_{DeviceRegistryTable}_DeviceCode ON {DeviceRegistryTable} (DeviceCode)"
    };

    private static IEnumerable<string> BuildStatements(IReadOnlyList<TableMap> tables)
    {
        yield return MetaStatement;
        foreach (var map in tables)
        {
            foreach (var statement in TableStatements(map)) yield return statement;
        }
        foreach (var statement in TombstoneStatements) yield return statement;
        yield return ClinicProfilesStatement;
        yield return RegisteredDevicesStatement;
        foreach (var statement in DeviceRegistryStatements) yield return statement;

        // Consultas enviadas entre equipos (no se replica: cada equipo la consulta directamente)
        foreach (var ddl in ConsultationHandoffService.HandoffTableDdl) yield return ddl;
    }
}
