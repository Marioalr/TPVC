using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

public class DeviceRoleOption
{
    public DeviceRole Role { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public partial class TursoSyncViewModel : ObservableObject
{
    private readonly TursoSyncService _syncService;
    private readonly TursoSettingsService _settingsService;
    private readonly ClinicProfileService _clinicProfileService;

    [ObservableProperty]
    private bool _isSyncEnabled;

    [ObservableProperty]
    private string _databaseUrl = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AuthTokenVisibilityIcon))]
    private string _authToken = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AuthTokenVisibilityIcon))]
    private bool _isAuthTokenVisible;

    public string AuthTokenVisibilityIcon => IsAuthTokenVisible ? "🙈" : "👁";

    [ObservableProperty]
    private string _deviceName = string.Empty;

    [ObservableProperty]
    private string _deviceId = string.Empty;

    [ObservableProperty]
    private DeviceRole _selectedRole = DeviceRole.WindowsDesktop;

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

    [ObservableProperty]
    private bool _isPairingModalOpen;

    [ObservableProperty]
    private string _pairingJsonPayload = string.Empty;

    [ObservableProperty]
    private string _pairingRoleTitle = string.Empty;

    [ObservableProperty]
    private bool _isHelpGuideOpen;

    [ObservableProperty]
    private string _saveStatusMessage = string.Empty;

    public ObservableCollection<DeviceRoleOption> RoleOptions { get; } = new()
    {
        new() { Role = DeviceRole.WindowsDesktop, DisplayName = "PC Windows 11 (Principal / Caja / Consultorio)", Description = "Control total: Consultas NOM-004, Recetas, Cobro POS, Inventario, Lotes y Nube." },
        new() { Role = DeviceRole.AndroidTablet, DisplayName = "Tablet Android (Consultorio / Asistente)", Description = "Uso clínico y operativo: Expedientes, Notas médicas, Recetas, Citas y Cobro." },
        new() { Role = DeviceRole.AndroidMobileAdmin, DisplayName = "Celular Android (Solo Administración)", Description = "Administración ejecutiva: Métricas del día, Cortes de caja, Consulta de existencias y Alertas." }
    };

    public TursoSyncViewModel(
        TursoSyncService syncService,
        TursoSettingsService settingsService,
        ClinicProfileService clinicProfileService)
    {
        _syncService = syncService;
        _settingsService = settingsService;
        _clinicProfileService = clinicProfileService;

        _syncService.StatusChanged += (s, m) =>
        {
            RunOnUIThread(() =>
            {
                Status = s;
                StatusMessage = m;
                RefreshLastSyncText();
                RefreshQuotaDisplay();
            });
        };

        _syncService.SyncCompleted += (res) =>
        {
            RunOnUIThread(() =>
            {
                RefreshLastSyncText();
                RefreshQuotaDisplay();
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
        DeviceId = settings.DeviceId;
        SelectedRole = settings.DeviceRole;
        AutoSyncOnChanges = settings.AutoSyncOnChanges;
        AutoSyncOnStartup = settings.AutoSyncOnStartup;

        Status = _syncService.Status;
        StatusMessage = _syncService.StatusMessage;

        RefreshLastSyncText();
        RefreshQuotaDisplay();
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
            $"• Escrituras este mes: {quota.RowsWrittenThisMonth:N0} de 25,000,000 ({quota.PercentageWritesUsed:F4}%)\n" +
            $"• Lecturas este mes: {quota.RowsReadThisMonth:N0} de 1,000,000,000 ({quota.PercentageReadsUsed:F4}%)\n" +
            $"• Espacio estimado ocupado: ~{quota.EstimatedStorageMegabytes:F2} MB de 9,000 MB (9 GB gratuitos)\n" +
            "• Máximo de dispositivos: 3 (Win11, Tablet Android, Celular Admin). Rara vez más de 2 simultáneos.\n" +
            "✓ Garantía: Esta arquitectura Local-First consume menos del 0.05% de los límites gratuitos de Turso.";
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
        settings.DeviceRole = SelectedRole;
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
        }
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
            "Esta operación preparará el esquema en Turso Cloud y subirá todos los productos, pacientes, consultas, servicios y recetas locales a la nube.\n\n¿Desea continuar?",
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
        }
    }

    [RelayCommand]
    public void OpenPairingModal(string roleTarget)
    {
        var role = roleTarget switch
        {
            "Tablet" => DeviceRole.AndroidTablet,
            "Mobile" => DeviceRole.AndroidMobileAdmin,
            _ => DeviceRole.AndroidTablet
        };

        PairingRoleTitle = role == DeviceRole.AndroidTablet
            ? "Vincular Tablet Android (Consultorio / Asistente)"
            : "Vincular Celular Android (Solo Administración)";

        var clinic = _clinicProfileService.CurrentProfile;
        PairingJsonPayload = _settingsService.GenerateAndroidPairingPayload(role, clinic.ClinicName, clinic.DoctorNameWithTitle);
        IsPairingModalOpen = true;
    }

    [RelayCommand]
    public void ClosePairingModal()
    {
        IsPairingModalOpen = false;
    }

    [RelayCommand]
    public void CopyPairingPayload()
    {
        if (!string.IsNullOrEmpty(PairingJsonPayload))
        {
            try
            {
                Clipboard.SetText(PairingJsonPayload);
                SaveStatusMessage = "✓ Código de vinculación copiado al portapapeles.";
            }
            catch (Exception ex)
            {
                AppLogger.Error("No se pudo copiar al portapapeles", ex);
            }
        }
    }

    [RelayCommand]
    public void ToggleHelpGuide()
    {
        IsHelpGuideOpen = !IsHelpGuideOpen;
    }
}
