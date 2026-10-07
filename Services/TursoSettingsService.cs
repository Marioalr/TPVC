using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TPVC.Models;

namespace TPVC.Services;

/// <summary>
/// Gestiona la persistencia segura de la configuración de Turso Cloud,
/// cifrado de tokens de autenticación y exportación de credenciales para dispositivos Android.
/// </summary>
public class TursoSettingsService
{
    private readonly string _configFilePath;
    public TursoSettings CurrentSettings { get; private set; } = new();

    public event Action? SettingsChanged;

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
        EnsureMonthlyMetricsPeriod();
        CurrentSettings.MonthlyRowsRead += rowsRead;
        CurrentSettings.MonthlyRowsWritten += rowsWritten;

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

    public TursoQuotaSummary GetQuotaSummary()
    {
        EnsureMonthlyMetricsPeriod();
        return new TursoQuotaSummary
        {
            RowsReadThisMonth = CurrentSettings.MonthlyRowsRead,
            RowsWrittenThisMonth = CurrentSettings.MonthlyRowsWritten,
            EstimatedStorageMegabytes = Math.Round(Math.Max(1.0, (CurrentSettings.MonthlyRowsWritten * 0.001)), 2)
        };
    }

    /// <summary>
    /// Genera un paquete JSON para vincular fácilmente una Tablet Android o Celular Android
    /// con la base de datos de Turso sin tener que teclear manualmente URLs ni tokens.
    /// </summary>
    public string GenerateAndroidPairingPayload(DeviceRole targetRole, string? clinicName = null, string? doctorName = null)
    {
        var pairing = new
        {
            Version = typeof(TursoSettingsService).Assembly.GetName().Version?.ToString(3) ?? "1",
            // El dispositivo Android debe hablar la misma versión del protocolo (ver docs/TURSO_SYNC_ARCHITECTURE.md)
            SyncProtocolVersion = TursoSyncService.RemoteSchemaVersion,
            DatabaseUrl = CurrentSettings.DatabaseUrl,
            AuthToken = CurrentSettings.AuthToken,
            AssignedRole = targetRole.ToString(),
            RoleDescription = DevicePermissions.GetRoleDescription(targetRole),
            ClinicName = clinicName ?? "Consultorio Médico",
            DoctorName = doctorName ?? "Dr. Titular",
            CreatedAtUtc = DateTime.UtcNow
        };

        return JsonSerializer.Serialize(pairing, new JsonSerializerOptions { WriteIndented = true });
    }

    private void EnsureMonthlyMetricsPeriod()
    {
        var currentPeriod = DateTime.UtcNow.ToString("yyyy-MM");
        if (CurrentSettings.MonthlyMetricsPeriod != currentPeriod)
        {
            CurrentSettings.MonthlyMetricsPeriod = currentPeriod;
            CurrentSettings.MonthlyRowsRead = 0;
            CurrentSettings.MonthlyRowsWritten = 0;
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
