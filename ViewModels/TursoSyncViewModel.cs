using System;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

public partial class TursoSyncViewModel : ObservableObject
{
    private readonly TursoSyncService _syncService;
    private readonly TursoSettingsService _settingsService;

    [ObservableProperty]
    private bool _isSyncEnabled;

    [ObservableProperty]
    private string _databaseUrl = string.Empty;

    [ObservableProperty]
    private string _authToken = string.Empty;

    [ObservableProperty]
    private bool _isAuthTokenVisible;

    [ObservableProperty]
    private string _deviceName = string.Empty;

    [ObservableProperty]
    private bool _autoSyncOnChanges = true;

    [ObservableProperty]
    private bool _autoSyncOnStartup = true;

    [ObservableProperty]
    private TursoSyncStatus _status = TursoSyncStatus.NotConfigured;

    [ObservableProperty]
    private string _statusMessage = "Sin configurar";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isTestingConnection;

    [ObservableProperty]
    private string _testConnectionResult = string.Empty;

    [ObservableProperty]
    private bool _isTestSuccessful;

    [ObservableProperty]
    private string _lastSyncText = "Nunca sincronizado";

    [ObservableProperty]
    private string _quotaSummaryText = "0% de cuota mensual gratuita utilizada";

    [ObservableProperty]
    private string _quotaDetailedText = string.Empty;

    /// <summary>El freno de consumo detuvo la sincronización automática (se quita con "Reanudar").</summary>
    [ObservableProperty]
    private bool _isConsumptionBrakeActive;

    /// <summary>Turso respondió BLOCKED: la sincronización automática se reintenta cada hora.</summary>
    [ObservableProperty]
    private bool _isQuotaExhausted;

    [ObservableProperty]
    private string _pauseNoticeText = string.Empty;

    /// <summary>"N cambios sin sincronizar: se reintentan solos…" (filas apartadas, paso 8.4); vacío si no hay.</summary>
    [ObservableProperty]
    private string _parkedNoticeText = string.Empty;

    [ObservableProperty]
    private bool _isHelpGuideOpen;

    [ObservableProperty]
    private string _saveStatusMessage = string.Empty;

    public TursoSyncViewModel(TursoSyncService syncService, TursoSettingsService settingsService)
    {
        _syncService = syncService;
        _settingsService = settingsService;

        _syncService.StatusChanged += (s, m) =>
        {
            RunOnUIThread(() =>
            {
                Status = s;
                StatusMessage = m;
                RefreshLastSyncText();
                RefreshQuotaDisplay();
                RefreshNotices();
            });
        };

        _syncService.SyncCompleted += (res) =>
        {
            RunOnUIThread(() =>
            {
                RefreshLastSyncText();
                RefreshQuotaDisplay();
                RefreshNotices();
            });
        };

        LoadFromSettings();
    }

    private static void RunOnUIThread(Action action)
    {
        var app = Application.Current;
        if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }

    public void LoadFromSettings()
    {
        var settings = _settingsService.CurrentSettings;
        IsSyncEnabled = settings.IsSyncEnabled;
        DatabaseUrl = settings.DatabaseUrl;
        AuthToken = settings.AuthToken;
        DeviceName = settings.DeviceName;
        AutoSyncOnChanges = settings.AutoSyncOnChanges;
        AutoSyncOnStartup = settings.AutoSyncOnStartup;

        Status = _syncService.Status;
        StatusMessage = _syncService.StatusMessage;

        _syncService.RefreshParkedCount();
        RefreshLastSyncText();
        RefreshQuotaDisplay();
        RefreshNotices();
    }

    private void RefreshLastSyncText()
    {
        var last = _settingsService.CurrentSettings.LastSyncUtc;
        LastSyncText = last.HasValue
            ? $"Última sincronización exitosa: {last.Value.ToLocalTime():dd/MM/yyyy HH:mm:ss}"
            : "Aún no se ha realizado ninguna sincronización con la nube.";
    }

