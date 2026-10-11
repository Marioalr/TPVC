using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TPVC.Models;

namespace TPVC.Services;

/// <summary>
/// El contrato de sincronización exportado (<c>contracts/sync/schema.json</c>, paso 8.3): lo que la app de Kotlin
/// necesita saber de cada tabla para hablar el protocolo v2 igual que la PC. Sale del mismo <see cref="SyncSchema"/>
/// que usa el motor, así que un cambio en el modelo de EF aparece como diferencia del archivo (la prueba
/// <c>GoldenVectorTests</c> lo detecta y lo regenera con <c>TPVC_WRITE_GOLDEN=1</c>).
/// </summary>
public static class SyncContract
{
    /// <summary>
    /// Revisión del contrato. Sube con cada cambio del esquema, que solo puede ser aditivo (columnas o tablas
    /// nuevas). La PC la escribe en <c>_tpvc_meta.contract</c> y entra a la huella del esquema.
    /// 2 (v1.21.0): <c>Products.UnitCost</c> y <c>Products.CostExcludesVat</c>.
    /// </summary>
    public const int Revision = 2;

    /// <summary>Cómo se interpreta una fecha: todas viajan como texto sin zona.</summary>
    public enum TimeKind
    {
        /// <summary>Hora UTC (marcas de sincronización).</summary>
        Utc,

        /// <summary>Hora local del consultorio (America/Mexico_City), con hora: hoy sale de DateTime.Now.</summary>
        ClinicLocal,

        /// <summary>Solo la fecha del consultorio; la hora siempre es 00:00:00.</summary>
        DateOnly
    }

    private static string KindName(TimeKind kind) => kind switch
    {
        TimeKind.Utc => "utc",
        TimeKind.ClinicLocal => "clinicLocal",
        _ => "dateOnly"
    };

    /// <summary>
    /// El tipo de cada fecha sincronizada. Una columna DateTime nueva sin declarar aquí hace fallar la exportación:
    /// el contrato tiene que decir cómo la interpreta la tablet.
    /// </summary>
    private static readonly Dictionary<(Type, string), TimeKind> TimeKinds = new()
    {
        [(typeof(ClinicalService), nameof(ClinicalService.CreatedAt))] = TimeKind.ClinicLocal,
        [(typeof(InventoryMovement), nameof(InventoryMovement.Date))] = TimeKind.ClinicLocal,
        [(typeof(PatientAppointment), nameof(PatientAppointment.Date))] = TimeKind.DateOnly,
        [(typeof(PatientAppointment), nameof(PatientAppointment.CreatedAt))] = TimeKind.ClinicLocal,
        [(typeof(PatientConsultation), nameof(PatientConsultation.Date))] = TimeKind.ClinicLocal,
        [(typeof(PatientConsultation), nameof(PatientConsultation.StartedAt))] = TimeKind.ClinicLocal,
        [(typeof(PatientConsultation), nameof(PatientConsultation.EndedAt))] = TimeKind.ClinicLocal,
        [(typeof(PatientRecord), nameof(PatientRecord.BirthDate))] = TimeKind.DateOnly,
        [(typeof(PatientRecord), nameof(PatientRecord.LastVisit))] = TimeKind.ClinicLocal,
        [(typeof(PatientRecord), nameof(PatientRecord.NextAppointmentDate))] = TimeKind.DateOnly,
        [(typeof(PatientRecord), nameof(PatientRecord.CreatedAt))] = TimeKind.ClinicLocal,
        [(typeof(Product), nameof(Product.CreatedAt))] = TimeKind.ClinicLocal,
        [(typeof(Product), nameof(Product.UpdatedAt))] = TimeKind.ClinicLocal,
        [(typeof(ProductBatch), nameof(ProductBatch.ExpirationDate))] = TimeKind.DateOnly,
        [(typeof(ProductBatch), nameof(ProductBatch.ReceivedDate))] = TimeKind.ClinicLocal,
        [(typeof(Sale), nameof(Sale.Date))] = TimeKind.ClinicLocal,
        [(typeof(Sale), nameof(Sale.CancelledAt))] = TimeKind.ClinicLocal,
    };

