using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TPVC.Models;

namespace TPVC.Services;

/// <summary>
/// Gestiona la persistencia segura de la configuración de Turso Cloud, el cifrado del token y los
/// contadores de consumo (del mes, para la pantalla, y del día, para el freno de consumo).
/// </summary>
public class TursoSettingsService
{
    private readonly string _configFilePath;
    private readonly object _usageLock = new();
    public TursoSettings CurrentSettings { get; private set; } = new();

    public event Action? SettingsChanged;

    /// <summary>Reloj de los contadores del mes y del día (reemplazable en pruebas).</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Huella de esta computadora (paso 8.10); null si no se puede leer, y entonces no se compara.</summary>
    private readonly Func<string?> _machineFingerprint;

    /// <param name="clock">Reloj de los contadores. Va en el constructor porque la carga ya reinicia los del día y del
    /// mes: asignado después, la carga los compararía con la fecha real.</param>
    /// <param name="machineFingerprint">Huella de la computadora (reemplazable en pruebas); por omisión
    /// <see cref="ReadMachineFingerprint"/>.</param>
    public TursoSettingsService(string? customPath = null, TimeProvider? clock = null, Func<string?>? machineFingerprint = null)
    {
        Clock = clock ?? TimeProvider.System;
        _machineFingerprint = machineFingerprint ?? ReadMachineFingerprint;
        _configFilePath = !string.IsNullOrEmpty(customPath)
            ? customPath
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TPVC",
                "turso_config.json"
            );

