using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Services;

namespace TPVC.ViewModels;

public class AccentPaletteOption
{
    public string Name { get; set; } = string.Empty;
    public string Hex { get; set; } = string.Empty;
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly ThemeService _themeService;
    private readonly AppUpdateService _updateService;
    private readonly BackupService _backupService;

    [ObservableProperty]
    private string _currentMode = "Claro";

    [ObservableProperty]
    private string _currentAccentHex = "#0284C7";

    [ObservableProperty]
    private string _customBgHex = "#F8FAFC";

    [ObservableProperty]
    private string _customSurfaceHex = "#FFFFFF";

    [ObservableProperty]
    private string _appVersion = "1.5.0";

    [ObservableProperty]
    private string _expectedSha256 = string.Empty;

    // Respaldos de la base de datos
    [ObservableProperty]
    private string _lastBackupText = "Sin respaldos todavía";

    [ObservableProperty]
    private string _backupStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBackingUp;

    public string DatabasePath => _backupService.DatabasePath;

    public string BackupDirectory => _backupService.BackupDirectory;

    [ObservableProperty]
    private string _updateStatusMessage = "Presiona 'Buscar Actualizaciones' para comprobar.";

    [ObservableProperty]
    private bool _isCheckingUpdates;

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private string _latestVersion = string.Empty;

    [ObservableProperty]
    private string _releaseNotes = string.Empty;

    [ObservableProperty]
    private string _directDownloadUrl = string.Empty;

    [ObservableProperty]
    private string _assetApiUrl = string.Empty;