    /// <summary>Columnas que repiten a otra (propiedad con get y set sobre la misma variable): se escriben iguales.</summary>
    private static readonly Dictionary<(Type, string), string> Aliases = new()
    {
        [(typeof(Sale), nameof(Sale.PatientName))] = nameof(Sale.CustomerName),
        [(typeof(SaleItem), nameof(SaleItem.ProductName))] = nameof(SaleItem.Description),
    };

    /// <summary>Lo que un equipo que escribe la tabla tiene que mantener por su cuenta.</summary>
    private static readonly Dictionary<(Type, string), string> Notes = new()
    {
        [(typeof(Product), nameof(Product.CurrentStock))] =
            "Existencias del producto. Hoy viaja como valor y se aplica al bajar; quien registra una venta o un movimiento la actualiza en la misma escritura. Pasa a derivada en el paso 8.20.",
        [(typeof(PatientRecord), nameof(PatientRecord.LastVisit))] =
            "Resumen: la fecha de la última consulta. Quien guarda una nota la actualiza en la misma escritura.",
        [(typeof(PatientRecord), nameof(PatientRecord.NextAppointmentDate))] =
            "Resumen: la fecha de la siguiente cita. Quien agenda la actualiza en la misma escritura.",
    };

    private static readonly string[] Rules =
    {
        "Cada tabla de 'tables' tiene una fila remota por registro, con llave SyncId: GUID en minúsculas con guiones. Los Id enteros locales nunca viajan: una referencia viaja como el SyncId de su principal (columna <Referencia>SyncId).",
        "La subida va tabla por tabla en el orden de 'tables' (los principales antes que sus dependientes) y al final las lápidas. Cada lápida es su upsert seguido de DELETE de la fila con UpdatedAtUtc <= DeletedAtUtc.",
        "UpdatedAtUtc la pone el equipo que escribe (contracts/golden/stamping.json); SyncedAtUtc la pone la nube con su propia hora (strftime('%Y-%m-%d %H:%M:%f','now')); OriginDeviceId es el DeviceId del equipo que escribió.",
        "Gana la versión con UpdatedAtUtc estrictamente más nueva; con marcas iguales se conserva la que ya está (contracts/golden/lww.json).",
        "Valores con la codificación de contracts/golden/values.json. Las fechas viajan como yyyy-MM-dd HH:mm:ss.fffffff, sin zona y sin conversión al escribir; 'timeKind' dice si son UTC, hora local del consultorio (America/Mexico_City) o solo fecha.",
        "Todo SyncId viaja, y entra a un UUIDv5 (contracts/golden/uuid5.json), en minúsculas. En la base local de la PC están en mayúsculas (así guarda EF Core los Guid en SQLite); se comparan como GUID, no como texto.",
        "Una columna obligatoria que llega nula se guarda con su 'nullValue'.",
        "El upsert escribe solo las columnas que el equipo conoce (ON CONFLICT DO UPDATE SET columna por columna, nunca INSERT OR REPLACE), para no borrar las que agregue una versión más nueva.",
        "Un equipo que se actualiza y conoce columnas nuevas de una tabla la baja completa antes de subir nada (la PC lo hace desde el paso 8.7).",
        "Las columnas de 'unique' son únicas solo en la base local; en la nube la única llave es SyncId. Hasta el paso 8.16, un folio repetido que llega se guarda con sufijo -R1, -R2… solo en el equipo que lo recibe y no se sube.",
        "Solo la PC con Windows crea o cambia el esquema de la nube ('ddl') y escribe _tpvc_meta (claves 'schema' = 'fingerprint' y 'contract' = 'contract'). Los cambios del esquema son aditivos.",
        "En esta revisión ninguna columna es derivada ('derived'): todas se aplican al bajar."
    };

    /// <summary>schema.json listo para escribir: sangría de 2, saltos de línea \n y un salto al final.</summary>
    public static string BuildSchemaJson(IModel model) => ToStableJson(BuildSchema(SyncSchema.FromModel(model)));

