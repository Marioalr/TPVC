using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace TPVC.Data;

/// <summary>
/// Datos de la propia base, no del consultorio (tabla "DbMeta": clave → texto). Viven dentro de consultorio.db:
/// un respaldo restaurado trae los suyos y nunca se cruzan con los de otra base. Plan de la Fase 8, §2.3; los
/// pasos siguientes agregan claves (clinicId, generation, la revisión del contrato por tabla…).
/// </summary>
public static class DbMeta
{
    public const string Table = "DbMeta";

    /// <summary>Identifica este archivo de base. Cambia al restaurar un respaldo.</summary>
    public const string DbInstanceId = "DbInstanceId";

    /// <summary>
    /// El último Seq de <see cref="SyncChangeLog"/> que ya subió a la nube. Sin la clave, la siguiente subida
    /// manda todo una vez.
    /// </summary>
    public const string LastPushedSeq = "LastPushedSeq";

    /// <summary>
    /// Desfase del reloj de este equipo con la nube, en milisegundos. Lo usan los triggers para marcar las lápidas;
    /// lo escribe el paso 8.7 y hasta entonces no existe (= 0).
    /// </summary>
    public const string ClockOffsetMs = "ClockOffsetMs";

    /// <summary>
    /// Cursor de bajada de cada tabla (paso 8.6): "PullCursor:Products" → "SyncedAtUtc|SyncId" de la última fila
    /// aplicada, sin pasar de la marca de agua de la sincronización. Un corte a media descarga sigue desde ahí.
    /// </summary>
    public const string PullCursorPrefix = "PullCursor:";

    /// <summary>
    /// Columnas que tenía cada tabla la última vez que se bajó (paso 8.7): "PullColumns:Patients" → nombres remotos
    /// separados por comas. Si el modelo conoce una columna que no está aquí, la tabla se baja completa antes de subir.
    /// </summary>
    public const string PullColumnsPrefix = "PullColumns:";

    /// <summary>La identidad de la nube a la que pertenece esta base (_tpvc_meta.clinicId, paso 8.7).</summary>
    public const string ClinicId = "clinicId";

    /// <summary>
    /// "1" si la base se unió a una nube («Unirse», «Recuperar» o «Reemplazar con la nube», paso 8.8): ya no siembra
    /// nada nunca, ni siquiera a media descarga.
    /// </summary>
    public const string CloudJoined = "CloudJoined";

    /// <summary>
    /// "1" desde «Unirse» o «Reemplazar con la nube» hasta que termina la bajada completa (paso 8.8): mientras tanto la
    /// bajada no omite las filas que subió este mismo equipo, que aquí ya no están. Un corte la deja puesta.
    /// </summary>
    public const string CloudDownloadPending = "CloudDownloadPending";

    /// <summary>
    /// La generación de la nube con la que está esta base (_tpvc_meta.generation, paso 8.9): va en la cerca de cada
    /// escritura. Si la nube cambia de generación (otra PC la reemplazó con un respaldo), esta base guarda lo que no subió,
    /// espera a que la nube esté completa y la vuelve a bajar.
    /// </summary>
    public const string Generation = "generation";

    /// <summary>
    /// "Reemplazar la nube con este respaldo" en curso (paso 8.9): la generación nueva. Si TPVC se cierra a la mitad, la
    /// siguiente sincronización lo continúa.
    /// </summary>
    public const string ReplacingCloud = "ReplacingCloud";

    /// <summary>Ruta del archivo con lo que la nube tenía y el respaldo no (o "-" si no había nada), del reemplazo en curso.</summary>
    public const string ReplaceLostFile = "ReplaceLostFile";

    /// <summary>
    /// "1" desde que se restauró un respaldo con la nube configurada hasta que se elige «Reemplazar la nube con este
    /// respaldo» o «Desconectar esta PC de la nube» (paso 8.9). Mientras tanto no se sincroniza.
    /// </summary>
    public const string RestoredWithCloud = "RestoredWithCloud";

    /// <summary>
    /// "1" cuando este equipo tomó un DeviceId nuevo porque otra instalación usaba el mismo (paso 8.10): la siguiente
    /// sincronización quita los cursores de bajada y baja todo una vez, porque mientras compartían el DeviceId cada una
    /// omitía las filas de la otra.
    /// </summary>
    public const string RedownloadAll = "RedownloadAll";

    internal const string CreateTableSql = "CREATE TABLE IF NOT EXISTS \"DbMeta\" (\"Key\" TEXT PRIMARY KEY NOT NULL, \"Value\" TEXT)";

    private const string SelectSql = "SELECT \"Value\" FROM \"DbMeta\" WHERE \"Key\" = {0}";
    private const string UpsertSql =
        "INSERT INTO \"DbMeta\" (\"Key\", \"Value\") VALUES ({0}, {1}) ON CONFLICT(\"Key\") DO UPDATE SET \"Value\" = excluded.\"Value\"";
    private const string DeleteSql = "DELETE FROM \"DbMeta\" WHERE \"Key\" = {0}";

    public static string? Get(AppDbContext db, string key) =>
        db.Database.SqlQueryRaw<string>(SelectSql, key).AsEnumerable().FirstOrDefault();

    public static async Task<string?> GetAsync(AppDbContext db, string key, CancellationToken ct = default) =>
        (await db.Database.SqlQueryRaw<string>(SelectSql, key).ToListAsync(ct)).FirstOrDefault();

    public static void Set(AppDbContext db, string key, string value) => db.Database.ExecuteSqlRaw(UpsertSql, key, value);

    public static Task SetAsync(AppDbContext db, string key, string value, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(UpsertSql, new object[] { key, value }, ct);

    public static void Remove(AppDbContext db, string key) => db.Database.ExecuteSqlRaw(DeleteSql, key);

    /// <summary>Las claves que empiezan con <paramref name="prefix"/>, con su valor.</summary>
    public static async Task<Dictionary<string, string>> GetByPrefixAsync(AppDbContext db, string prefix, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<Entry>(
                "SELECT \"Key\", \"Value\" FROM \"DbMeta\" WHERE substr(\"Key\", 1, {0}) = {1}", prefix.Length, prefix)
            .ToListAsync(ct);
        return rows.Where(r => r.Value != null).ToDictionary(r => r.Key, r => r.Value!, StringComparer.Ordinal);
    }

    public static void RemoveByPrefix(AppDbContext db, string prefix) =>
        db.Database.ExecuteSqlRaw("DELETE FROM \"DbMeta\" WHERE substr(\"Key\", 1, {0}) = {1}", prefix.Length, prefix);

    internal sealed class Entry
    {
        public string Key { get; set; } = string.Empty;
        public string? Value { get; set; }
    }
}