        LoadSettings();
    }

    public void LoadSettings()
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                var json = File.ReadAllText(_configFilePath);
                var loaded = JsonSerializer.Deserialize<TursoSettings>(json);
                if (loaded != null)
                {
                    CurrentSettings = loaded;
                    bool tokenUnreadable = false;
                    if (!string.IsNullOrEmpty(CurrentSettings.ProtectedAuthToken))
                    {
                        CurrentSettings.AuthToken = UnprotectToken(CurrentSettings.ProtectedAuthToken);
                        tokenUnreadable = CurrentSettings.AuthToken.Length == 0;
                    }

                    // La PC siempre es WindowsDesktop: antes había un selector y alguien pudo elegir otro rol
                    CurrentSettings.DeviceRole = DeviceRole.WindowsDesktop;
                    EnsureMonthlyMetricsPeriod();
                    EnsureDeviceIdentity(tokenUnreadable);
                    return;
                }
            }

            CurrentSettings = new TursoSettings { MachineFingerprint = _machineFingerprint() };
            EnsureMonthlyMetricsPeriod();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al cargar la configuración de Turso", ex);
            CurrentSettings = new TursoSettings();
        }
    }

    public void SaveSettings(TursoSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        if (!string.IsNullOrEmpty(settings.AuthToken))
        {
            settings.ProtectedAuthToken = ProtectToken(settings.AuthToken);
        }
        else
        {
            settings.ProtectedAuthToken = null;
        }

        CurrentSettings = settings;

        try
        {
            var dir = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(settings, options);
            File.WriteAllText(_configFilePath, json);

            SettingsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al guardar la configuración de Turso", ex);
            throw;
        }
    }

    /// <summary>
    /// Paso 8.10: la carpeta de TPVC copiada a otra computadora traería el mismo DeviceId, y cada una omitiría al bajar
    /// las filas de la otra. Si el token guardado no se puede descifrar (DPAPI solo funciona en la computadora y el
    /// usuario de Windows que lo cifraron; antes quedaba vacío en silencio) o cambió la huella de la máquina, este equipo
    /// toma un DeviceId nuevo y lo avisa.
    /// </summary>
    private void EnsureDeviceIdentity(bool tokenUnreadable)
    {
        var settings = CurrentSettings;
        var machine = _machineFingerprint();
        string? reason = null;
        if (tokenUnreadable)
        {
            reason = "No se pudo leer el token guardado: la configuración de TPVC se copió de otra computadora o de otro usuario " +
                     "de Windows. Esta computadora tomó una identidad nueva para la nube; vuelva a pegar el token en «Conexión con Turso».";
            // Sin esto, cada arranque volvería a fallar y a cambiar de identidad
            settings.ProtectedAuthToken = null;
        }
        else if (machine != null && settings.MachineFingerprint != null && settings.MachineFingerprint != machine)
        {
            reason = "La configuración de TPVC se copió de otra computadora. Esta tomó una identidad nueva para la nube, así que " +
                     "las dos sincronizan sin estorbarse.";
        }

        bool changed = false;
        if (reason != null)
        {
            AppLogger.Warn($"Turso: este equipo toma un DeviceId nuevo ({(tokenUnreadable ? "no se pudo descifrar el token" : "cambió la computadora")}).");
            settings.DeviceId = Guid.NewGuid().ToString("N");
            settings.DeviceCode = null;
            settings.DeviceIdNotice = reason;
            changed = true;
        }
        if (machine != null && settings.MachineFingerprint != machine)
        {
            settings.MachineFingerprint = machine;
            changed = true;
        }
        if (changed) TryWriteToDisk();
    }

    /// <summary>
    /// Otra instalación usa el mismo DeviceId (paso 8.10: el latido encontró su InstanceNonce): este equipo toma uno nuevo,
    /// lo guarda y lo avisa.
    /// </summary>
    public void RegenerateDeviceId(string notice)
    {
        var settings = CurrentSettings;
        AppLogger.Warn($"Turso: otra instalación usa el DeviceId {settings.DeviceId}; este equipo toma uno nuevo.");
        settings.DeviceId = Guid.NewGuid().ToString("N");
        settings.DeviceCode = null;
        settings.DeviceIdNotice = notice;
        SaveSettings(settings);
    }

    /// <summary>«Entendido» en el aviso de la identidad nueva.</summary>
    public void DismissDeviceIdNotice()
    {
        if (CurrentSettings.DeviceIdNotice == null) return;
        CurrentSettings.DeviceIdNotice = null;
        SaveSettings(CurrentSettings);
    }

    /// <summary>
    /// SHA-256 del MachineGuid de Windows (lo crea la instalación de Windows y no cambia al renombrar la computadora);
    /// null fuera de Windows o si no se puede leer.
    /// </summary>
    public static string? ReadMachineFingerprint()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") is string guid && guid.Length > 0 ? HashMachineId(guid) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>La huella que se guarda: no se guarda el MachineGuid tal cual.</summary>
    public static string HashMachineId(string machineId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("TPVC|" + machineId.Trim().ToLowerInvariant())))[..16];

    public void RecordUsage(long rowsRead, long rowsWritten)
    {
        lock (_usageLock)
        {
            EnsureMonthlyMetricsPeriod();
            CurrentSettings.MonthlyRowsRead += rowsRead;
            CurrentSettings.MonthlyRowsWritten += rowsWritten;
            CurrentSettings.DailyRowsRead += rowsRead;
            CurrentSettings.DailyRowsWritten += rowsWritten;
            TryWriteToDisk();
        }
    }

    /// <summary>Suma los bytes de laboratorio de hoy (lo que viajó, en base64) para su freno.</summary>
    public void RecordLabBytes(long up, long down)
    {
        lock (_usageLock)
        {
            EnsureMonthlyMetricsPeriod();
            CurrentSettings.DailyLabBytesUp += up;
            CurrentSettings.DailyLabBytesDown += down;
            TryWriteToDisk();
        }
    }

    /// <summary>Los bytes de laboratorio de hoy (subidos, bajados).</summary>
    public (long Up, long Down) GetLabBytesToday()
    {
        lock (_usageLock)
        {
            EnsureMonthlyMetricsPeriod();
            return (CurrentSettings.DailyLabBytesUp, CurrentSettings.DailyLabBytesDown);
        }
    }

    /// <summary>Guarda lo que se midió en la nube de laboratorio.</summary>
    public void SaveLabCloud(LabCloudSummary summary)
    {
        lock (_usageLock)
        {
            CurrentSettings.LabCloud = summary;
            TryWriteToDisk();
        }
    }

    /// <summary>El día del consultorio (yyyy-MM-dd) según el reloj de este servicio.</summary>
    public string Today => Clock.GetLocalNow().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Cuenta una descarga completa de hoy (para el freno de consumo).</summary>
    public void RecordFullPull()
    {
        lock (_usageLock)
        {
            EnsureMonthlyMetricsPeriod();
            CurrentSettings.DailyFullPulls++;
            TryWriteToDisk();
        }
    }

    /// <summary>Activa el freno de consumo; queda activo (también al reabrir el programa) hasta <see cref="ResumeAfterConsumptionBrake"/>.</summary>
    public void TripConsumptionBrake()
    {
        lock (_usageLock)
        {
            if (CurrentSettings.ConsumptionBrakeTrippedUtc != null) return;
            CurrentSettings.ConsumptionBrakeTrippedUtc = Clock.GetUtcNow().UtcDateTime;
            TryWriteToDisk();
        }
    }

    /// <summary>
    /// "Reanudar": quita el freno y vuelve a contar el día desde cero, así el freno se vuelve a activar
    /// solo si el consumo anormal continúa.
    /// </summary>
    public void ResumeAfterConsumptionBrake()
    {
        lock (_usageLock)
        {
            EnsureMonthlyMetricsPeriod();
            CurrentSettings.ConsumptionBrakeTrippedUtc = null;
            CurrentSettings.DailyRowsRead = 0;
            CurrentSettings.DailyRowsWritten = 0;
            CurrentSettings.DailyFullPulls = 0;
            TryWriteToDisk();
        }
    }

    // ================================================================== token rechazado y rotación (paso 8.15)

    /// <summary>La huella de un token: lo que se guarda para compararlo, nunca el token.</summary>
    public static string TokenHash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("TPVC-token|" + token.Trim())))[..16];

    private string? CurrentTokenHash => string.IsNullOrWhiteSpace(CurrentSettings.AuthToken) ? null : TokenHash(CurrentSettings.AuthToken);

    /// <summary>Turso rechazó el token de ahora (hasta que se pegue otro).</summary>
    public bool IsCurrentTokenRejected
    {
        get
        {
            var rejected = CurrentSettings.RejectedTokenHash;
            return rejected != null && rejected == CurrentTokenHash;
        }
    }

    /// <summary>Se espera un token nuevo porque se anularon los tokens (o se marcó un equipo como perdido).</summary>
    public bool IsTokenRotationExpected => CurrentSettings.TokenToRotateHash != null;

    /// <summary>
    /// Turso rechazó el token de ahora. Si era el que funcionaba, se anularon los tokens: la siguiente sincronización con otro
    /// lo anota en la nube. Uno que nunca funcionó (pegado de otra base o mal copiado) no es una rotación.
    /// </summary>
    public void RecordTokenRejected()
    {
        lock (_usageLock)
        {
            var hash = CurrentTokenHash;
            if (hash == null) return;
            if (CurrentSettings.RejectedTokenHash != hash) CurrentSettings.TokenRejectedUtc = Clock.GetUtcNow().UtcDateTime;
            CurrentSettings.RejectedTokenHash = hash;
            if (hash == CurrentSettings.WorkingTokenHash && CurrentSettings.TokenToRotateHash == null)
            {
                CurrentSettings.TokenToRotateHash = hash;
                CurrentSettings.TokenRotationAfterUtc = CurrentSettings.LastSyncUtc ?? Clock.GetUtcNow().UtcDateTime;
            }
            TryWriteToDisk();
        }
    }

    /// <summary>
    /// Se sincronizó bien con el token de ahora. Regresa true si es el token nuevo después de una rotación: falta anotarla
    /// en la nube (<see cref="ClearTokenRotation"/> después).
    /// </summary>
    public bool RecordTokenWorked()
    {
        lock (_usageLock)
        {
            var hash = CurrentTokenHash;
            if (hash == null) return false;
            bool changed = CurrentSettings.WorkingTokenHash != hash || CurrentSettings.RejectedTokenHash != null;
            CurrentSettings.WorkingTokenHash = hash;
            CurrentSettings.RejectedTokenHash = null;
            CurrentSettings.TokenRejectedUtc = null;
            if (changed) TryWriteToDisk();
            return CurrentSettings.TokenToRotateHash is { } old && old != hash;
        }
    }

    /// <summary>«Perdí este equipo»: el token de ahora se va a anular, y el siguiente que funcione se anota como rotación.</summary>
    public void ExpectTokenRotation()
    {
        lock (_usageLock)
        {
            var hash = CurrentTokenHash;
            if (hash == null || CurrentSettings.TokenToRotateHash != null) return;
            CurrentSettings.TokenToRotateHash = hash;
            CurrentSettings.TokenRotationAfterUtc = Clock.GetUtcNow().UtcDateTime;
            TryWriteToDisk();
        }
        SettingsChanged?.Invoke();
    }

    /// <summary>La rotación ya se anotó en la nube, o el médico cerró la guía sin rotar.</summary>
    public void ClearTokenRotation()
    {
        lock (_usageLock)
        {
            if (CurrentSettings.TokenToRotateHash == null) return;
            CurrentSettings.TokenToRotateHash = null;
            CurrentSettings.TokenRotationAfterUtc = null;
            TryWriteToDisk();
        }
        SettingsChanged?.Invoke();
    }

    public TursoQuotaSummary GetQuotaSummary()
    {
        lock (_usageLock)
        {
            EnsureMonthlyMetricsPeriod();
            return new TursoQuotaSummary
            {
                RowsReadThisMonth = CurrentSettings.MonthlyRowsRead,
                RowsWrittenThisMonth = CurrentSettings.MonthlyRowsWritten,
                EstimatedStorageMegabytes = Math.Round(Math.Max(1.0, (CurrentSettings.MonthlyRowsWritten * 0.001)), 2),
                RowsReadToday = CurrentSettings.DailyRowsRead,
                RowsWrittenToday = CurrentSettings.DailyRowsWritten,
                FullPullsToday = CurrentSettings.DailyFullPulls
            };
        }
    }

    private void TryWriteToDisk()
    {
        try
        {
            var dir = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(CurrentSettings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configFilePath, json);
        }
        catch
        {
            // Silencioso para operaciones en segundo plano
        }
    }

    /// <summary>Reinicia los contadores del mes (UTC) y los del día (hora del consultorio) cuando cambian.</summary>
    private void EnsureMonthlyMetricsPeriod()
    {
        var currentPeriod = Clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM");
        if (CurrentSettings.MonthlyMetricsPeriod != currentPeriod)
        {
            CurrentSettings.MonthlyMetricsPeriod = currentPeriod;
            CurrentSettings.MonthlyRowsRead = 0;
            CurrentSettings.MonthlyRowsWritten = 0;
        }

        var today = Clock.GetLocalNow().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (CurrentSettings.DailyMetricsDay != today)
        {
            CurrentSettings.DailyMetricsDay = today;
            CurrentSettings.DailyRowsRead = 0;
            CurrentSettings.DailyRowsWritten = 0;
            CurrentSettings.DailyFullPulls = 0;
            CurrentSettings.DailyLabBytesUp = 0;
            CurrentSettings.DailyLabBytesDown = 0;
        }
    }

    private static string ProtectToken(string clearToken)
    {
        if (string.IsNullOrEmpty(clearToken)) return string.Empty;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var bytes = Encoding.UTF8.GetBytes(clearToken);
                var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(encrypted);
            }
        }
        catch
        {
            // Fallback si DPAPI no está disponible
        }

        // En entornos no Windows (pruebas o Linux), codificar en Base64 con prefijo
        return "B64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(clearToken));
    }

    private static string UnprotectToken(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return string.Empty;

        if (cipherText.StartsWith("B64:"))
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(cipherText.Substring(4)));
            }
            catch (FormatException)
            {
                return string.Empty;
            }
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var bytes = Convert.FromBase64String(cipherText);
                var decrypted = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
        }
        catch
        {
            // Fallback
        }

        return string.Empty;
    }
}
