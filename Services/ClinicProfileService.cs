using System;
using System.IO;
using System.Text.Json;
using TPVC.Data;
using TPVC.Models;

namespace TPVC.Services;

/// <summary>
/// Gestiona la persistencia del perfil oficial del médico y del consultorio.
/// Se almacena en %LocalAppData%\TPVC\clinic_profile.json para acceso inmediato,
/// independiente de la base de datos y compatible con respaldos.
/// </summary>
public class ClinicProfileService
{
    private readonly string _settingsFile;

    public ClinicProfile CurrentProfile { get; private set; } = new();

    /// <summary>
    /// Verdadero si es la primera vez que se ejecuta el software en este equipo
    /// (la base de datos no existía previamente y no se ha configurado el asistente inicial).
    /// </summary>
    public bool IsFirstRun { get; private set; }

    public event Action? ProfileChanged;

    public ClinicProfileService(string? customSettingsFile = null)
    {
        _settingsFile = !string.IsNullOrEmpty(customSettingsFile)
            ? customSettingsFile
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TPVC",
                "clinic_profile.json"
            );
        LoadProfile();
    }

    public void LoadProfile()
    {
        try
        {
            if (File.Exists(_settingsFile))
            {
                var json = File.ReadAllText(_settingsFile);
                var loaded = JsonSerializer.Deserialize<ClinicProfile>(json);
                if (loaded != null)
                {
                    CurrentProfile = loaded;
                    IsFirstRun = false;
                    return;
                }
            }

            // Si no existe el archivo de perfil, verificar si la base de datos ya existía (instalación previa/actualización)
            bool dbExists = File.Exists(AppDbContext.DbPath);
            if (dbExists)
            {
                // Es una actualización desde una versión anterior (ej. v1.4.0 / v1.5.0):
                // No se debe forzar el asistente de primer arranque
                CurrentProfile = new ClinicProfile
                {
                    IsConfigured = false,
                    IsInitialSetupCompleted = true
                };
                IsFirstRun = false;
            }
            else
            {
                // Es una instalación completamente nueva
                CurrentProfile = new ClinicProfile
                {
                    IsConfigured = false,
                    IsInitialSetupCompleted = false
                };
                IsFirstRun = true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al cargar el perfil del médico", ex);
            CurrentProfile = new ClinicProfile();
            IsFirstRun = false;
        }
    }

    /// <param name="preserveTimestamp">true al aplicar un perfil descargado de la nube (conserva su UpdatedAtUtc).</param>
    public void SaveProfile(ClinicProfile profile, bool preserveTimestamp = false)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (!preserveTimestamp || profile.UpdatedAtUtc == null)
        {
            profile.UpdatedAtUtc = DateTime.UtcNow;
        }

        // Marcar como configurado si tiene al menos nombre de médico o clínica
        if (!string.IsNullOrWhiteSpace(profile.DoctorName) || !string.IsNullOrWhiteSpace(profile.ClinicName))
        {
            profile.IsConfigured = true;
        }

        profile.IsInitialSetupCompleted = true;
        CurrentProfile = profile;
        IsFirstRun = false;

        try
        {
            var dir = Path.GetDirectoryName(_settingsFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFile, json);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al guardar el perfil del consultorio y médico", ex);
        }

        ProfileChanged?.Invoke();
    }
}
