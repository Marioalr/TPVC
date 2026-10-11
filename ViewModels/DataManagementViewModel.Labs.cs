using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Services;
using TPVC.Services.LabFiles;

namespace TPVC.ViewModels;

/// <summary>
/// Respaldos y datos › Resultados de laboratorio (paso 8.12b): el archivo de esta PC y su respaldo externo (una USB o una
/// carpeta de OneDrive). Con el respaldo, la PC principal puede liberar la nube cuando llega a su tope.
/// </summary>
public partial class DataManagementViewModel
{
    public bool IsLabAvailable => _labResults != null;

    [ObservableProperty]
    private string _labLocalText = string.Empty;

    [ObservableProperty]
    private string _labBackupFolderText = "Sin carpeta elegida";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabFolderButtonText))]
    private bool _hasLabBackupFolder;

    public string LabFolderButtonText => HasLabBackupFolder ? "Cambiar carpeta" : "Elegir carpeta";

    [ObservableProperty]
    private bool _isLabBusy;

    public string LabArchiveDirectory => _labResults?.Store.Root ?? string.Empty;

    public void RefreshLabFiles()
    {
        if (_labResults == null) return;
        try
        {
            var (files, bytes) = _labResults.LocalTotals();
            LabLocalText = files == 0
                ? "Todavía no hay resultados de laboratorio en esta PC."
                : $"{files:N0} archivo{(files == 1 ? string.Empty : "s")} en esta PC ({LabFileImporter.Megabytes(bytes)}).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LabLocalText = "No se pudo leer el archivo de laboratorio.";
        }

        var folder = _labResults.BackupFolder;
        HasLabBackupFolder = !string.IsNullOrWhiteSpace(folder);
        LabBackupFolderText = !HasLabBackupFolder
            ? "Sin carpeta elegida: sin respaldo externo, la nube no libera espacio al llegar a su tope."
            : Directory.Exists(folder) ? folder! : $"{folder} (no está conectada)";
    }

    [RelayCommand]
    public void ChooseLabBackupFolder()
    {
        if (_labResults == null) return;
        var folder = DialogService.OpenFolderPickerHandler("Carpeta del respaldo de laboratorio (USB u OneDrive)", _labResults.BackupFolder);
        if (folder == null) return;
        _labResults.BackupFolder = folder;
        RefreshLabFiles();
        _ = BackUpLabFilesAsync();
    }

    [RelayCommand]
    public async Task BackUpLabFilesAsync()
    {
        var folder = _labResults?.BackupFolder;
        if (_labResults == null || IsLabBusy || string.IsNullOrWhiteSpace(folder)) return;
        IsLabBusy = true;
        try
        {
            var (copied, already) = await _labResults.BackUpAsync(folder);
            Notify(copied == 0 ? $"El respaldo de laboratorio ya estaba al día ({already:N0} archivos)." : $"Se respaldaron {copied:N0} archivos de laboratorio.", ok: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn($"Laboratorio: no se pudo respaldar en {folder}: {ex.Message}");
            Notify($"No se pudo respaldar: {ex.Message}", ok: false);
        }
        finally
        {
            IsLabBusy = false;
            RefreshLabFiles();
        }
    }

    [RelayCommand]
    public async Task RestoreLabFilesAsync()
    {
        var folder = _labResults?.BackupFolder;
        if (_labResults == null || IsLabBusy || string.IsNullOrWhiteSpace(folder)) return;
        IsLabBusy = true;
        try
        {
            var restored = await _labResults.RestoreFromBackupAsync(folder);
            Notify(restored == 0 ? "No faltaba nada en esta PC." : $"Se trajeron {restored:N0} archivos de laboratorio del respaldo.", ok: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify($"No se pudo traer del respaldo: {ex.Message}", ok: false);
        }
        finally
        {
            IsLabBusy = false;
            RefreshLabFiles();
        }
    }

    [RelayCommand]
    public void OpenLabArchiveFolder() => OpenFolder(LabArchiveDirectory);
}
