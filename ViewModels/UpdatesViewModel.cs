using System;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Services;

namespace TPVC.ViewModels;

/// <summary>
/// Actualizaciones y "acerca de": versión instalada, buscar e instalar la nueva (con verificación
/// SHA-256), token de GitHub para repositorios privados y la carpeta de registros.
/// </summary>
public partial class UpdatesViewModel : ObservableObject
{
    private readonly AppUpdateService _updateService;

    public UpdatesViewModel(AppUpdateService updateService)
    {
        _updateService = updateService;
        AppVersion = _updateService.CurrentVersion.TrimStart('v', 'V');
        GitHubToken = _updateService.GitHubToken ?? string.Empty;
        HasGitHubToken = !string.IsNullOrWhiteSpace(GitHubToken);
    }

    [ObservableProperty]
    private string _appVersion = string.Empty;

    [ObservableProperty]
    private string _updateStatusMessage = "Busque si hay una versión nueva.";

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
    private string _expectedSha256 = string.Empty;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string _gitHubToken = string.Empty;

    [ObservableProperty]
    private bool _isGitHubTokenVisible;

    [ObservableProperty]
    private bool _hasGitHubToken;

    [ObservableProperty]
    private bool _isTokenSectionOpen;

    [ObservableProperty]
    private string _tokenStatusMessage = string.Empty;

    public string LogDirectory => AppLogger.LogDirectory;

    [RelayCommand]
    public async Task CheckUpdatesAsync()
    {
        IsCheckingUpdates = true;
        UpdateStatusMessage = "Buscando…";
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
            UpdateStatusMessage = $"No se pudo buscar actualizaciones: {ex.Message}";
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
            UpdateStatusMessage = "La versión nueva no trae instalador para descargar.";
            return;
        }

        IsDownloading = true;
        DownloadProgress = 0;
        var progress = new Progress<double>(p => DownloadProgress = Math.Round(p, 1));
        bool success = await _updateService.DownloadAndInstallUpdateAsync(DirectDownloadUrl, DownloadFileName, AssetApiUrl, progress, ExpectedSha256);
        IsDownloading = false;

        if (!success)
        {
            UpdateStatusMessage = $"No se pudo descargar e instalar. {_updateService.LastError ?? "Revise la conexión o el token."}";
        }
    }

    [RelayCommand]
    public void ToggleTokenSection() => IsTokenSectionOpen = !IsTokenSectionOpen;

    [RelayCommand]
    public void ToggleGitHubTokenVisibility() => IsGitHubTokenVisible = !IsGitHubTokenVisible;

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
                    TokenStatusMessage = "Token pegado (falta guardarlo).";
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
        HasGitHubToken = !string.IsNullOrWhiteSpace(GitHubToken);
        TokenStatusMessage = HasGitHubToken ? "Token guardado en este equipo." : "Sin token: se usa el repositorio público.";
    }

    [RelayCommand]
    public void OpenLogsFolder() => DataManagementViewModel.OpenFolder(AppLogger.LogDirectory);
}
