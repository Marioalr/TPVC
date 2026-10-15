using System;
using System.Text.Json.Serialization;

namespace TPVC.Models;

/// <summary>
/// Roles de los dispositivos autorizados en la clínica.
/// El sistema está diseñado para un máximo de 3 dispositivos (Free Tier de Turso):
/// 1. Windows 11 (PC principal del consultorio / caja).
/// 2. Tablet Android (estación de apoyo en consultorio / recepción).
/// 3. Celular Android (administración ejecutiva limitada: métricas, cortes, stock).
/// </summary>
public enum DeviceRole
{
    /// <summary>PC principal con Windows 11. Acceso completo sin restricciones.</summary>
    WindowsDesktop = 1,

    /// <summary>Tablet Android. Consultas médicas, recetas, pacientes, citas y cobro en POS.</summary>
    AndroidTablet = 2,

    /// <summary>Celular Android. Funciones limitadas: Dashboard, Cortes de Caja, Alertas y Existencias.</summary>
    AndroidMobileAdmin = 3
}

/// <summary>
/// Matriz de permisos según el rol del dispositivo (plan de la Fase 8, §4.6). Con un solo token para tablet y celular,
/// Turso no distingue roles: estos límites los hace cumplir la app de cada equipo, con el rol que la PC le asignó en
/// DeviceRegistry.
/// </summary>
public static class DevicePermissions
{
    /// <summary>Ver expedientes (paso 8.10): todos los equipos.</summary>
    public static bool CanViewClinicalRecords(DeviceRole role) => true;

    public static bool CanManageClinicalRecords(DeviceRole role) =>
        role is DeviceRole.WindowsDesktop or DeviceRole.AndroidTablet;

    public static bool CanPrescribeMedications(DeviceRole role) =>
        role is DeviceRole.WindowsDesktop or DeviceRole.AndroidTablet;

    public static bool CanProcessSales(DeviceRole role) =>
        role is DeviceRole.WindowsDesktop or DeviceRole.AndroidTablet;

    /// <summary>Editar inventario y catálogo: productos, precios, conteos y cerrar lotes. La tablet y el celular solo lo ven.</summary>
    public static bool CanManageInventory(DeviceRole role) =>
        role is DeviceRole.WindowsDesktop;

    /// <summary>Registrar entradas de mercancía, un lote con su movimiento de entrada (paso 8.10): la PC y el celular.</summary>
    public static bool CanRegisterStockEntries(DeviceRole role) =>
        role is DeviceRole.WindowsDesktop or DeviceRole.AndroidMobileAdmin;

    /// <summary>Agendar citas (paso 8.10): todos los equipos.</summary>
    public static bool CanScheduleAppointments(DeviceRole role) => true;

    /// <summary>Compartir la receta y el ticket (paso 8.10): todos los equipos.</summary>
    public static bool CanShareDocuments(DeviceRole role) => true;

    public static bool CanViewDashboard(DeviceRole role) => true;

    /// <summary>Corte de caja: oculto en la tablet.</summary>
    public static bool CanViewCashCut(DeviceRole role) =>
        role is DeviceRole.WindowsDesktop or DeviceRole.AndroidMobileAdmin;

    public static bool CanViewStockAlerts(DeviceRole role) => true;

    /// <summary>Configuración, Excel, respaldos, la nube, vincular equipos, perfil y membrete: solo la PC.</summary>
    public static bool CanConfigureSystem(DeviceRole role) =>
        role is DeviceRole.WindowsDesktop;

    /// <summary>Recibe y envía la consulta en curso a otro equipo: el celular no.</summary>
    public static bool CanTakeConsultations(DeviceRole role) =>
        role is DeviceRole.WindowsDesktop or DeviceRole.AndroidTablet;

    /// <summary>La letra del código de cada equipo (P1, T1, C1…; paso 8.10).</summary>
    public static char CodeLetter(DeviceRole role) => role switch
    {
        DeviceRole.AndroidTablet => 'T',
        DeviceRole.AndroidMobileAdmin => 'C',
        _ => 'P'
    };

    public static string GetRoleDescription(DeviceRole role) => role switch
    {
        DeviceRole.WindowsDesktop => "Acceso completo: consultas, recetas, cobro, inventario, configuración y la nube.",
        DeviceRole.AndroidTablet => "Consulta y recepción: expedientes, notas, recetas, citas y cobro. Ve el inventario sin editarlo.",
        DeviceRole.AndroidMobileAdmin => "Ve expedientes, agenda citas, revisa existencias, registra entradas de mercancía y ve el corte de caja.",
        _ => ""
    };
}

