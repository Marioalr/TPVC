using System;
using System.IO;
using System.Text.Json;
using TPVC.Data;
using TPVC.Models;

namespace TPVC.Services;

/// <summary>Imágenes del membrete de la receta.</summary>
public enum LetterheadImageKind
{
    /// <summary>Escudo o logotipo (arriba a la izquierda).</summary>
    Crest,

    /// <summary>Imagen del encabezado (arriba a la derecha).</summary>
    Header
}

/// <summary>
/// Gestiona la persistencia del perfil oficial del médico y del consultorio.
/// Se almacena en %LocalAppData%\TPVC\clinic_profile.json para acceso inmediato,
/// independiente de la base de datos y compatible con respaldos. Las imágenes del membrete
/// de la receta se guardan en la carpeta "membrete" junto al perfil.
/// </summary>
public class ClinicProfileService
{
    /// <summary>Tamaño máximo de una imagen del membrete (va incrustada en el HTML/PDF de cada receta).</summary>
    public const int MaxLetterheadImageBytes = 10 * 1024 * 1024;

    private readonly string _settingsFile;

    public ClinicProfile CurrentProfile { get; private set; } = new();

    /// <summary>Carpeta de las imágenes del membrete (%LocalAppData%\TPVC\membrete).</summary>
    public string LetterheadFolder { get; }

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
        LetterheadFolder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_settingsFile)) ?? string.Empty, "membrete");
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
        finally
        {
            CurrentProfile.LetterheadFolder = LetterheadFolder;
            RefreshLetterheadHashes(CurrentProfile);
        }
    }

    /// <summary>
    /// El hash de cada imagen sale de su archivo (paso 8.12): el de un perfil de antes de v1.29.0 se calcula al abrir, y
    /// una imagen que ya no existe queda sin hash.
    /// </summary>
    private void RefreshLetterheadHashes(ClinicProfile profile)
    {
        profile.CrestImageHash = HashFile(profile.CrestImagePath);
        profile.HeaderImageHash = HashFile(profile.HeaderImagePath);
    }

    private static string HashFile(string? path)
    {
        try
        {
            return path != null && File.Exists(path) ? HashImage(File.ReadAllBytes(path)) : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>SHA-256 de una imagen en hex con minúsculas: su llave en la nube (ClinicAssets).</summary>
    public static string HashImage(byte[] content) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));

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

        // Las imágenes del membrete solo cambian con SaveLetterheadImage / ClearLetterheadImage o al bajarlas de la nube
        // (ApplyLetterheadImageFromCloud): un perfil nuevo (la edición en Configuración, el asistente inicial o los textos
        // que llegan de la nube) conserva las actuales, con su hash y su marca, si no trae las suyas
        if (!ReferenceEquals(profile, CurrentProfile))
        {
            if (string.IsNullOrWhiteSpace(profile.CrestImageFile))
            {
                profile.CrestImageFile = CurrentProfile.CrestImageFile;
                profile.CrestImageHash = CurrentProfile.CrestImageHash;
                profile.CrestImageEditedHere = CurrentProfile.CrestImageEditedHere;
            }
            if (string.IsNullOrWhiteSpace(profile.HeaderImageFile))
            {
                profile.HeaderImageFile = CurrentProfile.HeaderImageFile;
                profile.HeaderImageHash = CurrentProfile.HeaderImageHash;
                profile.HeaderImageEditedHere = CurrentProfile.HeaderImageEditedHere;
            }
        }
        profile.LetterheadFolder = LetterheadFolder;

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

    // =========================================================================
    // MEMBRETE DE LA RECETA (escudo e imagen del encabezado)
    // =========================================================================

    /// <summary>Copia la imagen elegida (PNG o JPG) a la carpeta del membrete y la deja en el perfil.</summary>
    /// <exception cref="InvalidOperationException">No es una imagen PNG/JPG o pesa demasiado.</exception>
    public void SaveLetterheadImage(LetterheadImageKind kind, string sourceFilePath)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
        {
            throw new InvalidOperationException("No se encontró el archivo de la imagen.");
        }
        if (new FileInfo(sourceFilePath).Length > MaxLetterheadImageBytes)
        {
            throw new InvalidOperationException("La imagen pesa más de 10 MB; elija una más ligera.");
        }
        SaveLetterheadImage(kind, File.ReadAllBytes(sourceFilePath));
    }

    /// <summary>Guarda la imagen (PNG o JPG) en la carpeta del membrete y la deja en el perfil.</summary>
    /// <exception cref="InvalidOperationException">No es una imagen PNG/JPG o pesa demasiado.</exception>
    public void SaveLetterheadImage(LetterheadImageKind kind, byte[] content)
    {
        var extension = ImageExtension(content)
                        ?? throw new InvalidOperationException("El archivo no es una imagen PNG o JPG.");
        if (content.Length > MaxLetterheadImageBytes)
        {
            throw new InvalidOperationException("La imagen pesa más de 10 MB; elija una más ligera.");
        }

        Directory.CreateDirectory(LetterheadFolder);
        // Nombre nuevo en cada cambio: la vista previa no muestra una copia vieja guardada en caché
        // y una imagen todavía abierta no impide guardar la nueva
        var fileName = $"{BaseName(kind)}-{Guid.NewGuid().ToString("N")[..12]}{extension}";
        File.WriteAllBytes(Path.Combine(LetterheadFolder, fileName), content);

        var previous = GetLetterheadFile(kind);
        SetLetterheadFile(kind, fileName, HashImage(content), editedHere: true);
        // Desde el paso 8.12 la imagen viaja a la nube con el perfil: adelanta su fecha de modificación
        SaveProfile(CurrentProfile);
        DeleteLetterheadFile(previous);
    }

    /// <summary>Quita la imagen del membrete (la receta sale sin ella; también en los demás equipos).</summary>
    public void ClearLetterheadImage(LetterheadImageKind kind)
    {
        var previous = GetLetterheadFile(kind);
        if (string.IsNullOrWhiteSpace(previous)) return;
        SetLetterheadFile(kind, string.Empty, string.Empty, editedHere: true);
        SaveProfile(CurrentProfile);
        DeleteLetterheadFile(previous);
    }

    /// <summary>
    /// La imagen que dice la nube (paso 8.12): la guarda (o la quita, con <paramref name="content"/> null) sin adelantar
    /// la fecha del perfil ni marcarla como elegida aquí. Devuelve false si no es una imagen PNG o JPG.
    /// </summary>
    public bool ApplyLetterheadImageFromCloud(LetterheadImageKind kind, byte[]? content)
    {
        var previous = GetLetterheadFile(kind);
        bool editedHere = kind == LetterheadImageKind.Crest ? CurrentProfile.CrestImageEditedHere : CurrentProfile.HeaderImageEditedHere;
        if (content == null)
        {
            if (string.IsNullOrWhiteSpace(previous) && GetLetterheadHash(kind).Length == 0) return true;
            SetLetterheadFile(kind, string.Empty, string.Empty, editedHere);
        }
        else
        {
            var extension = ImageExtension(content);
            if (extension == null || content.Length > MaxLetterheadImageBytes) return false;
            Directory.CreateDirectory(LetterheadFolder);
            var fileName = $"{BaseName(kind)}-{Guid.NewGuid().ToString("N")[..12]}{extension}";
            File.WriteAllBytes(Path.Combine(LetterheadFolder, fileName), content);
            SetLetterheadFile(kind, fileName, HashImage(content), editedHere);
        }
        SaveProfile(CurrentProfile, preserveTimestamp: true);
        DeleteLetterheadFile(previous);
        return true;
    }

    /// <summary>El hash de la imagen de este equipo; vacío si no tiene.</summary>
    public string GetLetterheadHash(LetterheadImageKind kind) =>
        kind == LetterheadImageKind.Crest ? CurrentProfile.CrestImageHash : CurrentProfile.HeaderImageHash;

    /// <summary>Los bytes de la imagen de este equipo, o null si no tiene (o ya no se puede leer).</summary>
    public byte[]? ReadLetterheadImage(LetterheadImageKind kind)
    {
        var path = kind == LetterheadImageKind.Crest ? CurrentProfile.CrestImagePath : CurrentProfile.HeaderImagePath;
        try
        {
            return path != null ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>".png" o ".jpg" según la firma del archivo; null si no es una de esas imágenes.</summary>
    public static string? ImageExtension(byte[]? content) => Documents.LetterheadHtml.ImageExtension(content);

    /// <summary>Indica si el archivo es una imagen PNG o JPG que se puede usar en el membrete.</summary>
    public static bool IsSupportedImageFile(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            if (new FileInfo(path).Length > MaxLetterheadImageBytes) return false;
            var header = new byte[8];
            using var stream = File.OpenRead(path);
            return stream.Read(header, 0, header.Length) == header.Length && ImageExtension(header) != null;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string BaseName(LetterheadImageKind kind) => kind == LetterheadImageKind.Crest ? "escudo" : "encabezado";

    private string GetLetterheadFile(LetterheadImageKind kind) =>
        kind == LetterheadImageKind.Crest ? CurrentProfile.CrestImageFile : CurrentProfile.HeaderImageFile;

    private void SetLetterheadFile(LetterheadImageKind kind, string fileName, string hash, bool editedHere)
    {
        if (kind == LetterheadImageKind.Crest)
        {
            CurrentProfile.CrestImageFile = fileName;
            CurrentProfile.CrestImageHash = hash;
            CurrentProfile.CrestImageEditedHere = editedHere;
        }
        else
        {
            CurrentProfile.HeaderImageFile = fileName;
            CurrentProfile.HeaderImageHash = hash;
            CurrentProfile.HeaderImageEditedHere = editedHere;
        }
    }

    private void DeleteLetterheadFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return;
        try
        {
            var path = Path.Combine(LetterheadFolder, Path.GetFileName(fileName));
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Abierta todavía (vista previa): se queda; el perfil ya no la usa
            AppLogger.Info($"No se pudo borrar la imagen anterior del membrete ({fileName}): {ex.Message}");
        }
    }
}
