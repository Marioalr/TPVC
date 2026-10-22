using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Services;

namespace TPVC.ViewModels;

/// <summary>Paso numerado de la hoja de importar/restaurar.</summary>
public sealed record ImportStep(int Number, string Text);

/// <summary>Respaldo guardado en la carpeta de respaldos.</summary>
public sealed record BackupEntry(string Path, string FileName, DateTime Date, long Bytes)
{
    public string DateText => Date.ToString("dddd d 'de' MMMM, HH:mm", System.Globalization.CultureInfo.GetCultureInfo("es-MX"));
    public string SizeText => Bytes >= 1024 * 1024 ? $"{Bytes / 1024.0 / 1024.0:0.0} MB" : $"{Bytes / 1024.0:0} KB";
}

/// <summary>
/// Respaldos y datos: respaldar ahora, restaurar un respaldo de la lista, importar el catálogo de
/// Excel y restaurar una base completa. Importar el Excel crea un respaldo antes y se puede volver
/// atrás, así que basta un clic; reemplazar la base completa pide escribir CONFIRMAR.
/// </summary>
public partial class DataManagementViewModel : ObservableObject
{
    public const string ImportExcel = "Excel";
    public const string ImportDatabase = "Database";

    private readonly BackupService _backupService;
    private readonly DataImportService? _importService;
    private readonly NotificationService? _notifications;
    private readonly TursoSyncService? _syncService;
    private readonly LabResultService? _labResults;

    public DataManagementViewModel(BackupService backupService, DataImportService? importService = null, NotificationService? notifications = null,
        TursoSyncService? syncService = null, LabResultService? labResults = null)
    {
        _backupService = backupService;
        _importService = importService;
        _notifications = notifications;
        _syncService = syncService;
        _labResults = labResults;
        RefreshBackups();
        RefreshCloudDecision();
        RefreshLabFiles();
    }

    // ================================================================== la nube después de restaurar (paso 8.9)

    /// <summary>Se restauró un respaldo con la nube activa y falta elegir qué hacer con ella (mientras, no se sincroniza).</summary>
    [ObservableProperty]
    private bool _isCloudDecisionPending;

    /// <summary>Un «Reemplazar la nube» quedó a medias: se termina al sincronizar.</summary>
    [ObservableProperty]
    private bool _isCloudReplacementPending;

    [ObservableProperty]
    private bool _isCloudBusy;

