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
/// Matriz de permisos según el rol del dispositivo.
/// </summary>
public static class DevicePermissions
{
    public static bool CanManageClinicalRecords(DeviceRole role) =>
        role is DeviceRole.WindowsDesktop or DeviceRole.AndroidTablet;

    public static bool CanPrescribeMedications(DeviceRole role) =>
        role is DeviceRole.WindowsDesktop or DeviceRole.AndroidTablet;

    public static bool CanProcessSales(DeviceRole role) =>
        role is DeviceRole.WindowsDesktop or DeviceRole.AndroidTablet;

    public static bool CanManageInventory(DeviceRole role) =>
        role is DeviceRole.WindowsDesktop;

    public static bool CanViewDashboard(DeviceRole role) => true;

    public static bool CanViewCashCut(DeviceRole role) =>
        role is DeviceRole.WindowsDesktop or DeviceRole.AndroidMobileAdmin;

    public static bool CanViewStockAlerts(DeviceRole role) => true;

    public static bool CanConfigureSystem(DeviceRole role) =>
        role is DeviceRole.WindowsDesktop;

    public static string GetRoleDescription(DeviceRole role) => role switch
    {
        DeviceRole.WindowsDesktop => "Control total: Consultas NOM-004, Recetas, Cobro POS, Inventario, Lotes, Kardex y Nube.",
        DeviceRole.AndroidTablet => "Uso clínico y operativo: Expedientes, Notas médicas, Recetas, Citas y Cobro.",
        DeviceRole.AndroidMobileAdmin => "Administración ejecutiva: Métricas del día, Cortes de caja, Consulta de existencias y Alertas.",
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
    Error
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

    /// <summary>Identificador único persistente de este dispositivo.</summary>
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Nombre asignado al dispositivo.</summary>
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>Rol de este dispositivo.</summary>
    public DeviceRole DeviceRole { get; set; } = DeviceRole.WindowsDesktop;

    /// <summary>Sincronizar automáticamente en segundo plano tras cambios locales.</summary>
    public bool AutoSyncOnChanges { get; set; } = true;

    /// <summary>Sincronizar automáticamente al iniciar la aplicación.</summary>
    public bool AutoSyncOnStartup { get; set; } = true;

    /// <summary>Marca de tiempo UTC de la última sincronización completada exitosamente.</summary>
    public DateTime? LastSyncUtc { get; set; }

    /// <summary>
    /// Hora local (UTC) hasta la que ya se subieron cambios de este dispositivo.
    /// null = nunca se ha subido nada (la siguiente sincronización sube todo).
    /// </summary>
    public DateTime? LastPushedLocalUtc { get; set; }

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
}

/// <summary>
/// Resumen del consumo de cuota gratuita en Turso.
/// </summary>
public class TursoQuotaSummary
{
    // Límites oficiales del plan gratuito de Turso
    public const long FreeTierMonthlyRowsReadLimit = 1_000_000_000L; // 1,000 Millones
    public const long FreeTierMonthlyRowsWriteLimit = 25_000_000L;    // 25 Millones

    public long RowsWrittenThisMonth { get; set; }
    public long RowsReadThisMonth { get; set; }
    public double EstimatedStorageMegabytes { get; set; }

    public double PercentageWritesUsed => (double)RowsWrittenThisMonth / FreeTierMonthlyRowsWriteLimit * 100.0;
    public double PercentageReadsUsed => (double)RowsReadThisMonth / FreeTierMonthlyRowsReadLimit * 100.0;

    public string FreeTierStatusDescription =>
        $"100% Gratuito: {PercentageWritesUsed:F4}% de escrituras y {PercentageReadsUsed:F4}% de lecturas usadas de la cuota mensual.";
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
}