    [ObservableProperty]
    private string _downloadFileName = string.Empty;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitHubTokenVisibilityIcon))]
    private string _gitHubToken = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitHubTokenVisibilityIcon))]
    private bool _isGitHubTokenVisible;

    public string GitHubTokenVisibilityIcon => IsGitHubTokenVisible ? "🙈" : "👁";

    [ObservableProperty]
    private string _tokenStatusMessage = string.Empty;

    public ObservableCollection<AccentPaletteOption> PaletteOptions { get; } = new()
    {
        new() { Name = "Cian Médico", Hex = "#0284C7" },
        new() { Name = "Azul Clínico", Hex = "#2563EB" },
        new() { Name = "Verde Quirúrgico", Hex = "#0D9488" },
        new() { Name = "Esmeralda Salud", Hex = "#059669" },
        new() { Name = "Violeta", Hex = "#7C3AED" },
        new() { Name = "Rosa / Magenta", Hex = "#E11D48" },
        new() { Name = "Ámbar Cálido", Hex = "#D97706" },
        new() { Name = "Grafito Neutro", Hex = "#475569" }
    };

    public SettingsViewModel(ThemeService themeService, AppUpdateService updateService, BackupService? backupService = null)
    {
        _themeService = themeService;
        _updateService = updateService;
        _backupService = backupService ?? new BackupService();
        RefreshBackupInfo();

        AppVersion = _updateService.CurrentVersion.TrimStart('v', 'V');
        GitHubToken = _updateService.GitHubToken ?? string.Empty;
        LoadCurrentThemeSettings();
    }

    private void LoadCurrentThemeSettings()
    {
        var settings = _themeService.CurrentSettings;
        CurrentMode = settings.ThemeMode;
        CurrentAccentHex = settings.AccentColor;
        CustomBgHex = settings.BackgroundColor;
        CustomSurfaceHex = settings.SurfaceColor;
    }

    [RelayCommand]
    public void SelectMode(string mode)
    {
        CurrentMode = mode;
        _themeService.SetMode(mode);
        LoadCurrentThemeSettings();
    }

    [RelayCommand]
    public void SelectAccent(string hex)
    {
        CurrentAccentHex = hex;
        _themeService.SetAccent(hex);
    }

    [RelayCommand]
    public void ApplyCustomColors()
    {
        if (string.IsNullOrWhiteSpace(CustomBgHex) || string.IsNullOrWhiteSpace(CustomSurfaceHex))
            return;

        _themeService.SetCustomBackground(CustomBgHex.Trim(), CustomSurfaceHex.Trim());
        CurrentMode = "Personalizado";
    }

    [RelayCommand]
    public void ToggleGitHubTokenVisibility()
    {
        IsGitHubTokenVisible = !IsGitHubTokenVisible;
    }

    [RelayCommand]
    public void PasteGitHubToken()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    GitHubToken = TursoClient.CleanToken(text);
                    TokenStatusMessage = "✓ Token pegado desde el portapapeles.";
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al pegar token de GitHub desde el portapapeles", ex);
        }
    }

    [RelayCommand]
    public void SaveGitHubToken()
    {
        _updateService.SaveToken(GitHubToken);
        TokenStatusMessage = string.IsNullOrWhiteSpace(GitHubToken)
            ? "✓ Configuración guardada (modo público sin token)."
            : "✓ Token de GitHub guardado exitosamente para este equipo.";
    }

    [RelayCommand]
    public async Task CheckUpdatesAsync()
    {
        IsCheckingUpdates = true;
        UpdateStatusMessage = "Comprobando si hay nuevas versiones...";
        try
        {
            var result = await _updateService.CheckForUpdatesAsync();
            UpdateStatusMessage = result.Message;
            IsUpdateAvailable = result.IsUpdateAvailable;
            LatestVersion = result.LatestVersion;
            ReleaseNotes = result.ReleaseNotes;
            DirectDownloadUrl = result.DirectDownloadUrl;
            AssetApiUrl = result.AssetApiUrl;
            DownloadFileName = result.DownloadFileName;
            ExpectedSha256 = result.ExpectedSha256 ?? string.Empty;
        }
        catch (Exception ex)
        {
            UpdateStatusMessage = $"Error al buscar actualizaciones: {ex.Message}";
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    [RelayCommand]
    public async Task DownloadAndInstallUpdateAsync()
    {
        if (string.IsNullOrWhiteSpace(DirectDownloadUrl) && string.IsNullOrWhiteSpace(AssetApiUrl))
        {
            UpdateStatusMessage = "No se encontró paquete directo de actualización en la versión.";
            return;
        }

        IsDownloading = true;
        DownloadProgress = 0;
        var progress = new Progress<double>(p => DownloadProgress = Math.Round(p, 1));

        bool success = await _updateService.DownloadAndInstallUpdateAsync(DirectDownloadUrl, DownloadFileName, AssetApiUrl, progress, ExpectedSha256);
        IsDownloading = false;

        if (!success)
        {
            UpdateStatusMessage = $"No se pudo descargar e instalar la actualización. {_updateService.LastError ?? "Verifique la conexión o el token configurado."}";
        }
    }

    // ==========================================
    // RESPALDOS
    // ==========================================
    public void RefreshBackupInfo()
    {
        try
        {
            var latest = _backupService.GetLatestBackup();
            LastBackupText = latest == null
                ? "Sin respaldos todavía"
                : $"Último respaldo: {latest.LastWriteTime:dd/MM/yyyy HH:mm} ({latest.Length / 1024.0:N0} KB) • {_backupService.GetBackups().Count} guardados";
        }
        catch
        {
            LastBackupText = "No se pudo leer la carpeta de respaldos";
        }
    }

    [RelayCommand]
    public async Task BackupNowAsync()
    {
        IsBackingUp = true;
        BackupStatusMessage = "Creando respaldo...";
        try
        {
            var path = await _backupService.CreateBackupNowAsync();
            BackupStatusMessage = $"✓ Respaldo creado: {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error en respaldo manual", ex);
            BackupStatusMessage = $"No se pudo crear el respaldo: {ex.Message}";
        }
        finally
        {
            IsBackingUp = false;
            RefreshBackupInfo();
        }
    }

    [RelayCommand]
    public void OpenBackupFolder() => OpenFolder(_backupService.BackupDirectory);

    [RelayCommand]
    public void OpenLogsFolder() => OpenFolder(AppLogger.LogDirectory);

    private static void OpenFolder(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Error($"No se pudo abrir la carpeta {path}", ex);
        }
    }
}