/// <summary>
/// Estado actual de la sincronización con Turso Cloud.
/// </summary>
public enum TursoSyncStatus
{
    NotConfigured,
    Idle,
    Syncing,
    Success,
    Offline,
    Error,

    /// <summary>La sincronización automática está detenida: cuota de Turso agotada o freno de consumo.</summary>
    Paused
}

/// <summary>Por qué está detenida la sincronización automática.</summary>
public enum TursoSyncPauseReason
{
    None,

    /// <summary>Turso respondió BLOCKED: se reintenta sola cada hora.</summary>
    QuotaExhausted,

    /// <summary>El freno de consumo vio un uso anormal: sigue detenida hasta "Reanudar".</summary>
    ConsumptionBrake,

    /// <summary>
    /// Turso rechazó el token (HTTP 401, o 400 de un token mal copiado; paso 8.15): sigue detenida hasta pegar otro. No se
    /// borra ni se aparta nada.
    /// </summary>
    TokenRejected
}

/// <summary>
/// Freno de consumo: protege la cuota del plan gratuito de un ciclo descontrolado (por ejemplo, una
/// descarga completa repetida cada pocos segundos). El uso normal queda ~100 veces por debajo de estos topes.
/// Las cifras son las del estimador local (<see cref="TursoSettings.DailyRowsRead"/>), no las del panel de Turso.
/// </summary>
public static class TursoConsumptionBrake
{
    public const int MaxFullPullsPerDay = 2;
    public const long MaxRowsReadPerDay = 3_000_000L;
    public const long MaxRowsWrittenPerDay = 30_000L;

    /// <summary>
    /// Freno de bytes de los resultados de laboratorio en la PC (paso 8.12b), por día: alcanza para archivar los 2 GB de la
    /// nube en un día después de unirse. Turso no cobra el tráfico; esto frena un ciclo de bajadas descontrolado. Abrir un
    /// resultado a pedido del médico no se frena. En Android: 300 MB de bajada y 150 MB de subida.
    /// </summary>
    public const long MaxLabBytesDownPerDay = 2_500_000_000L;
    public const long MaxLabBytesUpPerDay = 500_000_000L;

    /// <summary>El freno de bytes de la tablet y el celular (datos móviles); va en el contrato para Kotlin.</summary>
    public const long AndroidMaxLabBytesDownPerDay = 300_000_000L;
    public const long AndroidMaxLabBytesUpPerDay = 150_000_000L;

    /// <summary>Tras BLOCKED, la sincronización automática espera esto antes de reintentar.</summary>
    public static readonly TimeSpan QuotaRetryInterval = TimeSpan.FromHours(1);

    /// <summary>Para el estado (barra de estado y resumen de Configuración).</summary>
    public const string PausedTitle = "Sincronización pausada: consumo anormal";

    /// <summary>Para el aviso de Nube y dispositivos, debajo del título.</summary>
    public const string PausedDetail =
        "Este equipo usó hoy mucho más de lo normal de la cuota de Turso y la sincronización automática se detuvo " +
        "para no agotarla. Revise que todo esté en orden y pulse «Reanudar».";

    /// <summary>Para los errores (título y detalle).</summary>
    public const string PausedMessage = PausedTitle + ". " + PausedDetail;
}

/// <summary>
/// Configuración de conexión y sincronización con Turso.
/// </summary>
public class TursoSettings
{
    public bool IsSyncEnabled { get; set; } = false;

    /// <summary>URL del endpoint de Turso (ej. https://tpvc-clinica.turso.io o libsql://...)</summary>
    public string DatabaseUrl { get; set; } = string.Empty;

    /// <summary>Token de autenticación Bearer de Turso.</summary>
    [JsonIgnore]
    public string AuthToken { get; set; } = string.Empty;

    /// <summary>Token cifrado para almacenamiento seguro en disco.</summary>
    public string? ProtectedAuthToken { get; set; }

