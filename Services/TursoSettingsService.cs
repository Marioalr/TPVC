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

    public TursoSettingsService(string? customPath = null)
    {
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
                    if (!string.IsNullOrEmpty(CurrentSettings.ProtectedAuthToken))
                    {
                        CurrentSettings.AuthToken = UnprotectToken(CurrentSettings.ProtectedAuthToken);
                    }

                    // La PC siempre es WindowsDesktop: antes había un selector y alguien pudo elegir otro rol
                    CurrentSettings.DeviceRole = DeviceRole.WindowsDesktop;
                    EnsureMonthlyMetricsPeriod();
                    return;
                }
            }

            CurrentSettings = new TursoSettings();
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
            var raw = Convert.FromBase64String(cipherText.Substring(4));
            return Encoding.UTF8.GetString(raw);
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