    /// <summary>JSON con sangría de 2 y saltos \n (igual en Windows y Linux), sin escapar acentos ni "&lt;".</summary>
    public static string ToStableJson(JsonNode node) =>
        node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentSize = 2,
            NewLine = "\n",
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }) + "\n";

    internal static JsonObject BuildSchema(SyncSchema schema)
    {
        var tables = new JsonArray();
        int order = 0;
        foreach (var map in schema.Tables) tables.Add(DescribeTable(map, ++order));

        return new JsonObject
        {
            ["contract"] = Revision,
            ["protocol"] = TursoSyncService.RemoteSchemaVersion,
            ["fingerprint"] = schema.Fingerprint,
            ["generatedBy"] = "tests/TPVC.Tests/GoldenVectorTests.cs desde SyncSchema (TPVC_WRITE_GOLDEN=1 lo regenera)",
            ["rules"] = new JsonArray(Rules.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
            ["tables"] = tables,
            ["tombstones"] = new JsonObject
            {
                ["table"] = SyncSchema.TombstonesTable,
                ["key"] = new JsonArray("EntityType", "SyncId"),
                ["columns"] = new JsonArray(
                    SystemColumn("EntityType", "TEXT", false, "El 'entityType' de la tabla borrada (nombre de la clase de C#)."),
                    SystemColumn("SyncId", "TEXT", false, "SyncId del registro borrado."),
                    SystemColumn("DeletedAtUtc", "TEXT", false, "Marca del borrado, con la misma regla que UpdatedAtUtc.", TimeKind.Utc),
                    SystemColumn("SyncedAtUtc", "TEXT", false, "Hora de la nube al escribir la lápida.", TimeKind.Utc),
                    SystemColumn("OriginDeviceId", "TEXT", true, "DeviceId del equipo que borró.")),
                ["ddl"] = new JsonArray(SyncSchema.TombstoneStatements.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray())
            },
            ["otherTables"] = new JsonArray(
                OtherTable(SyncSchema.MetaTable, "Claves de la nube que escribe solo la PC: 'schema' (la huella) y 'contract' (la revisión).", SyncSchema.MetaStatement),
                OtherTable("ClinicProfiles", "Perfil del consultorio, una sola fila (Id = 1). Gana el UpdatedAtUtc más nuevo; hoy solo lo edita la PC.", SyncSchema.ClinicProfilesStatement),
                OtherTable("RegisteredDevices", "Latido de cada equipo (LastSeenUtc con la hora de la nube). No se replica.", SyncSchema.RegisteredDevicesStatement),
                OtherTable("ConsultationHandoffs", "Consulta entre equipos (docs/TURSO_SYNC_ARCHITECTURE.md §4.5). No se replica: cada equipo la consulta directamente.",
                    ConsultationHandoffService.HandoffTableDdl)),
            ["ddl"] = new JsonArray(schema.Statements.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray())
        };
    }

    private static JsonObject DescribeTable(TableMap map, int order)
    {
        var columns = new JsonArray
        {
            SystemColumn("SyncId", "TEXT", false, "Llave: GUID en minúsculas con guiones."),
            SystemColumn("UpdatedAtUtc", "TEXT", false, "Marca del equipo que escribió (decide qué versión gana).", TimeKind.Utc),
            SystemColumn("SyncedAtUtc", "TEXT", false, "Hora de la nube al escribir la fila; la usa la descarga.", TimeKind.Utc),
            SystemColumn("OriginDeviceId", "TEXT", true, "DeviceId del equipo que escribió; la descarga omite lo propio.")
        };

        foreach (var column in map.Columns) columns.Add(DescribeColumn(map, column));
        foreach (var reference in map.References)
        {
            columns.Add(new JsonObject
            {
                ["name"] = reference.RemoteColumn,
                ["role"] = "reference",
                ["remoteType"] = "TEXT",
                ["nullable"] = !reference.IsRequired,
                ["clrType"] = "Guid"
            });
        }

        var references = new JsonArray();
        foreach (var reference in map.References)
        {
            var principal = reference.PrincipalType;
            references.Add(new JsonObject
            {
                ["column"] = reference.RemoteColumn,
                ["localProperty"] = reference.Property.Name,
                ["principal"] = principal.Name,
                ["required"] = reference.IsRequired,
                ["kind"] = reference.DeleteBehavior == null ? "soft" : "foreignKey",
                ["onDelete"] = DescribeDelete(reference.DeleteBehavior)
            });
        }

        return new JsonObject
        {
            ["order"] = order,
            ["table"] = map.Table,
            ["entityType"] = map.ClrType.Name,
            ["columns"] = columns,
            ["references"] = references,
            ["unique"] = new JsonArray(map.UniqueColumns.Select(u => (JsonNode)new JsonArray(u.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray())).ToArray()),
            ["ddl"] = new JsonArray(SyncSchema.TableStatements(map).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray())
        };
    }

    private static JsonObject DescribeColumn(TableMap map, SyncColumn column)
    {
        var type = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;
        var result = new JsonObject
        {
            ["name"] = column.Name,
            ["role"] = "data",
            ["remoteType"] = column.RemoteType,
            ["nullable"] = column.IsNullable,
            ["clrType"] = ClrTypeName(type),
            ["derived"] = false
        };

        if (type.IsEnum)
        {
            var values = new JsonObject();
            foreach (var name in Enum.GetNames(type)) values[name] = Convert.ToInt64(Enum.Parse(type, name), System.Globalization.CultureInfo.InvariantCulture);
            result["enumType"] = type.Name;
            result["enumValues"] = values;
        }

        if (type == typeof(DateTime))
        {
            if (!TimeKinds.TryGetValue((map.ClrType, column.Property.Name), out var kind))
            {
                throw new InvalidOperationException(
                    $"{map.ClrType.Name}.{column.Property.Name} es una fecha sin timeKind: decláralo en SyncContract.TimeKinds (utc, clinicLocal o dateOnly).");
            }
            result["timeKind"] = KindName(kind);
        }

        if (!column.IsNullable)
        {
            // Lo que guarda un equipo cuando la columna llega nula, codificado como se manda a Turso
            var replacement = TursoSyncService.FromRemoteValue(null, column.ClrType, isNullable: false);
            result["nullValue"] = TursoClient.FormatArg(TursoSyncService.ToRemoteValue(replacement));
        }

        if (Aliases.TryGetValue((map.ClrType, column.Property.Name), out var aliasOf)) result["aliasOf"] = aliasOf;
        if (Notes.TryGetValue((map.ClrType, column.Property.Name), out var note)) result["note"] = note;
        return result;
    }

    private static JsonObject SystemColumn(string name, string remoteType, bool nullable, string note, TimeKind? timeKind = null)
    {
        var column = new JsonObject
        {
            ["name"] = name,
            ["role"] = "system",
            ["remoteType"] = remoteType,
            ["nullable"] = nullable
        };
        if (timeKind is { } kind) column["timeKind"] = KindName(kind);
        column["note"] = note;
        return column;
    }

    private static JsonObject OtherTable(string table, string purpose, params string[] ddl) => new()
    {
        ["table"] = table,
        ["replicated"] = false,
        ["purpose"] = purpose,
        ["ddl"] = new JsonArray(ddl.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray())
    };

    /// <summary>
    /// Qué pasa en la base local al borrar el principal: "cascade" borra los dependientes, "setNull" vacía la
    /// referencia, "restrict" no deja borrar mientras haya dependientes, "none" (referencia suave) la deja tal cual.
    /// ClientSetNull (lo que EF usa por omisión en una referencia opcional) vacía la referencia de los dependientes
    /// que la app tiene cargados y en SQLite queda como NO ACTION: se reporta como "restrict".
    /// </summary>
    private static string DescribeDelete(DeleteBehavior? behavior) => behavior switch
    {
        null => "none",
        DeleteBehavior.Cascade or DeleteBehavior.ClientCascade => "cascade",
        DeleteBehavior.SetNull => "setNull",
        _ => "restrict"
    };

    private static string ClrTypeName(Type type) => type switch
    {
        _ when type == typeof(string) => "string",
        _ when type == typeof(int) => "int",
        _ when type == typeof(long) => "long",
        _ when type == typeof(short) => "short",
        _ when type == typeof(byte) => "byte",
        _ when type == typeof(bool) => "bool",
        _ when type == typeof(decimal) => "decimal",
        _ when type == typeof(double) => "double",
        _ when type == typeof(float) => "float",
        _ when type == typeof(DateTime) => "DateTime",
        _ when type == typeof(Guid) => "Guid",
        _ when type.IsEnum => "enum",
        _ => type.Name
    };
}