    /// <summary>
    /// Identificador único persistente de este dispositivo. La descarga omite las filas que subió este DeviceId, así que
    /// dos instalaciones con el mismo nunca verían las filas de la otra: se cambia por uno nuevo si la configuración se
    /// copió a otra computadora (paso 8.10).
    /// </summary>
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Huella de la computadora donde se creó este <see cref="DeviceId"/> (SHA-256 del MachineGuid de Windows; null antes
    /// de v1.27.0). Si cambia, la carpeta de TPVC se copió a otra máquina y se genera un DeviceId nuevo.
    /// </summary>
    public string? MachineFingerprint { get; set; }

    /// <summary>
    /// Por qué este equipo tomó un DeviceId nuevo (configuración copiada de otra computadora u otra instalación con el
    /// mismo); se muestra en Nube y dispositivos hasta pulsar «Entendido». null si no hay aviso.
    /// </summary>
    public string? DeviceIdNotice { get; set; }

    /// <summary>El código de este equipo en DeviceRegistry (P1, P2…; paso 8.10), según la última sincronización.</summary>
    public string? DeviceCode { get; set; }

    /// <summary>Nombre asignado al dispositivo.</summary>
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>
    /// Rol de este dispositivo. En la PC siempre es <see cref="DeviceRole.WindowsDesktop"/> (paso 8.2: se quitó
    /// el selector); el de la tablet y el celular lo asignará la PC al vincularlos.
    /// </summary>
    public DeviceRole DeviceRole { get; set; } = DeviceRole.WindowsDesktop;

    /// <summary>Sincronizar automáticamente en segundo plano tras cambios locales.</summary>
    public bool AutoSyncOnChanges { get; set; } = true;

    /// <summary>Sincronizar automáticamente al iniciar la aplicación.</summary>
    public bool AutoSyncOnStartup { get; set; } = true;

    /// <summary>Marca de tiempo UTC de la última sincronización completada exitosamente.</summary>
    public DateTime? LastSyncUtc { get; set; }

    /// <summary>
    /// Marca del reloj del servidor Turso hasta la que ya se descargaron cambios
    /// (se compara contra la columna SyncedAtUtc que asigna el propio servidor,
    /// inmune a diferencias de reloj entre dispositivos).
    /// </summary>
    public string? LastPulledServerWatermark { get; set; }

    /// <summary>Filas enviadas acumuladas en el mes actual.</summary>
    public long MonthlyRowsWritten { get; set; }

    /// <summary>Filas recibidas/leídas acumuladas en el mes actual.</summary>
    public long MonthlyRowsRead { get; set; }

    /// <summary>Mes y año de las métricas registradas (formato YYYY-MM).</summary>
    public string MonthlyMetricsPeriod { get; set; } = DateTime.UtcNow.ToString("yyyy-MM");

    /// <summary>Día (hora del consultorio, yyyy-MM-dd) de los contadores diarios del freno de consumo.</summary>
    public string? DailyMetricsDay { get; set; }

    /// <summary>Filas leídas estimadas hoy (desde la medianoche o desde el último "Reanudar").</summary>
    public long DailyRowsRead { get; set; }

    /// <summary>Filas escritas estimadas hoy (desde la medianoche o desde el último "Reanudar").</summary>
    public long DailyRowsWritten { get; set; }

    /// <summary>Descargas completas de hoy (sincronización completa o primera descarga).</summary>
    public int DailyFullPulls { get; set; }

    /// <summary>Cuándo se activó el freno de consumo; null = no está activo. Se conserva al cerrar el programa.</summary>
    public DateTime? ConsumptionBrakeTrippedUtc { get; set; }

    /// <summary>Bytes de resultados de laboratorio que este equipo subió hoy (lo que viaja: base64), para su freno.</summary>
    public long DailyLabBytesUp { get; set; }

    /// <summary>Bytes de resultados de laboratorio que este equipo bajó hoy (lo que viaja: base64), para su freno.</summary>
    public long DailyLabBytesDown { get; set; }

    /// <summary>Lo que había en la nube de laboratorio la última vez que se midió (paso 8.12b); null si nunca.</summary>
    public LabCloudSummary? LabCloud { get; set; }

    // Paso 8.15. Del token solo se guarda su huella (TursoSettingsService.TokenHash), nunca el token

    /// <summary>La huella del último token con el que se sincronizó bien.</summary>
    public string? WorkingTokenHash { get; set; }

    /// <summary>La huella del token que Turso rechazó; mientras sea el de ahora, la sincronización automática no intenta.</summary>
    public string? RejectedTokenHash { get; set; }

    /// <summary>Cuándo rechazó Turso ese token.</summary>
    public DateTime? TokenRejectedUtc { get; set; }

    /// <summary>
    /// La huella del token que se está reemplazando porque se anularon los tokens (un 401 a uno que funcionaba, o «Perdí este
    /// equipo»). La primera sincronización con otro token lo anota en la nube (_tpvc_meta.tokensRotatedAtUtc) y lo borra.
    /// </summary>
    public string? TokenToRotateHash { get; set; }

    /// <summary>
    /// Desde cuándo se espera esa rotación: la última sincronización buena con el token anulado (o cuándo se marcó el equipo
    /// perdido). Una marca de la nube más nueva es la misma rotación, anotada por otro equipo: no se mueve.
    /// </summary>
    public DateTime? TokenRotationAfterUtc { get; set; }
}

/// <summary>
/// El uso de la nube por los resultados de laboratorio (paso 8.12b): los archivos sin desalojar y su suma, y el espacio
/// de toda la base según SQLite ((page_count − freelist_count) × page_size, lo mismo que cuenta Turso con dbstat; V20).
/// </summary>
public sealed record LabCloudSummary(int Files, long Bytes, long? CloudSpaceBytes, DateTime MeasuredUtc, string? MeasuredDay = null);

/// <summary>
/// Resumen del consumo de cuota gratuita en Turso (estimación de este equipo; la cifra real está en el panel de Turso).
/// </summary>
public class TursoQuotaSummary
{
    // Límites del plan gratuito de Turso (https://turso.tech/pricing, consultado el 2026-10-09)
    public const long FreeTierMonthlyRowsReadLimit = 500_000_000L;  // 500 millones
    public const long FreeTierMonthlyRowsWriteLimit = 10_000_000L;  // 10 millones
    public const double FreeTierStorageMegabytes = 5_000;           // 5 GB

