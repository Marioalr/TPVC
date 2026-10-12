using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using TPVC.Models;

// El SQL dinámico de este archivo solo usa nombres de tablas/columnas del modelo de EF
// y SQL generado por el proveedor; nunca datos capturados por el usuario.
#pragma warning disable EF1002, EF1003

namespace TPVC.Data;

public static partial class DatabaseInitializer
{
    /// <summary>
    /// Migración aditiva: compara el modelo de EF con el esquema real de SQLite y crea
    /// tablas, columnas e índices faltantes. Reemplaza la lista manual de "ALTER TABLE"
    /// que se ejecutaba (y fallaba con excepción) en cada arranque, y cubre automáticamente
    /// cualquier columna nueva que se agregue a los modelos en el futuro.
    /// </summary>
    public static void EnsureSchema(AppDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        bool openedHere = connection.State != ConnectionState.Open;
        if (openedHere) context.Database.OpenConnection();

        try
        {
            MigrateModelTables(context, connection);

            // Fuera del atajo de user_version: un respaldo viejo restaurado también recibe los triggers, y un
            // trigger cuya definición cambió (una columna nueva) se vuelve a crear
            SyncChangeLog.EnsureInstalled(context);

            // Paso 8.19: la foto de las diferencias que la base ya traía entre sus existencias y el libro de movimientos,
            // una sola vez (también en un respaldo anterior que se restaura): la verificación solo reporta lo nuevo
            Services.StockLedger.EnsureBaseline(context);
        }
        finally
        {
            if (openedHere) context.Database.CloseConnection();
        }
    }

    private static void MigrateModelTables(AppDbContext context, DbConnection connection)
    {
        // Atajo: si la huella del modelo coincide con la guardada, el esquema ya está al día
        int fingerprint = ComputeModelFingerprint(context.Model);
        if (ReadUserVersion(connection) == fingerprint)
        {
            return;
        }

        var model = context.GetService<IDesignTimeModel>().Model;
        var differ = context.GetService<IMigrationsModelDiffer>();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var desired = differ.GetDifferences(null, model.GetRelationalModel());

        var existingTables = QueryNames(connection, "SELECT name FROM sqlite_master WHERE type = 'table'");
        var pending = new List<MigrationOperation>();

        foreach (var create in desired.OfType<CreateTableOperation>())
        {
            if (!existingTables.Contains(create.Name))
            {
                pending.Add(create);
                continue;
            }

            var columns = QueryNames(connection, $"SELECT name FROM pragma_table_info('{create.Name}')");
            foreach (var column in create.Columns.Where(c => !columns.Contains(c.Name)))
            {
                pending.Add(new AddColumnOperation
                {
                    Table = create.Name,
                    Name = column.Name,
                    ClrType = column.ClrType,
                    ColumnType = column.ColumnType,
                    IsNullable = column.IsNullable,
                    DefaultValue = column.IsNullable ? null : (column.DefaultValue ?? DefaultValueFor(column.ClrType))
                });
            }
        }

        Execute(context, generator, model, pending);
        BackfillSyncColumns(context, model);

        var existingIndexes = QueryNames(connection, "SELECT name FROM sqlite_master WHERE type = 'index'");
        var uniqueIndexes = QueryNames(connection,
            "SELECT il.name FROM sqlite_master AS m, pragma_index_list(m.name) AS il WHERE m.type = 'table' AND il.\"unique\" = 1");
        var indexOperations = new List<MigrationOperation>();
        foreach (var index in desired.OfType<CreateIndexOperation>())
        {
            if (existingIndexes.Contains(index.Name))
            {
                // Un índice que dejó de ser único (paso 8.16: RecordNumber y Sale.Folio) se vuelve a crear. Al revés no: con
                // filas repetidas fallaría al abrir
                if (index.IsUnique || !uniqueIndexes.Contains(index.Name)) continue;
                indexOperations.Add(new DropIndexOperation { Name = index.Name, Table = index.Table });
            }
            indexOperations.Add(index);
        }
        Execute(context, generator, model, indexOperations);

        context.Database.ExecuteSqlRaw($"PRAGMA user_version = {fingerprint};");
    }