    private void RefreshQuotaDisplay()
    {
        var quota = _settingsService.GetQuotaSummary();
        QuotaSummaryText = quota.FreeTierStatusDescription;
        QuotaDetailedText =
            $"• Lecturas este mes: {quota.RowsReadThisMonth:N0} de {TursoQuotaSummary.FreeTierMonthlyRowsReadLimit:N0} ({quota.PercentageReadsUsed:0.###}%)\n" +
            $"• Escrituras este mes: {quota.RowsWrittenThisMonth:N0} de {TursoQuotaSummary.FreeTierMonthlyRowsWriteLimit:N0} ({quota.PercentageWritesUsed:0.###}%)\n" +
            $"• Espacio estimado: ~{quota.EstimatedStorageMegabytes:0.##} MB de 5 GB\n" +
            $"• Hoy: {quota.RowsReadToday:N0} lecturas, {quota.RowsWrittenToday:N0} escrituras y {quota.FullPullsToday} descargas completas. " +
            $"El freno de consumo detiene la sincronización automática si pasa de {TursoConsumptionBrake.MaxRowsReadPerDay:N0} lecturas, " +
            $"{TursoConsumptionBrake.MaxRowsWrittenPerDay:N0} escrituras o {TursoConsumptionBrake.MaxFullPullsPerDay} descargas completas en el día.\n" +
            "Son cifras estimadas por este equipo; las reales están en el panel de Turso.";
    }

    private void RefreshNotices()
    {
        var parked = _syncService.ParkedCount;
        ParkedNoticeText = parked > 0
            ? $"{TursoSyncService.ParkedText(parked)}: registros que todavía no se pueden subir o aplicar (por ejemplo, porque aún no llega " +
              "el expediente o el lote al que pertenecen). Se reintentan solos en cada sincronización."
            : string.Empty;

        var reason = _syncService.PauseReason;
        IsConsumptionBrakeActive = reason == TursoSyncPauseReason.ConsumptionBrake;
        IsQuotaExhausted = reason == TursoSyncPauseReason.QuotaExhausted;
        // El título ya está en el estado, justo encima: el aviso da el detalle
        PauseNoticeText = reason switch
        {
            TursoSyncPauseReason.ConsumptionBrake => TursoConsumptionBrake.PausedDetail,
            TursoSyncPauseReason.QuotaExhausted => TursoException.QuotaExhaustedDetail,
            _ => string.Empty
        };
    }

    [RelayCommand]
    public void ToggleAuthTokenVisibility()
    {
        IsAuthTokenVisible = !IsAuthTokenVisible;
    }

