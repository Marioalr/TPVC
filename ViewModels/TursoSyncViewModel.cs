using System;
using System.Collections.ObjectModel;
using System.Linq;
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

    // Avisos del servicio que siguen en la cola de la interfaz (solo se toca dentro de _uiQueueGate)
    private readonly object _uiQueueGate = new();
    private int _pendingUiUpdates;

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

    /// <summary>"La hora de esta computadora difiere N min de la nube…" (paso 8.7); vacío si no pasa de 2 min.</summary>
    [ObservableProperty]
    private string _clockNoticeText = string.Empty;

    /// <summary>La nube pertenece a otra base y no se sincroniza (paso 8.7); vacío si no.</summary>
    [ObservableProperty]
    private string _identityNoticeText = string.Empty;

    /// <summary>
    /// Paso 8.9: un respaldo restaurado esperando decisión, un reemplazo de la nube a medias o la espera porque otra PC la
    /// reemplaza; vacío si no.
    /// </summary>
    [ObservableProperty]
    private string _generationNoticeText = string.Empty;

    /// <summary>"N cambios guardados para «Volver a aplicar»…" (paso 8.9); vacío si no hay.</summary>
    [ObservableProperty]
    private string _savedChangesNoticeText = string.Empty;

    /// <summary>Esta base todavía no tiene identidad: puede unirse a la de la nube si ya la usaba.</summary>
    [ObservableProperty]
    private bool _canAdoptCloudIdentity;

    /// <summary>Hay conflicto de identidad: se ofrece «Reemplazar con la nube» (paso 8.8).</summary>
    [ObservableProperty]
    private bool _canReplaceWithCloud;

    /// <summary>El panel de «Reemplazar con la nube», que pide teclear CONFIRMAR.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmReplaceWithCloud))]
    private bool _isReplaceWithCloudOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmReplaceWithCloud))]
    private string _replaceConfirmWord = string.Empty;

    public bool CanConfirmReplaceWithCloud => IsReplaceWithCloudOpen && !IsBusy && DataImportService.ValidateConfirmationWord(ReplaceConfirmWord);

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanConfirmReplaceWithCloud));

    /// <summary>El reporte de la última «Sincronización completa» en esta sesión.</summary>
    [ObservableProperty]
    private string _fullSyncReportText = string.Empty;

    [ObservableProperty]
    private bool _isFullSyncReportClean;

    /// <summary>
    /// Este equipo tomó un DeviceId nuevo (configuración copiada de otra computadora u otra instalación con el mismo; paso
    /// 8.10); vacío si no hay aviso.
    /// </summary>
    [ObservableProperty]
    private string _deviceIdNoticeText = string.Empty;

    /// <summary>Equipos vinculados (paso 8.10): los que usan esta nube, menos los quitados.</summary>
    public ObservableCollection<LinkedDevice> Devices { get; } = new();

    /// <summary>Leer la lista al abrir la sección (las pruebas de pantalla la apagan y la leen a mano).</summary>
    public bool RefreshDevicesOnOpen { get; set; } = true;

    [ObservableProperty]
    private bool _isLoadingDevices;

    /// <summary>"Por ahora solo esta computadora…", o por qué no se pudo leer la lista.</summary>
    [ObservableProperty]
    private string _devicesStatusText = string.Empty;

    /// <summary>Quién es la PC principal, o si esta computadora está quitada.</summary>
    [ObservableProperty]
    private string _primaryText = string.Empty;

    [ObservableProperty]
    private bool _canMakeThisThePrimary;

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

    /// <summary>
    /// Lleva a la interfaz un aviso del servicio sin esperarla: un Invoke síncrono detenía el hilo
    /// de la sincronización mientras la interfaz estuviera ocupada (y para siempre si el hilo del
    /// despachador ya había terminado). InvokeAsync con la misma prioridad conserva el orden de los
    /// avisos; uno que llega en el hilo de la interfaz mientras otros siguen en cola también se
    /// encola, para no adelantárseles.
    /// </summary>
    private void RunOnUIThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null)
        {
            lock (_uiQueueGate)
            {
                if (!dispatcher.CheckAccess() || _pendingUiUpdates > 0)
                {
                    _pendingUiUpdates++;
                    dispatcher.InvokeAsync(() =>
                    {
                        try { action(); }
                        finally { lock (_uiQueueGate) _pendingUiUpdates--; }
                    });
                    return;
                }
            }
        }

        action();
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
        _syncService.RefreshSavedChangesCount();
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

        ClockNoticeText = _syncService.ClockWarningText is { } clock
            ? $"{clock}. Mientras tanto TPVC marca los cambios con la hora de la nube; conviene corregir la fecha y hora de Windows."
            : string.Empty;

        GenerationNoticeText = _syncService.IsCloudReplacementPending
            ? "El reemplazo de la nube con el respaldo de esta computadora quedó a medias. «Sincronizar ahora» lo termina; mientras " +
              "tanto los demás equipos esperan sin bajar una nube incompleta."
            : _syncService.IsRestoreDecisionPending
                ? TursoSyncService.RestorePendingMessage
                : _syncService.IsWaitingForCloudReplacement
                    ? "Otra computadora está reemplazando la nube con un respaldo. La sincronización sigue sola cuando termine: esta " +
                      "computadora guarda lo que no había subido, vuelve a bajar todo y ofrece «Volver a aplicar»."
                    : string.Empty;

        var saved = _syncService.SavedChangesCount;
        SavedChangesNoticeText = saved > 0
            ? (saved == 1 ? "1 cambio guardado" : $"{saved:N0} cambios guardados") + " para «Volver a aplicar»: lo que esta computadora no alcanzó a subir antes de que la nube " +
              "se reemplazara con un respaldo, o lo que la nube tenía y el respaldo no. «Volver a aplicar» los escribe aquí de nuevo y " +
              "suben en la siguiente sincronización."
            : string.Empty;

        DeviceIdNoticeText = _settingsService.CurrentSettings.DeviceIdNotice ?? string.Empty;

        var conflict = _syncService.IdentityConflict;
        CanAdoptCloudIdentity = conflict is { LocalClinicId: null };
        CanReplaceWithCloud = conflict != null;
        if (conflict == null) IsReplaceWithCloudOpen = false;
        IdentityNoticeText = conflict == null
            ? string.Empty
            : conflict.LocalClinicId == null
                ? "Esta nube ya tiene datos de otra computadora y esta base todavía no está unida a ella, así que no se sincroniza " +
                  "para no mezclar datos. En una PC nueva (o para recuperar una descompuesta), pulse «Unirse a esta nube»: baja todo " +
                  "lo de la nube. Si esta PC ya sincronizaba con ella, «Esta PC ya usaba esta nube». Si es otra base, revise la URL."
                : "Esta nube pertenece a otra base de datos (otro consultorio o una nube reemplazada), así que no se sincroniza " +
                  "para no mezclar datos. Revise la URL y el token, o use «Reemplazar con la nube» para quedarse con lo de la nube.";
    }

    /// <summary>
    /// «Unirse a esta nube» (también «Recuperar desde la nube» en una PC nueva): solo con una base sin datos propios.
    /// Respalda, borra el catálogo inicial y baja todo lo de la nube.
    /// </summary>
    [RelayCommand]
    public async Task JoinCloudAsync()
    {
        bool confirm = DialogService.Confirm(
            "TPVC guardará un respaldo de esta base, quitará las categorías y los servicios de ejemplo y bajará todo lo de la nube " +
            "(catálogo, inventario, pacientes, consultas, citas y ventas). Puede tardar varios minutos si la nube tiene muchos datos; " +
            "si se corta, la siguiente sincronización sigue donde iba.\n\n" +
            "Solo procede si esta base no tiene datos propios.\n\n¿Desea continuar?",
            "TPVC - Unirse a esta nube");
        if (!confirm) return;

        await RunCloudDownloadAsync(_syncService.JoinCloudAsync);
    }

    [RelayCommand]
    public void OpenReplaceWithCloud()
    {
        ReplaceConfirmWord = string.Empty;
        IsReplaceWithCloudOpen = true;
    }

    [RelayCommand]
    public void CancelReplaceWithCloud()
    {
        IsReplaceWithCloudOpen = false;
        ReplaceConfirmWord = string.Empty;
    }

    /// <summary>«Reemplazar con la nube»: borra lo local (con respaldo antes) y baja todo lo de la nube. Pide CONFIRMAR.</summary>
    [RelayCommand]
    public async Task ReplaceWithCloudAsync()
    {
        if (!CanConfirmReplaceWithCloud) return;
        IsReplaceWithCloudOpen = false;
        ReplaceConfirmWord = string.Empty;
        await RunCloudDownloadAsync(_syncService.ReplaceWithCloudAsync);
    }

    private async Task RunCloudDownloadAsync(Func<System.Threading.CancellationToken, Task<(bool Ok, string Message)>> prepare)
    {
        IsBusy = true;
        try
        {
            var (ok, message) = await prepare(default);
            SaveStatusMessage = ok ? message : $"✕ {message}";
            RefreshNotices();
            if (ok) await SyncNowAsync();
        }
        finally
        {
            IsBusy = false;
            RefreshNotices();
        }
    }

    /// <summary>«Volver a aplicar N cambios» (paso 8.9).</summary>
    [RelayCommand]
    public async Task ReapplySavedChangesAsync()
    {
        var summary = await _syncService.GetSavedChangesSummaryAsync();
        bool confirm = DialogService.Confirm(
            $"Se volverán a escribir en esta computadora {summary.Total:N0} cambios guardados (con hora nueva, así ganan y suben en la " +
            $"siguiente sincronización):\n• {string.Join("\n• ", summary.Lines)}\n\n¿Desea continuar?",
            "TPVC - Volver a aplicar");
        if (!confirm) return;

        IsBusy = true;
        try
        {
            var (ok, message) = await _syncService.ReapplySavedChangesAsync();
            SaveStatusMessage = ok ? message : $"✕ {message}";
            if (ok) await SyncNowAsync();
        }
        finally
        {
            IsBusy = false;
            RefreshNotices();
        }
    }

    /// <summary>Descarta los cambios guardados (el archivo del reemplazo sigue en la carpeta de respaldos).</summary>
    [RelayCommand]
    public async Task DiscardSavedChangesAsync()
    {
        bool confirm = DialogService.Confirm(
            "Se descartarán los cambios guardados para «Volver a aplicar». Si vinieron de un reemplazo de la nube, su archivo sigue en la " +
            "carpeta de respaldos y se puede volver a agregar.\n\n¿Desea continuar?",
            "TPVC - Descartar cambios guardados");
        if (!confirm) return;
        await _syncService.DiscardSavedChangesAsync();
        SaveStatusMessage = "Se descartaron los cambios guardados.";
        RefreshNotices();
    }

    /// <summary>Agrega a «Volver a aplicar» un archivo guardado al reemplazar la nube (de esta u otra computadora).</summary>
    [RelayCommand]
    public async Task ImportSavedChangesFileAsync()
    {
        var path = DialogService.ShowOpenFileDialog(
            "Cambios guardados al reemplazar la nube (nube-antes-de-reemplazar-*.db)|*.db|Todos los archivos (*.*)|*.*",
            "Elegir el archivo de cambios guardados");
        if (string.IsNullOrWhiteSpace(path)) return;
        var (ok, message) = await _syncService.ImportSavedChangesFileAsync(path);
        SaveStatusMessage = ok ? message : $"✕ {message}";
        RefreshNotices();
    }

    /// <summary>«Esta PC ya usaba esta nube»: une esta base a la identidad de la nube si todo lo que subió está en ella.</summary>
    [RelayCommand]
    public async Task AdoptCloudIdentityAsync()
    {
        bool confirm = DialogService.Confirm(
            "TPVC revisará que todo lo que esta computadora ya subió esté en la nube (lee la lista de registros de la nube una vez). " +
            "Si es así, la une a ella y la sincronización sigue normal; si falta algo, no cambia nada.\n\n¿Desea continuar?",
            "TPVC - Esta PC ya usaba esta nube");
        if (!confirm) return;

        IsBusy = true;
        try
        {
            var (ok, message) = await _syncService.AdoptCloudIdentityAsync();
            SaveStatusMessage = ok ? message : $"✕ {message}";
            RefreshNotices();
            if (ok) await SyncNowAsync();
        }
        finally
        {
            IsBusy = false;
            RefreshNotices();
        }
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
                SaveStatusMessage = $"✓ Sincronización completa terminada ({res.RowsPushed} registros subidos, {res.RowsPulled} recibidos).";
                FullSyncReportText = res.FullSyncReport?.Summary ?? string.Empty;
                IsFullSyncReportClean = res.FullSyncReport?.IsClean == true;
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

    // ================================================================== equipos vinculados (paso 8.10)

    /// <summary>Lee la lista de la nube (escribe antes el latido de esta PC). Al abrir la sección y con «Actualizar».</summary>
    [RelayCommand]
    public async Task RefreshDevicesAsync()
    {
        var settings = _settingsService.CurrentSettings;
        if (!settings.IsSyncEnabled || string.IsNullOrWhiteSpace(settings.DatabaseUrl) || string.IsNullOrWhiteSpace(settings.AuthToken))
        {
            Devices.Clear();
            DevicesStatusText = "Active la sincronización para ver los equipos que usan esta nube.";
            RefreshPrimary();
            return;
        }
        if (IsLoadingDevices) return;

        IsLoadingDevices = true;
        try
        {
            var devices = await _syncService.GetDevicesAsync();
            Devices.Clear();
            foreach (var device in devices) Devices.Add(device);
            DevicesStatusText = devices.Count(d => !d.IsThisDevice) == 0
                ? "Por ahora solo esta computadora usa la nube. La tablet y el celular aparecerán aquí al vincularlos."
                : string.Empty;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"No se pudo leer la lista de equipos: {ex.Message}");
            DevicesStatusText = $"No se pudo leer la lista de equipos: {ConsultationHandoffService.Describe(ex)}.";
        }
        finally
        {
            IsLoadingDevices = false;
            RefreshPrimary();
            RefreshNotices();
        }
    }

    /// <summary>La PC principal según la lista (o el último registro) y si esta puede tomar su lugar.</summary>
    private void RefreshPrimary()
    {
        var registration = _syncService.Registration;
        var thisDevice = Devices.FirstOrDefault(d => d.IsThisDevice);
        var primary = Devices.FirstOrDefault(d => d.IsPrimary);
        bool thisIsPrimary = thisDevice?.IsPrimary ?? registration?.IsPrimary ?? false;
        var code = thisDevice?.DeviceCode ?? registration?.DeviceCode;

        if (registration is { IsRevoked: true })
        {
            PrimaryText = $"Esta computadora se quitó de los equipos vinculados el {registration.RevokedAtUtc!.Value.ToLocalTime():dd/MM/yyyy HH:mm}" +
                          (registration.RevokedReason == TursoSyncService.RevokedReplacedReason ? ", porque otra quedó como la PC principal." : ".") +
                          " Si va a seguir siendo la del consultorio, pulse «Esta PC reemplaza a la principal».";
            CanMakeThisThePrimary = true;
        }
        else if (thisIsPrimary)
        {
            PrimaryText = $"Esta computadora es la PC principal{(code != null ? $" (código {code})" : string.Empty)}: la única que regularizará los folios " +
                          "hechos sin conexión y creará los saldos iniciales del inventario.";
            CanMakeThisThePrimary = false;
        }
        else if (primary != null || registration?.PrimaryDeviceId != null)
        {
            var name = primary?.DeviceName ?? registration?.PrimaryName ?? "otra computadora";
            var seen = primary?.SeenText.ToLowerInvariant() ?? (registration?.PrimaryLastSeenUtc is { } last
                ? $"visto {ConsultationHandoffService.DescribeLongAgo(DateTime.UtcNow - last)}"
                : "sin latido reciente");
            PrimaryText = $"La PC principal es «{name}» ({seen}). Si esta computadora la reemplaza, por ejemplo porque la anterior se " +
                          "descompuso, pulse «Esta PC reemplaza a la principal»." +
                          (code != null ? $" El código de esta es {code}." : string.Empty);
            CanMakeThisThePrimary = true;
        }
        else
        {
            PrimaryText = Devices.Count > 0 || registration != null
                ? "La PC principal se asigna en la siguiente sincronización con la nube verificada."
                : string.Empty;
            CanMakeThisThePrimary = false;
        }
    }

    /// <summary>«Esta PC reemplaza a la principal»: toma la clave de la nube y quita a la anterior.</summary>
    [RelayCommand]
    public async Task MakeThisThePrimaryAsync()
    {
        var previous = Devices.FirstOrDefault(d => d.IsPrimary && !d.IsThisDevice);
        var detail = previous == null
            ? string.Empty
            : $" en lugar de «{previous.DeviceName}» ({previous.SeenText.ToLowerInvariant()})" +
              (previous.IsOnline ? ". ¡Atención: esa computadora está en línea ahora mismo!" : string.Empty);
        bool confirm = DialogService.Confirm(
            $"Esta computadora quedará como la PC principal{detail}. La anterior se quita de los equipos vinculados.\n\n" +
            "Úselo cuando la PC principal se descompuso o se cambió por esta.\n\n¿Desea continuar?",
            "TPVC - Esta PC reemplaza a la principal");
        if (!confirm) return;

        IsBusy = true;
        try
        {
            var (ok, message) = await _syncService.MakeThisThePrimaryAsync();
            SaveStatusMessage = ok ? message : $"✕ {message}";
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshDevicesAsync();
    }

    /// <summary>«Quitar» un equipo: muestra antes sus cambios sin subir.</summary>
    [RelayCommand]
    public async Task RemoveDeviceAsync(LinkedDevice? device)
    {
        if (device == null || !device.CanRemove) return;
        var pending = device.PendingCount is > 0
            ? $"\n\n{device.PendingText}: todavía no están en la nube y se perderían si ese equipo ya no se vuelve a conectar."
            : $"\n\n{device.PendingText}.";
        bool confirm = DialogService.Confirm(
            $"¿Quitar «{device.DeviceName}» ({device.RoleText.ToLowerInvariant()}, {device.SeenText.ToLowerInvariant()}) de los equipos vinculados?" +
            pending + "\n\nDeja de aparecer aquí y de recibir consultas de otros equipos. Su código no se vuelve a usar.",
            "TPVC - Quitar equipo");
        if (!confirm) return;

        IsBusy = true;
        try
        {
            var (ok, message) = await _syncService.RemoveDeviceAsync(device);
            SaveStatusMessage = ok ? message : $"✕ {message}";
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshDevicesAsync();
    }

    /// <summary>«Entendido» en el aviso de la identidad nueva de este equipo.</summary>
    [RelayCommand]
    public void DismissDeviceIdNotice()
    {
        _settingsService.DismissDeviceIdNotice();
        RefreshNotices();
    }
}
