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
}