    public long RowsWrittenThisMonth { get; set; }
    public long RowsReadThisMonth { get; set; }
    public double EstimatedStorageMegabytes { get; set; }

    public long RowsReadToday { get; set; }
    public long RowsWrittenToday { get; set; }
    public int FullPullsToday { get; set; }

    public double PercentageWritesUsed => (double)RowsWrittenThisMonth / FreeTierMonthlyRowsWriteLimit * 100.0;
    public double PercentageReadsUsed => (double)RowsReadThisMonth / FreeTierMonthlyRowsReadLimit * 100.0;

    public string FreeTierStatusDescription =>
        $"Este mes, este equipo lleva {PercentageReadsUsed:0.###}% de las lecturas y {PercentageWritesUsed:0.###}% de las escrituras del plan gratuito (estimado).";
}

/// <summary>
/// Resultado detallado de un ciclo de sincronización.
/// </summary>
public class TursoSyncResult
{
    public bool Success { get; set; }
    public int RowsPushed { get; set; }
    public int RowsPulled { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Filas apartadas al terminar (se reintentan en cada sincronización).</summary>
    public int ParkedRows { get; set; }

    /// <summary>El reporte de la «Sincronización completa» (null en las demás).</summary>
    public TursoFullSyncReport? FullSyncReport { get; set; }

    /// <summary>Paso 8.23: citas empalmadas y posibles expedientes duplicados con lo que llegó de otro equipo.</summary>
    public TPVC.Services.SyncArrivals Arrivals { get; set; } = TPVC.Services.SyncArrivals.None;
}

/// <summary>Filas de una tabla aquí y en la nube.</summary>
public sealed record TursoTableCount(string Table, long Local, long Remote);

/// <summary>
/// Reporte de la «Sincronización completa» (paso 8.7, A6): filas apartadas, conteo local contra remoto por tabla y
/// tablas heredadas de v1.8 (legacy_v1_*). Solo si sale limpio se escribe la identidad de la nube (clinicId,
/// fullUploadAtUtc…); sin eso no se podrá vincular la tablet ni el celular.
/// </summary>
public sealed class TursoFullSyncReport
{
    public int ParkedRows { get; init; }
    public IReadOnlyList<TursoTableCount> Tables { get; init; } = Array.Empty<TursoTableCount>();
    public IReadOnlyList<string> LegacyTables { get; init; } = Array.Empty<string>();

    /// <summary>La identidad de la nube que quedó escrita (null si el reporte no salió limpio).</summary>
    public string? ClinicId { get; set; }

    /// <summary>Lecturas que costó el reporte (los conteos recorren cada tabla una vez).</summary>
    public long RowsRead { get; init; }

    public IEnumerable<TursoTableCount> Mismatches => Tables.Where(t => t.Local != t.Remote);

    /// <summary>Limpio: nada apartado y las mismas filas aquí y en la nube. Las tablas heredadas solo se avisan.</summary>
    public bool IsClean => ParkedRows == 0 && !Mismatches.Any();

    public string Summary
    {
        get
        {
            var lines = new List<string>
            {
                IsClean
                    ? $"✓ La nube y esta computadora tienen lo mismo ({Tables.Sum(t => t.Local):N0} registros en {Tables.Count} tablas)."
                    : "La sincronización completa terminó con diferencias; la nube todavía no queda verificada:"
            };
            if (ParkedRows > 0) lines.Add($"• {ParkedRows} registros apartados (no se pudieron subir o aplicar).");
            foreach (var t in Mismatches) lines.Add($"• {t.Table}: {t.Local:N0} aquí y {t.Remote:N0} en la nube.");
            if (LegacyTables.Count > 0) lines.Add($"• Tablas de la versión 1.8 que siguen en la nube: {string.Join(", ", LegacyTables)}.");
            return string.Join("\n", lines);
        }
    }
}