    private static void Execute(AppDbContext context, IMigrationsSqlGenerator generator, IModel model, IReadOnlyList<MigrationOperation> operations)
    {
        if (operations.Count == 0) return;
        foreach (var command in generator.Generate(operations, model))
        {
            context.Database.ExecuteSqlRaw(command.CommandText);
        }
    }

    /// <summary>
    /// Los registros creados antes de v1.5.0 no tienen SyncId/UpdatedAtUtc: se les asigna
    /// un UUID v4 aleatorio y la fecha actual para que puedan sincronizarse.
    /// </summary>
    private static void BackfillSyncColumns(AppDbContext context, IModel model)
    {
        const string uuidExpr =
            "upper(hex(randomblob(4))) || '-' || upper(hex(randomblob(2))) || '-4' || substr(upper(hex(randomblob(2))), 2) || '-' || " +
            "substr('89AB', 1 + (abs(random()) % 4), 1) || substr(upper(hex(randomblob(2))), 2) || '-' || upper(hex(randomblob(6)))";

        foreach (var entityType in model.GetEntityTypes().Where(t => typeof(ISyncEntity).IsAssignableFrom(t.ClrType)))
        {
            var table = entityType.GetTableName();
            if (string.IsNullOrEmpty(table)) continue;

            context.Database.ExecuteSqlRaw(
                $"UPDATE \"{table}\" SET \"SyncId\" = {uuidExpr} " +
                $"WHERE \"SyncId\" IS NULL OR \"SyncId\" = '' OR \"SyncId\" = '{Guid.Empty.ToString().ToUpperInvariant()}';");

            context.Database.ExecuteSqlRaw(
                $"UPDATE \"{table}\" SET \"UpdatedAtUtc\" = strftime('%Y-%m-%d %H:%M:%f', 'now') " +
                $"WHERE \"UpdatedAtUtc\" IS NULL OR \"UpdatedAtUtc\" = '' OR \"UpdatedAtUtc\" LIKE '0001-01-01%';");
        }
    }

    /// <summary>
    /// Huella estable (FNV-1a) de tablas, columnas e índices del modelo. Cambia en cuanto se
    /// agrega una entidad, propiedad o índice, lo que dispara la migración aditiva.
    /// </summary>
    private static int ComputeModelFingerprint(IModel model)
    {
        var parts = new List<string>();
        foreach (var entityType in model.GetEntityTypes())
        {
            var table = entityType.GetTableName();
            if (table == null) continue;
            var storeObject = StoreObjectIdentifier.Table(table, entityType.GetSchema());
            foreach (var property in entityType.GetProperties())
            {
                parts.Add($"{table}.{property.GetColumnName(storeObject)}");
            }
            foreach (var index in entityType.GetIndexes())
            {
                // Con "!" si es único: así un índice que cambia de único a normal también dispara la migración
                parts.Add($"{table}#{index.GetDatabaseName(storeObject)}{(index.IsUnique ? "!" : string.Empty)}");
            }
        }
        parts.Sort(StringComparer.Ordinal);

        uint hash = 2166136261;
        foreach (var c in string.Join("|", parts))
        {
            hash = (hash ^ c) * 16777619;
        }
        return (int)(hash & 0x7FFFFFFF);
    }

    private static long ReadUserVersion(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static object? DefaultValueFor(Type clrType)
    {
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (type == typeof(string)) return string.Empty;
        if (type == typeof(bool)) return false;
        if (type == typeof(decimal)) return 0m;
        if (type == typeof(Guid)) return Guid.Empty;
        if (type == typeof(DateTime)) return DateTime.MinValue;
        if (type.IsEnum) return 0;
        if (type.IsPrimitive) return Convert.ChangeType(0, type);
        return null;
    }

    private static HashSet<string> QueryNames(DbConnection connection, string sql)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0)) names.Add(reader.GetString(0));
        }
        return names;
    }
}