    /// <summary>Lo que se perdería al reemplazar la nube (después de «Revisar qué se perdería»).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCloudPreview), nameof(CanConfirmCloudReplace))]
    private string _cloudPreviewText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmCloudReplace))]
    private string _cloudReplaceConfirmWord = string.Empty;

    [ObservableProperty]
    private string _cloudDecisionMessage = string.Empty;

    public bool HasCloudPreview => CloudPreviewText.Length > 0;

    public bool CanConfirmCloudReplace => HasCloudPreview && !IsCloudBusy && DataImportService.ValidateConfirmationWord(CloudReplaceConfirmWord);

    partial void OnIsCloudBusyChanged(bool value) => OnPropertyChanged(nameof(CanConfirmCloudReplace));

    public void RefreshCloudDecision()
    {
        IsCloudReplacementPending = _syncService?.IsCloudReplacementPending == true;
        IsCloudDecisionPending = IsCloudReplacementPending || _syncService?.IsRestoreDecisionPending == true;
        if (!IsCloudDecisionPending)
        {
            CloudPreviewText = string.Empty;
            CloudReplaceConfirmWord = string.Empty;
        }
    }

    /// <summary>«Revisar qué se perdería»: lee la nube una vez y dice qué tiene que este respaldo no.</summary>
    [RelayCommand]
    public async Task ReviewCloudReplacementAsync()
    {
        if (_syncService == null || IsCloudBusy) return;
        IsCloudBusy = true;
        CloudDecisionMessage = string.Empty;
        try
        {
            var (ok, message, _) = await _syncService.PreviewCloudReplacementAsync();
            if (ok) CloudPreviewText = message;
            else CloudDecisionMessage = $"✕ {message}";
        }
        finally
        {
            IsCloudBusy = false;
        }
    }

    /// <summary>«Reemplazar la nube con este respaldo» (pide CONFIRMAR).</summary>
    [RelayCommand]
    public async Task ReplaceCloudAsync()
    {
        // Cambia la generación de la nube (paso 8.9): solo la PC, dueña de la nube
        if (!TursoSyncViewModel.IsCloudAdministrator) return;
        if (_syncService == null || !CanConfirmCloudReplace) return;
        IsCloudBusy = true;
        CloudDecisionMessage = string.Empty;
        try
        {
            var result = await _syncService.ReplaceCloudWithLocalAsync();
            CloudDecisionMessage = result.Ok ? result.Message : $"✕ {result.Message}";
            Notify(result.Ok ? "La nube quedó igual a este respaldo" : "El reemplazo de la nube no terminó", result.Ok);
        }
        finally
        {
            IsCloudBusy = false;
            CloudReplaceConfirmWord = string.Empty;
            RefreshCloudDecision();
            RefreshBackups();
        }
    }

    /// <summary>Termina un reemplazo de la nube que se cortó (sincroniza).</summary>
    [RelayCommand]
    public async Task ContinueCloudReplacementAsync()
    {
        if (!TursoSyncViewModel.IsCloudAdministrator) return;
        if (_syncService == null || IsCloudBusy) return;
        IsCloudBusy = true;
        try
        {
            var result = await _syncService.SyncAsync();
            CloudDecisionMessage = result.Success ? "✓ Se terminó de reemplazar la nube con este respaldo." : $"✕ {result.ErrorMessage}";
        }
        finally
        {
            IsCloudBusy = false;
            RefreshCloudDecision();
        }
    }

    /// <summary>«Desconectar esta PC de la nube»: se queda con el respaldo y deja de sincronizar.</summary>
    [RelayCommand]
    public async Task DisconnectCloudAsync()
    {
        if (_syncService == null || IsCloudBusy) return;
        bool confirm = DialogService.Confirm(
            "Esta computadora dejará de sincronizar y se quedará con el respaldo restaurado. La nube y los demás equipos siguen como " +
            "estaban; para volver a conectarla hay que configurar la nube otra vez.\n\n¿Desea continuar?",
            "TPVC - Desconectar esta PC de la nube");
        if (!confirm) return;

        IsCloudBusy = true;
        try
        {
            var (ok, message) = await _syncService.DisconnectFromCloudAsync();
            CloudDecisionMessage = ok ? message : $"✕ {message}";
            if (ok) Notify("Esta PC se desconectó de la nube", ok: true);
        }
        finally
        {
            IsCloudBusy = false;
            RefreshCloudDecision();
        }
    }

    public string BackupDirectory => _backupService.BackupDirectory;
    public bool CanImport => _importService != null;

    // ================================================================== respaldos

    [ObservableProperty]
    private ObservableCollection<BackupEntry> _backups = new();

    [ObservableProperty]
    private string _lastBackupText = "Sin respaldos todavía";

    [ObservableProperty]
    private bool _isBackingUp;

    public bool HasBackups => Backups.Count > 0;

    public void RefreshBackups()
    {
        try
        {
            Backups = new ObservableCollection<BackupEntry>(_backupService.GetBackups()
                .Take(30)
                .Select(f => new BackupEntry(f.FullName, f.Name, f.LastWriteTime, f.Length)));
            var latest = Backups.FirstOrDefault();
            LastBackupText = latest == null
                ? "Sin respaldos todavía"
                : latest.Date.Date == DateTime.Today ? $"Último respaldo hoy {latest.Date:HH:mm}" : $"Último respaldo {latest.Date:dd/MM/yyyy HH:mm}";
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo leer la carpeta de respaldos", ex);
            LastBackupText = "No se pudo leer la carpeta de respaldos";
        }
        OnPropertyChanged(nameof(HasBackups));
    }

    [RelayCommand]
    public async Task BackupNowAsync()
    {
        if (IsBackingUp) return;
        IsBackingUp = true;
        try
        {
            var path = await _backupService.CreateBackupNowAsync();
            Notify($"Respaldo creado: {Path.GetFileName(path)}", ok: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error en respaldo manual", ex);
            Notify($"No se pudo crear el respaldo: {ex.Message}", ok: false);
        }
        finally
        {
            IsBackingUp = false;
            RefreshBackups();
        }
    }

    [RelayCommand]
    public void OpenBackupFolder() => OpenFolder(_backupService.BackupDirectory);

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Error($"No se pudo abrir la carpeta {path}", ex);
        }
    }

    // ================================================================== importar y restaurar (hoja en pasos)

    [ObservableProperty]
    private bool _isImportSheetOpen;

    /// <summary>"Excel" (catálogo) o "Database" (reemplazo completo).</summary>
    [ObservableProperty]
    private string _importKind = ImportExcel;

    [ObservableProperty]
    private string _importFilePath = string.Empty;

    [ObservableProperty]
    private string _importConfirmWord = string.Empty;

    [ObservableProperty]
    private bool _isImporting;

    [ObservableProperty]
    private DataImportResult? _importResult;

    public bool IsDatabaseRestore => ImportKind == ImportDatabase;
    public string ImportFileName => Path.GetFileName(ImportFilePath);
    public bool RequiresTypedConfirmation => IsDatabaseRestore;
    public bool HasImportResult => ImportResult != null;
    public bool IsConfirmStep => IsImportSheetOpen && ImportResult == null;

    /// <summary>Lo irreversible (reemplazar toda la base) pide escribir CONFIRMAR; el catálogo, no.</summary>
    public bool CanProceedWithImport => !IsImporting && !string.IsNullOrWhiteSpace(ImportFilePath)
        && (!RequiresTypedConfirmation || DataImportService.ValidateConfirmationWord(ImportConfirmWord));

    public string ImportSheetTitle => IsDatabaseRestore ? "Restaurar base de datos" : "Importar catálogo de Excel";

    public string ImportFileDetail
    {
        get
        {
            try
            {
                var info = new FileInfo(ImportFilePath);
                return info.Exists
                    ? $"{info.Length / 1024.0:0} KB · modificado {info.LastWriteTime:dd/MM/yyyy HH:mm}"
                    : "El archivo ya no está disponible";
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>Qué pasará, en el orden en que pasa.</summary>
    public IReadOnlyList<ImportStep> ImportSteps => (IsDatabaseRestore
        ? new[]
        {
            "Se crea un respaldo de la base actual (por si hay que volver).",
            "Se reemplaza TODA la información (pacientes, consultas, inventario y ventas) por la del archivo.",
            "Se revisa el archivo y se actualiza a esta versión del programa."
        }
        : new[]
        {
            "Se crea un respaldo de la base actual (por si hay que volver).",
            "Se agregan o actualizan medicamentos, plantillas de receta y dosis pediátricas.",
            "Se actualizan el perfil del médico y los expedientes que vengan en el archivo."
        }).Select((text, i) => new ImportStep(i + 1, text)).ToList();

    public IReadOnlyList<string> ImportResultLines
    {
        get
        {
            var r = ImportResult;
            if (r == null) return Array.Empty<string>();
            var lines = new List<string>();
            if (r.ProductsImported > 0) lines.Add($"{r.ProductsImported} medicamento(s) nuevo(s)");
            if (r.ProductsUpdated > 0) lines.Add($"{r.ProductsUpdated} medicamento(s) actualizado(s)");
            if (r.PatientsImported > 0) lines.Add($"{r.PatientsImported} expediente(s)");
            if (r.TemplatesImported > 0) lines.Add($"{r.TemplatesImported} plantilla(s) de receta");
            if (r.PediatricRulesImported > 0) lines.Add($"{r.PediatricRulesImported} dosis pediátrica(s)");
            if (r.DoctorProfileUpdated) lines.Add($"Perfil del médico: {r.DoctorName}");
            if (!string.IsNullOrWhiteSpace(r.BackupPath)) lines.Add($"Respaldo previo: {Path.GetFileName(r.BackupPath)}");
            return lines;
        }
    }

    partial void OnImportKindChanged(string value) => NotifyImportState();
    partial void OnImportFilePathChanged(string value) => NotifyImportState();
    partial void OnImportConfirmWordChanged(string value) => OnPropertyChanged(nameof(CanProceedWithImport));
    partial void OnIsImportingChanged(bool value) => OnPropertyChanged(nameof(CanProceedWithImport));
    partial void OnImportResultChanged(DataImportResult? value) => NotifyImportState();
    partial void OnIsImportSheetOpenChanged(bool value) => OnPropertyChanged(nameof(IsConfirmStep));

    private void NotifyImportState()
    {
        OnPropertyChanged(nameof(IsDatabaseRestore));
        OnPropertyChanged(nameof(ImportFileName));
        OnPropertyChanged(nameof(ImportFileDetail));
        OnPropertyChanged(nameof(RequiresTypedConfirmation));
        OnPropertyChanged(nameof(CanProceedWithImport));
        OnPropertyChanged(nameof(ImportSheetTitle));
        OnPropertyChanged(nameof(ImportSteps));
        OnPropertyChanged(nameof(HasImportResult));
        OnPropertyChanged(nameof(IsConfirmStep));
        OnPropertyChanged(nameof(ImportResultLines));
    }

    [RelayCommand]
    public void StartImportExcel()
    {
        if (IsImporting) return;
        var path = DialogService.ShowOpenFileDialog(
            "Catálogos (*.xlsx;*.csv;*.txt)|*.xlsx;*.csv;*.txt|Libros de Excel (*.xlsx)|*.xlsx|Archivos CSV (*.csv)|*.csv|Todos los archivos (*.*)|*.*",
            "Elegir el catálogo de Excel");
        if (!string.IsNullOrWhiteSpace(path)) OpenImportSheet(ImportExcel, path);
    }

    [RelayCommand]
    public void StartRestoreFromFile()
    {
        // Restaurar con la nube activa cambia la generación (paso 8.9): solo la PC. La tablet vuelve a bajar todo de la nube
        if (!TursoSyncViewModel.IsCloudAdministrator) return;
        if (IsImporting) return;
        var path = DialogService.ShowOpenFileDialog(
            "Bases de datos SQLite (*.db;*.sqlite;*.sqlite3)|*.db;*.sqlite;*.sqlite3|Todos los archivos (*.*)|*.*",
            "Elegir la base de datos a restaurar");
        if (!string.IsNullOrWhiteSpace(path)) OpenImportSheet(ImportDatabase, path);
    }

    [RelayCommand]
    public void RestoreBackup(BackupEntry? backup)
    {
        if (!TursoSyncViewModel.IsCloudAdministrator) return;
        if (backup != null && !IsImporting) OpenImportSheet(ImportDatabase, backup.Path);
    }

    private void OpenImportSheet(string kind, string path)
    {
        ImportResult = null;
        ImportKind = kind;
        ImportFilePath = path;
        ImportConfirmWord = string.Empty;
        IsImportSheetOpen = true;
    }

    [RelayCommand]
    public void CloseImportSheet()
    {
        if (IsImporting) return;
        IsImportSheetOpen = false;
        ImportResult = null;
        ImportFilePath = string.Empty;
        ImportConfirmWord = string.Empty;
    }

    [RelayCommand]
    public async Task ExecuteImportAsync()
    {
        if (!CanProceedWithImport) return;
        if (_importService == null)
        {
            Notify("La importación no está disponible en este equipo.", ok: false);
            return;
        }

        IsImporting = true;
        var path = ImportFilePath;
        try
        {
            var result = IsDatabaseRestore
                ? await Task.Run(() => _importService.ImportDatabaseFileAsync(path))
                : await Task.Run(() => _importService.ImportExcelOrCsvAsync(path));
            ImportResult = result;
            if (result.Success) Notify(IsDatabaseRestore ? "Base de datos restaurada" : "Catálogo importado", ok: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al importar o restaurar", ex);
            ImportResult = new DataImportResult { Success = false, Message = $"Error inesperado: {ex.Message}" };
        }
        finally
        {
            IsImporting = false;
            ImportConfirmWord = string.Empty;
            RefreshBackups();
            RefreshCloudDecision();
        }
    }

    private void Notify(string message, bool ok)
    {
        if (_notifications == null) return;
        if (ok) _notifications.Success(message);
        else _notifications.Warning(message);
    }
}