    [RelayCommand]
    public void PasteAuthToken()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    AuthToken = TursoClient.CleanToken(text);
                    SaveStatusMessage = "✓ Token pegado desde el portapapeles.";
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al pegar token desde el portapapeles", ex);
        }
    }

    [RelayCommand]
    public void PasteDatabaseUrl()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    DatabaseUrl = text.Trim().Trim('"', '\'', '`');
                    SaveStatusMessage = "✓ URL pegada desde el portapapeles.";
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al pegar URL desde el portapapeles", ex);
        }
    }

    [RelayCommand]
    public async Task TestConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(DatabaseUrl))
        {
            TestConnectionResult = "✕ Falta la URL: Ingrese la dirección de su base de datos de Turso (ej. libsql://mi-clinica.turso.io).";
            IsTestSuccessful = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(AuthToken))
        {
            TestConnectionResult = "✕ Falta el Token: Ingrese el Token de autenticación de Turso (Bearer Token).";
            IsTestSuccessful = false;
            return;
        }

        IsTestingConnection = true;
        TestConnectionResult = "Conectando con Turso...";
        IsTestSuccessful = false;

        try
        {
            using var client = new TursoClient(DatabaseUrl, AuthToken);
            var (success, latency, error) = await client.TestConnectionAsync();
            if (success)
            {
                IsTestSuccessful = true;
                TestConnectionResult = $"✓ Conexión exitosa con Turso Cloud (Latencia: {latency} ms). Base de datos lista.";
            }
            else
            {
                IsTestSuccessful = false;
                TestConnectionResult = $"✕ {error}";
            }
        }
        catch (ArgumentException aex)
        {
            IsTestSuccessful = false;
            TestConnectionResult = $"✕ {aex.Message}";
        }
        catch (TursoException tex)
        {
            IsTestSuccessful = false;
            TestConnectionResult = $"✕ {tex.Message}";
        }
        catch (Exception ex)
        {
            IsTestSuccessful = false;
            TestConnectionResult = $"✕ Excepción al conectar: {ex.Message}";
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    [RelayCommand]
    public void SaveSettings()
    {
        var settings = _settingsService.CurrentSettings;
        settings.IsSyncEnabled = IsSyncEnabled;
        settings.DatabaseUrl = DatabaseUrl?.Trim() ?? string.Empty;
        settings.AuthToken = AuthToken?.Trim() ?? string.Empty;
        settings.DeviceName = string.IsNullOrWhiteSpace(DeviceName) ? Environment.MachineName : DeviceName.Trim();
        settings.DeviceRole = DeviceRole.WindowsDesktop;
        settings.AutoSyncOnChanges = AutoSyncOnChanges;
        settings.AutoSyncOnStartup = AutoSyncOnStartup;

        _settingsService.SaveSettings(settings);
        SaveStatusMessage = "✓ Configuración de Turso guardada correctamente.";
        Status = _syncService.Status;
        StatusMessage = _syncService.StatusMessage;
    }

    [RelayCommand]
    public async Task SyncNowAsync()
    {
        if (!IsSyncEnabled || string.IsNullOrWhiteSpace(DatabaseUrl))
        {
            StatusMessage = "Habilite la sincronización y configure la URL de Turso.";
            return;
        }

        IsBusy = true;
        SaveSettings();

        try
        {
            var res = await _syncService.SyncAsync(isBackgroundAutoSync: false);
            if (res.Success)
            {
                SaveStatusMessage = $"✓ Sincronización completada exitosamente ({res.RowsPushed} subidos, {res.RowsPulled} recibidos en {res.ElapsedMilliseconds} ms)";
            }
            else
            {
                SaveStatusMessage = $"✕ No se pudo sincronizar: {res.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            SaveStatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RefreshLastSyncText();
            RefreshQuotaDisplay();
            RefreshNotices();
        }
    }

    /// <summary>"Reanudar" después del freno de consumo: quita la pausa y sincroniza en ese momento.</summary>
    [RelayCommand]
    public async Task ResumeSyncAsync()
    {
        _syncService.ResumeAfterConsumptionBrake();
        RefreshNotices();
        RefreshQuotaDisplay();
        await SyncNowAsync();
    }

    [RelayCommand]
    public async Task InitialUploadAsync()
    {
        if (string.IsNullOrWhiteSpace(DatabaseUrl))
        {
            SaveStatusMessage = "Ingrese la URL de Turso antes de subir la base de datos.";
            return;
        }

        bool confirm = DialogService.Confirm(
            "Esta operación prepara el esquema en Turso Cloud y hace una sincronización COMPLETA: sube todos los datos de este equipo (productos, lotes, pacientes, consultas, citas, ventas, servicios y plantillas) y descarga todo lo que ya exista en la nube.\n\nSi un mismo registro cambió en dos equipos, se conserva la versión más reciente.\n\n¿Desea continuar?",
            "TPVC - Migración Inicial a Turso Cloud");

        if (!confirm) return;

        IsBusy = true;
        SaveSettings();

        try
        {
            var res = await _syncService.InitialUploadToTursoAsync();
            if (res.Success)
            {
                IsSyncEnabled = true;
                _settingsService.CurrentSettings.IsSyncEnabled = true;
                _settingsService.SaveSettings(_settingsService.CurrentSettings);
                SaveStatusMessage = $"✓ Base de datos subida a Turso exitosamente ({res.RowsPushed} registros subidos).";
            }
            else
            {
                SaveStatusMessage = $"✕ Error en la migración: {res.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            SaveStatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RefreshLastSyncText();
            RefreshQuotaDisplay();
            RefreshNotices();
        }
    }

    [RelayCommand]
    public void ToggleHelpGuide()
    {
        IsHelpGuideOpen = !IsHelpGuideOpen;
    }
}
