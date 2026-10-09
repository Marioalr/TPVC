using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels.Patients;

namespace TPVC.ViewModels;

/// <summary>
/// Consulta entre equipos (fase E del rediseño): "Enviar a…" manda la consulta en curso a otro
/// equipo (PC ↔ PC o tablet) y aquí queda en solo lectura hasta que el otro la acepta (entonces se
/// cierra aquí, sin descartar nada) o se recupera. El que recibe ve el aviso en Pacientes y
/// "Continuar aquí" la abre con la misma nota, signos, receta, cuenta, folio y hora de inicio.
/// Nunca dos equipos editan la misma consulta: tomarla y recuperarla son instrucciones condicionadas
/// en la nube, y todo envío dudoso se resuelve contra la nube antes de volver a editarla.
/// </summary>
public partial class MedicalRecordsViewModel
{
    private const string LineProduct = "producto";
    private const string LineService = "servicio";
    private const string LineFree = "libre";

    private ConsultationHandoffService? _handoff;

    // Enviar, recuperar, recibir y aplicar lo revisado van de uno en uno
    private readonly SemaphoreSlim _handoffGate = new(1, 1);
    private ConsultationHandoffMarker? _outgoingHandoff;

    // Cambia con cada envío, recuperación, liberación o recepción: un resultado de una revisión
    // anterior ya no se aplica
    private long _handoffEpoch;

    private CancellationTokenSource? _handoffLoopCts;
    private CancellationTokenSource _handoffWakeCts = new();
    private volatile bool _inPatientsSection;
    private readonly HashSet<string> _dismissedIncoming = new();
    private readonly HashSet<string> _announcedIncoming = new();
    private readonly HashSet<string> _appliedIncoming = new();

    /// <summary>Expediente que no se edita aquí: su consulta está en otro equipo (o se está actualizando).</summary>
    private int? _handoffLockedPatientId;

    /// <summary>La consulta en pantalla se recibió de otro equipo (su envío y la ficha con que se tomó).</summary>
    private string? _receivedHandoffId;
    private string? _receivedHandoffToken;
    private readonly HashSet<string> _finalizedReceived = new();

    /// <summary>Cada cuánto se revisa la nube estando en Pacientes.</summary>
    public TimeSpan HandoffPollInterval { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>Fuera de Pacientes: solo "sigo en línea" y avisos.</summary>
    public TimeSpan HandoffIdlePollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Cuánto esperar a que termine otra sincronización antes de enviar o recibir.</summary>
    public TimeSpan HandoffSyncLockWait { get; set; } = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan HandoffRequestTimeout = TimeSpan.FromSeconds(20);

    private void InitializeHandoff(ConsultationHandoffService? handoff)
    {
        _handoff = handoff;
    }

    // ================================================================== estado visible

    [ObservableProperty]
    private bool _isConsultationHandedOff;

    [ObservableProperty]
    private bool _isSendingHandoff;

    [ObservableProperty]
    private bool _isRecoveringHandoff;

    [ObservableProperty]
    private bool _isReceivingHandoff;

    [ObservableProperty]
    private bool _isSendConsultationSheetOpen;

    [ObservableProperty]
    private bool _isLoadingHandoffDevices;

    [ObservableProperty]
    private ObservableCollection<LinkedDevice> _handoffDevices = new();

    [ObservableProperty]
    private LinkedDevice? _selectedHandoffDevice;

    [ObservableProperty]
    private string? _handoffSheetError;

    /// <summary>La última revisión de la nube funcionó.</summary>
    [ObservableProperty]
    private bool _isHandoffOnline = true;

    [ObservableProperty]
    private string _handoffOfflineReason = string.Empty;

    /// <summary>"Liberar este equipo": cuando el envío no se puede resolver (sin conexión, otra nube…).</summary>
    [ObservableProperty]
    private bool _canReleaseHandoffLocally;

    /// <summary>Línea extra del aviso de solo lectura (p. ej. "No se pudo confirmar el envío").</summary>
    [ObservableProperty]
    private string? _handoffBannerDetail;

    /// <summary>Consulta que otro equipo mandó a este (la más reciente sin atender).</summary>
    [ObservableProperty]
    private HandoffRow? _incomingHandoff;

    [ObservableProperty]
    private string _incomingHandoffDetail = string.Empty;

    /// <summary>Ya se tomó aquí pero falta abrirla (p. ej. se perdió la conexión al tomarla).</summary>
    [ObservableProperty]
    private bool _isIncomingHandoffAccepted;

    partial void OnSelectedHandoffDeviceChanged(LinkedDevice? value)
    {
        if (value is { IsAvailable: false }) SelectedHandoffDevice = null;
        NotifyHandoffState();
    }

    partial void OnIsSendingHandoffChanged(bool value) => NotifyHandoffState();
    partial void OnIsReceivingHandoffChanged(bool value) => NotifyHandoffState();
    partial void OnIsHandoffOnlineChanged(bool value) => NotifyHandoffState();
    partial void OnIncomingHandoffChanged(HandoffRow? value) => NotifyHandoffState();
    partial void OnIsLoadingHandoffDevicesChanged(bool value) => NotifyHandoffState();
    partial void OnHandoffSheetErrorChanged(string? value) => NotifyHandoffState();

    /// <summary>La nube está configurada en este equipo (sin ella, "Enviar a…" no aparece).</summary>
    public bool IsHandoffAvailable => _handoff?.IsConfigured == true;

    /// <summary>Se puede escribir en la consulta (no se envió ni se está enviando).</summary>
    public bool IsConsultationEditable => Active.HasActiveConsultation && !IsConsultationHandedOff && !IsSendingHandoff;

    public bool ShowsSendConsultationButton => IsHandoffAvailable && Active.HasActiveConsultation && !IsConsultationHandedOff;

    public bool CanSendConsultation => ShowsSendConsultationButton && !IsSendingHandoff && IsHandoffOnline;

    /// <summary>Por qué no se puede enviar (visible junto al botón, no solo en la descripción emergente).</summary>
    public string SendConsultationHint => ShowsSendConsultationButton && !IsHandoffOnline ? HandoffOfflineReason : string.Empty;

    public string HandoffTargetName => _outgoingHandoff?.ToDeviceName ?? string.Empty;

    public string HandoffBannerText => _outgoingHandoff == null
        ? string.Empty
        : _outgoingHandoff.Phase switch
        {
            HandoffPhase.Sent => $"Enviada a {_outgoingHandoff.ToDeviceName}: aquí queda en solo lectura hasta que la acepte",
            HandoffPhase.Unconfirmed => $"No se pudo confirmar el envío a {_outgoingHandoff.ToDeviceName}: en solo lectura hasta resolverlo",
            _ => $"Enviando a {_outgoingHandoff.ToDeviceName}…"
        };

    public string SendHandoffButtonText => SelectedHandoffDevice != null ? $"Enviar a {SelectedHandoffDevice.DeviceName}" : "Enviar";

    public bool CanConfirmSendHandoff => SelectedHandoffDevice is { IsAvailable: true } && !IsSendingHandoff && !IsLoadingHandoffDevices;

    public bool HasNoHandoffDevices => !IsLoadingHandoffDevices && HandoffDevices.Count == 0 && string.IsNullOrEmpty(HandoffSheetError);

    public bool ShowsIncomingHandoff => IncomingHandoff != null;

    /// <summary>El aviso se ve en el inicio y la ficha (en la consulta o el alta, un aviso breve).</summary>
    public bool ShowsIncomingHandoffNotice => ShowsIncomingHandoff && Navigation.ShowsPatientsTopBar;

    public string IncomingHandoffTitle => IncomingHandoff == null
        ? string.Empty
        : IsIncomingHandoffAccepted
            ? $"Consulta recibida de {IncomingHandoff.FromDeviceName}, pendiente de abrir"
            : $"{IncomingHandoff.FromDeviceName} te envió una consulta";

    public bool CanAcceptIncomingHandoff => IncomingHandoff != null && string.IsNullOrEmpty(IncomingHandoffBlockedReason);

    public string IncomingHandoffBlockedReason
    {
        get
        {
            if (IncomingHandoff == null) return string.Empty;
            if (IsReceivingHandoff) return "Recibiendo la consulta…";
            if (IsConsultationHandedOff) return "Primero recupere la consulta que envió a otro equipo";
            if (Active.HasActiveConsultation) return "Termine o descarte la consulta en curso para continuar esta aquí";
            if (Navigation.IsFormPage) return "Termine el alta o edición del expediente para continuarla aquí";
            if (IncomingHandoff.PayloadVersion > ConsultationHandoffService.ProtocolVersion) return "Actualice TPVC en este equipo para recibirla";
            return string.Empty;
        }
    }

    private void NotifyHandoffState()
    {
        OnPropertyChanged(nameof(IsHandoffAvailable));
        OnPropertyChanged(nameof(IsConsultationEditable));
        OnPropertyChanged(nameof(ShowsSendConsultationButton));
        OnPropertyChanged(nameof(CanSendConsultation));
        OnPropertyChanged(nameof(SendConsultationHint));
        OnPropertyChanged(nameof(HandoffTargetName));
        OnPropertyChanged(nameof(HandoffBannerText));
        OnPropertyChanged(nameof(SendHandoffButtonText));
        OnPropertyChanged(nameof(CanConfirmSendHandoff));
        OnPropertyChanged(nameof(HasNoHandoffDevices));
        OnPropertyChanged(nameof(ShowsIncomingHandoff));
        OnPropertyChanged(nameof(ShowsIncomingHandoffNotice));
        OnPropertyChanged(nameof(IncomingHandoffTitle));
        OnPropertyChanged(nameof(CanAcceptIncomingHandoff));
        OnPropertyChanged(nameof(IncomingHandoffBlockedReason));
        OnPropertyChanged(nameof(ConsultationBannerText));
    }

    // ================================================================== guardas

    /// <summary>La consulta se mandó a otro equipo (o se está mandando): aquí no se modifica.</summary>
    private bool BlockIfConsultationReadOnly()
    {
        if (!Active.HasActiveConsultation || (!IsConsultationHandedOff && !IsSendingHandoff)) return false;
        Feedback.ShowWarning(IsSendingHandoff
            ? "Se está enviando la consulta a otro equipo."
            : $"La consulta se envió a {HandoffTargetName}: aquí está en solo lectura. Use \"Recuperar\" para seguirla en este equipo.");
        return true;
    }

    /// <summary>El expediente no se edita aquí mientras su consulta está en otro equipo.</summary>
    private bool BlockIfPatientHandedOff(PatientRecord? patient)
    {
        if (patient == null || _handoffLockedPatientId != patient.Id) return false;
        Feedback.ShowWarning(IsConsultationHandedOff
            ? $"La consulta de {patient.FullName} está en {HandoffTargetName}: el expediente se edita allá (o aquí al recuperarla)."
            : "Se está actualizando el expediente con lo del otro equipo. Intente en unos segundos.");
        return true;
    }

    private bool BlockIfReceivingHandoff()
    {
        if (!IsReceivingHandoff) return false;
        Feedback.ShowWarning("Se está recibiendo una consulta de otro equipo. Espere un momento.");
        return true;
    }

    // ================================================================== revisión periódica

    /// <summary>Revisión en segundo plano (la inicia la ventana principal; las pruebas revisan a mano).</summary>
    public void StartHandoffPolling()
    {
        if (_handoff == null || _handoffLoopCts != null) return;
        _handoffLoopCts = new CancellationTokenSource();
        var ct = _handoffLoopCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_handoff.AutoPoll) await PollHandoffsOnceAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Falló la revisión de consultas entre equipos", ex);
                }

                var wake = Volatile.Read(ref _handoffWakeCts);
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, wake.Token);
                    await Task.Delay(_inPatientsSection ? HandoffPollInterval : HandoffIdlePollInterval, linked.Token);
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested) break;
                }
            }
        }, ct);
    }

    public void StopHandoffPolling()
    {
        _handoffLoopCts?.Cancel();
        _handoffLoopCts = null;
    }

    private void WakeHandoffPolling()
    {
        var previous = Interlocked.Exchange(ref _handoffWakeCts, new CancellationTokenSource());
        try { previous.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Fuera de Pacientes se revisa con menos frecuencia.</summary>
    public void LeavePatientsSection() => _inPatientsSection = false;

    private void OnEnterPatientsSectionForHandoff()
    {
        _inPatientsSection = true;
        _dismissedIncoming.Clear();
        WakeHandoffPolling();
    }

    /// <summary>Una revisión: avisos de consultas recibidas, estado del envío propio y conexión.</summary>
    public async Task PollHandoffsOnceAsync(CancellationToken ct = default)
    {
        if (_handoff == null) return;
        if (!_handoff.IsConfigured)
        {
            await OnUiAsync(() =>
            {
                IsHandoffOnline = true;
                IncomingHandoff = null;
                if (_outgoingHandoff != null)
                {
                    CanReleaseHandoffLocally = true;
                    HandoffBannerDetail = "La nube está desactivada en este equipo: no se puede confirmar el envío.";
                }
                NotifyHandoffState();
            });
            return;
        }

        // Si hay un envío o una recepción en curso, esta vuelta se salta
        if (!await _handoffGate.WaitAsync(0, ct)) return;
        try
        {
            var epoch = Interlocked.Read(ref _handoffEpoch);
            var outgoing = _outgoingHandoff;
            bool outgoingUsable = outgoing != null && OutgoingMatchesSettings(outgoing);

            HandoffPollResult result;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(HandoffRequestTimeout);
                result = await _handoff.PollAsync(outgoingUsable && outgoing!.Phase == HandoffPhase.Sent ? outgoing.Id : null, timeout.Token);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await OnUiAsync(() => SetHandoffConnectivity(false, ConsultationHandoffService.Describe(ex)));
                return;
            }

            await OnUiAsync(() => SetHandoffConnectivity(true, string.Empty));

            if (outgoing != null && !outgoingUsable)
            {
                await OnUiAsync(() =>
                {
                    if (_outgoingHandoff?.Id != outgoing.Id) return;
                    CanReleaseHandoffLocally = true;
                    HandoffBannerDetail = "Cambió la nube o el equipo configurado: este equipo no puede confirmar el envío.";
                });
            }
            else if (outgoing != null && Interlocked.Read(ref _handoffEpoch) == epoch)
            {
                if (outgoing.Phase == HandoffPhase.Sent)
                {
                    if (result.OutgoingState == ConsultationHandoffService.StateAccepted)
                    {
                        bool released = false;
                        await OnUiAsync(() => released = ReleaseHandedOffConsultation(outgoing,
                            $"La consulta de {outgoing.PatientName} continúa en {outgoing.ToDeviceName}"));
                        if (released && MarkerGoneFromDisk(outgoing.Id)) _ = AcknowledgeHandoffAsync(outgoing.Id);
                    }
                    else if (result.OutgoingState == ConsultationHandoffService.StateCancelled)
                    {
                        // Solo este equipo cancela (p. ej. se cerró el programa justo después de recuperarla)
                        await ResolveOutgoingCoreAsync(outgoing, userInitiated: false);
                    }
                    else if (result.OutgoingState == string.Empty)
                    {
                        // Un envío confirmado no desaparece solo (la limpieza espera a que este equipo lo vea):
                        // nunca se vuelve editable sin saber dónde siguió
                        await OnUiAsync(() =>
                        {
                            if (_outgoingHandoff?.Id != outgoing.Id) return;
                            CanReleaseHandoffLocally = true;
                            HandoffBannerDetail = "No se encontró este envío en la nube (¿se cambió o restauró la base?). Si la consulta no siguió en el otro equipo, libere este.";
                        });
                    }
                }
                else
                {
                    // Enviando o dudoso (p. ej. tras cerrar el programa a medio envío)
                    await ResolveOutgoingCoreAsync(outgoing, userInitiated: false);
                }
            }

            await HandleIncomingAsync(result, epoch);
        }
        finally
        {
            _handoffGate.Release();
        }
    }

    private bool OutgoingMatchesSettings(ConsultationHandoffMarker marker) =>
        _handoff != null && _handoff.IsConfigured
        && string.Equals(marker.DatabaseUrl, _handoff.DatabaseUrl, StringComparison.OrdinalIgnoreCase)
        && marker.FromDeviceId == _handoff.DeviceId;

    private void SetHandoffConnectivity(bool online, string reason)
    {
        IsHandoffOnline = online;
        HandoffOfflineReason = online ? string.Empty : $"{reason}: no se puede enviar ahora";
        NotifyHandoffState();
    }

    private async Task HandleIncomingAsync(HandoffPollResult result, long epoch)
    {
        // Todo en el hilo de la interfaz: los conjuntos de vistas/descartadas también cambian ahí
        await OnUiAsync(() =>
        {
            if (Interlocked.Read(ref _handoffEpoch) != epoch) return;

            HandoffRow? acceptedPending = null;
            foreach (var row in result.Incoming.Where(r => r.State == ConsultationHandoffService.StateAccepted))
            {
                if (_appliedIncoming.Contains(row.Id) || _draftStore?.IsHandoffApplied(row.Id) == true)
                {
                    // Ya está guardada aquí: solo falta borrar su copia de la nube
                    if (_draftStore?.GetAcceptToken(row.Id) is { } token && _draftStore.IsHandoffApplied(row.Id))
                    {
                        _ = ClearIncomingPayloadAsync(row.Id, token);
                    }
                    continue;
                }
                // Solo las que tomó este equipo (con su ficha); falta abrirlas
                if (_draftStore?.IsAcceptToken(row.Id, row.AcceptToken) == true) acceptedPending ??= row;
            }

            var offered = acceptedPending ?? result.Incoming
                .Where(r => r.State == ConsultationHandoffService.StateSent && !_dismissedIncoming.Contains(r.Id))
                .OrderByDescending(r => r.CreatedAtUtc)
                .FirstOrDefault();
            ShowIncomingNotice(offered, accepted: offered != null && ReferenceEquals(offered, acceptedPending));

            foreach (var row in result.Incoming.Where(r => r.State == ConsultationHandoffService.StateSent))
            {
                if (!_announcedIncoming.Add(row.Id)) continue;
                // En Pacientes ya se ve el aviso; fuera de ahí (o con otra consulta abierta), un aviso breve
                if (!_inPatientsSection || Active.HasActiveConsultation)
                {
                    Feedback.Info($"{row.FromDeviceName} te envió la consulta de {row.PatientName}. Ábrela en Pacientes.");
                }
            }
        });
    }

    private void ShowIncomingNotice(HandoffRow? row, bool accepted)
    {
        IsIncomingHandoffAccepted = row != null && accepted;
        if (row == null)
        {
            IncomingHandoff = null;
            IncomingHandoffDetail = string.Empty;
            NotifyHandoffState();
            return;
        }

        if (IncomingHandoff?.Id != row.Id || IncomingHandoff.State != row.State)
        {
            IncomingHandoffDetail = DescribeIncoming(row);
            IncomingHandoff = row;
        }
        NotifyHandoffState();
    }

    private static string DescribeIncoming(HandoffRow row)
    {
        var parts = new List<string> { row.PatientName };
        var payload = ConsultationDraftStore.Deserialize(row.Payload);
        if (payload != null)
        {
            if (payload.StartedAt != default) parts.Add($"iniciada {payload.StartedAt:HH:mm}");
            var diagnosis = (payload.Diagnosis ?? string.Empty).Split('\n')[0].Trim();
            if (diagnosis.Length > 0) parts.Add(diagnosis.Length > 60 ? diagnosis[..60] + "…" : diagnosis);
            var total = payload.Items.Sum(i => i.UnitPrice * i.Quantity);
            if (total > 0) parts.Add($"cargos {total.ToString("C2", Mexican)}");
        }
        return string.Join(" · ", parts);
    }

    /// <summary>"Más tarde": el aviso se oculta hasta volver a entrar a Pacientes.</summary>
    [RelayCommand]
    public void DismissIncomingHandoff()
    {
        if (IncomingHandoff == null) return;
        _dismissedIncoming.Add(IncomingHandoff.Id);
        ShowIncomingNotice(null, accepted: false);
    }

    // ================================================================== quien envía

    [RelayCommand]
    public async Task OpenSendConsultationSheetAsync()
    {
        if (_handoff == null || !ShowsSendConsultationButton) return;
        if (BlockIfConsultationReadOnly()) return;

        HandoffSheetError = null;
        SelectedHandoffDevice = null;
        HandoffDevices = new ObservableCollection<LinkedDevice>();
        IsSendConsultationSheetOpen = true;
        IsLoadingHandoffDevices = true;
        try
        {
            using var timeout = new CancellationTokenSource(HandoffRequestTimeout);
            var devices = await _handoff.GetLinkedDevicesAsync(timeout.Token);
            HandoffDevices = new ObservableCollection<LinkedDevice>(devices);
            SelectedHandoffDevice = devices.FirstOrDefault(d => d.IsAvailable && d.IsOnline) ?? devices.FirstOrDefault(d => d.IsAvailable);
            SetHandoffConnectivity(true, string.Empty);
        }
        catch (Exception ex)
        {
            HandoffSheetError = $"No se pudo consultar los equipos: {ConsultationHandoffService.Describe(ex)}.";
            if (ConsultationHandoffService.IsConnectivityFailure(ex)) SetHandoffConnectivity(false, ConsultationHandoffService.Describe(ex));
        }
        finally
        {
            IsLoadingHandoffDevices = false;
            NotifyHandoffState();
        }
    }

    [RelayCommand]
    public void CloseSendConsultationSheet()
    {
        // Mientras se envía no se cierra: la consulta quedaría en solo lectura sin explicación
        if (IsSendingHandoff) return;
        IsSendConsultationSheetOpen = false;
    }

    /// <summary>
    /// Sube lo pendiente del expediente, deja la consulta en solo lectura (con la marca guardada en
    /// disco) y publica la copia para el otro equipo.
    /// </summary>
    [RelayCommand]
    public async Task SendConsultationToSelectedDeviceAsync()
    {
        var device = SelectedHandoffDevice;
        if (_handoff == null || _inventoryService == null || _consultationService?.CurrentConsultation == null) return;
        if (device == null || !device.IsAvailable || IsSendingHandoff || !IsConsultationEditable) return;

        if (!await _handoffGate.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            HandoffSheetError = "Se está revisando la nube. Intente de nuevo en un momento.";
            return;
        }

        IsSendingHandoff = true;
        HandoffSheetError = null;
        Interlocked.Increment(ref _handoffEpoch);
        try
        {
            // 1. Lo pendiente del expediente (receta en curso, avance de nota) sube antes
            var push = await _handoff.Sync.SyncAsync(isBackgroundAutoSync: false, lockWait: HandoffSyncLockWait);
            if (!push.Success)
            {
                HandoffSheetError = $"No se pudo subir el expediente a la nube: {push.ErrorMessage}";
                return;
            }

            // 2. La copia con identificadores que valen en cualquier equipo
            var payload = await BuildHandoffPayloadAsync();
            if (payload == null || payload.PatientSyncId is not Guid patientSyncId)
            {
                HandoffSheetError = "No se pudo preparar la copia de la consulta.";
                return;
            }

            var marker = new ConsultationHandoffMarker
            {
                Id = Guid.NewGuid().ToString(),
                ToDeviceId = device.DeviceId,
                ToDeviceName = device.DeviceName,
                FromDeviceId = _handoff.DeviceId,
                FromDeviceName = _handoff.DeviceName,
                DatabaseUrl = _handoff.DatabaseUrl,
                PatientSyncId = TursoSyncService.FormatGuid(patientSyncId),
                PatientName = payload.PatientName,
                Folio = payload.Folio,
                Phase = HandoffPhase.Sending,
                CreatedAt = DateTime.Now
            };

            // 3. Solo lectura aquí ANTES de que el otro equipo pueda verla, y que sobreviva a un cierre
            EnterHandedOffState(marker);
            if (!SaveConsultationDraftChecked())
            {
                LeaveHandedOffState(marker);
                HandoffSheetError = "No se pudo guardar la consulta en este equipo; no se envió.";
                return;
            }

            // 4. Publicarla
            try
            {
                await _handoff.SendAsync(marker, ConsultationDraftStore.Serialize(payload));
                marker.Phase = HandoffPhase.Sent;
                SaveConsultationDraftChecked();
                IsSendConsultationSheetOpen = false;
                Feedback.ShowSuccess($"Consulta enviada a {device.DeviceName}");
            }
            catch (Exception ex) when (ConsultationHandoffService.IsDefinitiveRejection(ex))
            {
                LeaveHandedOffState(marker);
                HandoffSheetError = $"No se envió: {ConsultationHandoffService.Describe(ex)}.";
            }
            catch (Exception ex)
            {
                // Pudo llegar o no: se resuelve contra la nube antes de volver a editarla aquí
                AppLogger.Warn($"Envío de consulta dudoso ({ex.Message}); se resuelve contra la nube.");
                marker.Phase = HandoffPhase.Unconfirmed;
                SaveConsultationDraftChecked();
                IsSendConsultationSheetOpen = false;
                if (!await ResolveOutgoingCoreAsync(marker, userInitiated: false))
                {
                    HandoffBannerDetail = "No se pudo confirmar el envío. Use \"Recuperar\" en cuanto haya conexión.";
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo enviar la consulta a otro equipo", ex);
            HandoffSheetError = $"No se pudo enviar: {ex.Message}";
        }
        finally
        {
            IsSendingHandoff = false;
            NotifyHandoffState();
            _handoffGate.Release();
        }
    }

    private void EnterHandedOffState(ConsultationHandoffMarker marker)
    {
        _outgoingHandoff = marker;
        _handoffLockedPatientId = Active.ActiveConsultation?.PatientId;
        HandoffBannerDetail = null;
        CanReleaseHandoffLocally = false;
        IsConsultationHandedOff = true;
        _consultationService?.SetActiveConsultationHandedOff(true);
        CloseConsultationSheets();
        NotifyHandoffState();
    }

    /// <summary>Al abrir el programa con una consulta enviada: vuelve en solo lectura.</summary>
    private void RestoreOutgoingHandoff(ConsultationHandoffMarker marker)
    {
        _outgoingHandoff = marker;
        _handoffLockedPatientId = Active.ActiveConsultation?.PatientId;
        IsConsultationHandedOff = true;
        _consultationService?.SetActiveConsultationHandedOff(true);
        NotifyHandoffState();
    }

    /// <summary>El envío no se hizo: la consulta vuelve a ser editable aquí.</summary>
    private void LeaveHandedOffState(ConsultationHandoffMarker marker)
    {
        if (_outgoingHandoff?.Id != marker.Id) return;
        Interlocked.Increment(ref _handoffEpoch);
        _outgoingHandoff = null;
        _handoffLockedPatientId = null;
        HandoffBannerDetail = null;
        CanReleaseHandoffLocally = false;
        IsConsultationHandedOff = false;
        _consultationService?.SetActiveConsultationHandedOff(false);
        SaveConsultationDraftChecked();
        NotifyHandoffState();
    }

    private void CloseConsultationSheets()
    {
        Prescription.IsPrescriptionModalOpen = false;
        Supplies.IsSuppliesModalOpen = false;
        ClinicalServices.IsServicesModalOpen = false;
        CatalogBrowser.IsCatalogBrowserModalOpen = false;
        Preview.IsPrescriptionPdfModalOpen = false;
        Prescription.IsAllergyAlertOpen = false;
        Agenda.IsAgendaPanelOpen = false;
    }

    /// <summary>
    /// La copia que viaja: el expediente, los productos, servicios, la cita y el honorario van por su
    /// identificador de sincronización (los Id locales no sirven en otro equipo).
    /// </summary>
    private async Task<ConsultationDraft?> BuildHandoffPayloadAsync()
    {
        var active = _consultationService?.CurrentConsultation;
        if (active == null || _inventoryService == null) return null;
        var draft = BuildConsultationDraft();
        if (draft == null) return null;

        var patient = Session.Patients.FirstOrDefault(p => p.Id == active.PatientId)
                      ?? (Record.Patient?.Id == active.PatientId ? Record.Patient : null)
                      ?? (Session.SelectedPatient?.Id == active.PatientId ? Session.SelectedPatient : null);
        if (patient == null || patient.SyncId == Guid.Empty) return null;

        var refs = await _inventoryService.GetCatalogRefsAsync();
        draft.Handoff = null;
        draft.ReceivedHandoffId = null;
        draft.ReceivedHandoffToken = null;
        draft.PatientId = 0;
        draft.AttendingAppointmentId = null;
        draft.SavedAt = DateTime.Now;
        draft.PatientSyncId = patient.SyncId;
        draft.PatientUpdatedAtUtc = await _inventoryService.GetPatientUpdatedAtAsync(patient.Id);
        draft.ProgressNoteSyncId = await _inventoryService.FindConsultationNoteSyncIdAsync(patient.Id, draft.Folio);
        draft.AttendingAppointmentSyncId = _attendingAppointmentId is int appointmentId
            ? await _inventoryService.GetAppointmentSyncIdAsync(appointmentId)
            : null;
        var fee = Account.ConsultationFeeService;
        draft.FeeServiceSyncId = fee != null && fee.Id > 0 ? refs.FirstOrDefault(r => r.Kind == CatalogRefKind.Service && r.Id == fee.Id)?.SyncId : null;

        foreach (var item in draft.Items)
        {
            CatalogRef? match = item.ProductId switch
            {
                > 0 => refs.FirstOrDefault(r => r.Kind == CatalogRefKind.Product && r.Id == item.ProductId),
                < 0 and > -1_000_000 => refs.FirstOrDefault(r => r.Kind == CatalogRefKind.Service && r.Id == -item.ProductId),
                _ => null
            };
            item.LineKind = match?.Kind switch
            {
                CatalogRefKind.Product => LineProduct,
                CatalogRefKind.Service => LineService,
                _ => LineFree
            };
            item.CatalogSyncId = match?.SyncId;
            item.ProductId = 0;
        }
        return draft;
    }

    /// <summary>"Recuperar": vuelve a este equipo si el otro no la ha aceptado.</summary>
    [RelayCommand]
    public async Task RecoverHandedOffConsultationAsync()
    {
        var marker = _outgoingHandoff;
        if (marker == null || _handoff == null || IsRecoveringHandoff) return;
        if (!OutgoingMatchesSettings(marker))
        {
            CanReleaseHandoffLocally = true;
            HandoffBannerDetail = _handoff.IsConfigured
                ? "Cambió la nube o el equipo configurado: este equipo no puede confirmar el envío."
                : "La nube está desactivada en este equipo: no se puede confirmar el envío.";
            return;
        }

        if (!await _handoffGate.WaitAsync(TimeSpan.FromSeconds(15)))
        {
            Feedback.ShowWarning("Se está revisando la nube. Intente de nuevo en un momento.");
            return;
        }
        IsRecoveringHandoff = true;
        try
        {
            if (_outgoingHandoff?.Id == marker.Id) await ResolveOutgoingCoreAsync(marker, userInitiated: true);
        }
        finally
        {
            IsRecoveringHandoff = false;
            _handoffGate.Release();
        }
    }

    /// <summary>
    /// Resuelve contra la nube cómo quedó un envío: aceptada (se cierra aquí) o cancelada (vuelve a
    /// ser editable). Devuelve false si no hubo conexión (sigue en solo lectura).
    /// </summary>
    private async Task<bool> ResolveOutgoingCoreAsync(ConsultationHandoffMarker marker, bool userInitiated)
    {
        if (_handoff == null) return false;
        HandoffResolution resolution;
        try
        {
            using var timeout = new CancellationTokenSource(HandoffRequestTimeout);
            resolution = await _handoff.ResolveOutgoingAsync(marker, timeout.Token);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"No se pudo resolver el envío {marker.Id}: {ex.Message}");
            await OnUiAsync(() =>
            {
                if (_outgoingHandoff?.Id != marker.Id) return;
                // Liberar solo cuando lo enviado sí llegó (sigue en la nube para el otro equipo); un
                // envío dudoso se resuelve en cuanto haya conexión
                CanReleaseHandoffLocally = marker.Phase == HandoffPhase.Sent;
                if (marker.Phase != HandoffPhase.Sent)
                {
                    HandoffBannerDetail = "No se pudo confirmar el envío. Se resolverá solo en cuanto haya conexión.";
                }
                if (userInitiated)
                {
                    HandoffBannerDetail = $"No se pudo recuperar: {ConsultationHandoffService.Describe(ex)}. Intente de nuevo o libere este equipo.";
                }
                NotifyHandoffState();
            });
            return false;
        }

        bool applied = false;
        await OnUiAsync(() =>
        {
            // Si mientras tanto se liberó o resolvió de otra forma, no se cambia nada ni se avisa
            if (_outgoingHandoff?.Id != marker.Id) return;
            applied = true;
            if (resolution == HandoffResolution.Accepted)
            {
                ReleaseHandedOffConsultation(marker, $"La consulta de {marker.PatientName} continúa en {marker.ToDeviceName}");
            }
            else
            {
                LeaveHandedOffState(marker);
                Feedback.ShowSuccess(userInitiated
                    ? "La consulta volvió a este equipo"
                    : $"No se confirmó el envío a {marker.ToDeviceName}: la consulta sigue en este equipo");
            }
        });
        // La nube puede borrar el envío solo cuando este equipo ya no lo tiene pendiente en disco
        if (applied && MarkerGoneFromDisk(marker.Id)) _ = AcknowledgeHandoffAsync(marker.Id);
        return true;
    }

    /// <summary>
    /// El otro equipo la aceptó: se cierra aquí SIN descartar (la nota, la receta y la cuenta siguen
    /// allá) y sin tocar el expediente; después se descarga lo del otro equipo.
    /// </summary>
    private bool ReleaseHandedOffConsultation(ConsultationHandoffMarker marker, string message)
    {
        if (_outgoingHandoff?.Id != marker.Id) return false;
        Interlocked.Increment(ref _handoffEpoch);
        var patientId = Active.ActiveConsultation?.PatientId;

        // Sin estas referencias el cierre no regresa la receta ni el resumen del expediente
        Record.Abandon();
        _isCurrentConsultationSaved = false;
        _attendingAppointmentId = null;
        Account.FeeServiceOverrideId = null;
        IsSendConsultationSheetOpen = false;
        CloseConsultationSheets();
        IsFinishConsultationOpen = false;

        // El cierre borra el borrador local (la consulta ya no es de este equipo)
        _consultationService?.CloseActiveConsultation();
        _outgoingHandoff = null;
        IsConsultationHandedOff = false;
        CanReleaseHandoffLocally = false;
        HandoffBannerDetail = null;
        Active.StopConsultationTimer();
        ClearConsultationFields();

        if (Navigation.IsConsultationPage)
        {
            if (Session.SelectedPatient != null) Navigation.PatientsPage = PageChart;
            else Navigation.GoToPatientsHome();
        }
        NotifyHandoffState();
        NotifyPatientsPageState();
        Feedback.ShowSuccess(message);

        // El expediente se edita aquí otra vez cuando llegue lo del otro equipo (o si no hay conexión)
        _handoffLockedPatientId = patientId;
        _ = RefreshAfterHandoffAsync(patientId);
        return true;
    }

    /// <summary>La marca de "enviada" ya no está en el borrador del disco (o no hay borrador).</summary>
    private bool MarkerGoneFromDisk(string markerId)
    {
        if (_draftStore == null || !_draftStore.Exists) return true;
        return _draftStore.Load()?.Handoff?.Id != markerId;
    }

    private async Task RefreshAfterHandoffAsync(int? patientId)
    {
        try
        {
            if (_handoff != null) await _handoff.Sync.SyncAsync(isBackgroundAutoSync: false, lockWait: HandoffSyncLockWait);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"No se pudo actualizar tras pasar la consulta a otro equipo: {ex.Message}");
        }

        await OnUiAsync(async () =>
        {
            try
            {
                if (patientId is int id) Session.ForceFreshPatientIds.Add(id);
                await LoadPatientsAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error("No se pudo recargar el expediente tras pasar la consulta a otro equipo", ex);
            }
            finally
            {
                if (_outgoingHandoff == null && _handoffLockedPatientId == patientId) _handoffLockedPatientId = null;
            }
        });
    }

    private async Task AcknowledgeHandoffAsync(string handoffId)
    {
        try
        {
            if (_handoff != null) await _handoff.AcknowledgeAsync(handoffId);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"No se pudo confirmar el fin del envío {handoffId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Sin conexión (u otra nube configurada) el envío no se puede resolver: este equipo deja de
    /// esperarla. La copia se aparta en un archivo; si el otro equipo la acepta, sigue allá.
    /// </summary>
    [RelayCommand]
    public async Task ReleaseHandoffLocallyAsync()
    {
        var marker = _outgoingHandoff;
        if (marker == null || IsRecoveringHandoff) return;

        var question = marker.Phase == HandoffPhase.Sent
            ? $"La consulta de {marker.PatientName} se quitará de este equipo sin confirmar con la nube.\n\n" +
              $"Si {marker.ToDeviceName} ya la aceptó, continúa allá. Si no, sigue en la nube para que {marker.ToDeviceName} la acepte.\n\n" +
              "¿Liberar este equipo?"
            : $"No se sabe si la consulta de {marker.PatientName} llegó a {marker.ToDeviceName}.\n\n" +
              "Se quitará de este equipo y su copia se apartará en un archivo (no se pierde, pero no se podrá seguir aquí).\n\n" +
              "¿Liberar este equipo?";
        if (!await _dialogs.ConfirmAsync(question, "Liberar este equipo")) return;

        // Con la misma compuerta que recuperar y revisar: nunca a la mitad de una resolución
        if (!await _handoffGate.WaitAsync(0))
        {
            Feedback.ShowWarning("Se está revisando la nube. Intente de nuevo en un momento.");
            return;
        }
        try
        {
            if (_outgoingHandoff?.Id != marker.Id) return; // se resolvió mientras se confirmaba
            var archived = _draftStore?.ArchiveDraft(marker.Id);
            AppLogger.Warn($"Equipo liberado de la consulta enviada {marker.Id} ({marker.Folio}); copia apartada en {archived ?? "(sin copia)"}.");
            ReleaseHandedOffConsultation(marker, "Este equipo quedó libre");
        }
        finally
        {
            _handoffGate.Release();
        }
    }

    // ================================================================== quien recibe

    /// <summary>"Continuar aquí": descarga lo último, la toma (si nadie más lo hizo) y la abre.</summary>
    [RelayCommand]
    public async Task AcceptIncomingHandoffAsync()
    {
        var row = IncomingHandoff;
        if (_handoff == null || row == null || IsReceivingHandoff) return;
        if (!CanAcceptIncomingHandoff)
        {
            Feedback.ShowWarning(IncomingHandoffBlockedReason);
            return;
        }

        IsReceivingHandoff = true;
        try
        {
            // La consulta propia sin terminar (si la hay) se recupera primero
            await InitialLoad;
            if (Active.HasActiveConsultation || Navigation.IsFormPage)
            {
                Feedback.ShowWarning(IncomingHandoffBlockedReason.Length > 0 ? IncomingHandoffBlockedReason : "Termine lo que está abierto para continuarla aquí.");
                return;
            }

            if (!await _handoffGate.WaitAsync(TimeSpan.FromSeconds(15)))
            {
                Feedback.ShowWarning("Se está revisando la nube. Intente de nuevo en un momento.");
                return;
            }
            try
            {
                await AcceptIncomingCoreAsync(row);
            }
            finally
            {
                _handoffGate.Release();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo recibir la consulta de otro equipo", ex);
            Feedback.ShowWarning($"No se pudo recibir la consulta: {ex.Message}");
        }
        finally
        {
            IsReceivingHandoff = false;
        }
    }

    private sealed record MappedHandoff(PatientRecord Patient, ConsultationDraft Draft, List<string> FreeLines);

    private async Task AcceptIncomingCoreAsync(HandoffRow row)
    {
        Interlocked.Increment(ref _handoffEpoch);
        var payload = ConsultationDraftStore.Deserialize(row.Payload);
        if (payload == null || row.PayloadVersion > ConsultationHandoffService.ProtocolVersion || payload.Version > ConsultationDraft.CurrentVersion)
        {
            Feedback.ShowWarning("Esta consulta viene de una versión más nueva de TPVC. Actualice este equipo para recibirla.");
            return;
        }

        // 1. Lo último del expediente (el otro equipo lo subió antes de enviarla)
        var pull = await _handoff!.Sync.SyncAsync(isBackgroundAutoSync: false, lockWait: HandoffSyncLockWait);
        if (!pull.Success)
        {
            Feedback.ShowWarning($"No se pudo actualizar desde la nube: {pull.ErrorMessage}");
            return;
        }

        // 2. Paciente, cargos, cita y honorario de este equipo
        var (mapped, error) = await MapIncomingDraftAsync(payload, row);
        if (mapped == null)
        {
            Feedback.ShowWarning(error ?? "No se pudo preparar la consulta recibida.");
            return;
        }

        // 3. Tomarla. Si este equipo ya lo intentó (p. ej. se perdió la respuesta), se pregunta con la
        //    ficha de entonces: nunca se reemplaza, o su propio intento parecería de otro equipo
        string? token = _draftStore?.GetAcceptToken(row.Id);
        if (token != null)
        {
            (string? State, string? Owner) current;
            try
            {
                current = await _handoff.GetAcceptStateAsync(row.Id);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"No se pudo consultar la consulta recibida {row.Id}: {ex.Message}");
                Feedback.ShowWarning("Sin conexión: si la consulta quedó tomada en este equipo, aparecerá para abrirla en cuanto vuelva la conexión.");
                return;
            }

            if (current.State == ConsultationHandoffService.StateSent)
            {
                if (!await TryTakeIncomingAsync(row, token)) return;
            }
            else if (current.State != ConsultationHandoffService.StateAccepted || current.Owner != token)
            {
                Feedback.ShowWarning(current.State == ConsultationHandoffService.StateCancelled
                    ? $"{row.FromDeviceName} recuperó la consulta."
                    : current.State == ConsultationHandoffService.StateAccepted
                        ? "Otro equipo ya tomó esta consulta."
                        : "La consulta ya no está disponible.");
                _dismissedIncoming.Add(row.Id);
                ShowIncomingNotice(null, accepted: false);
                return;
            }
        }
        else
        {
            token = Guid.NewGuid().ToString("N");
            if (_draftStore != null && !_draftStore.RememberAcceptToken(row.Id, token))
            {
                Feedback.ShowWarning("No se pudo preparar este equipo para recibir la consulta (no se pudo escribir en el disco).");
                return;
            }
            if (!await TryTakeIncomingAsync(row, token)) return;
        }

        // 4. Abrirla, sin más esperas entre tomarla y guardarla aquí
        ApplyReceivedConsultation(row, mapped, token);
    }

    /// <summary>Toma la consulta; si la respuesta se pierde, pregunta a la nube quién se quedó con ella.</summary>
    private async Task<bool> TryTakeIncomingAsync(HandoffRow row, string token)
    {
        try
        {
            if (await _handoff!.TryAcceptAsync(row.Id, token)) return true;
        }
        catch (Exception ex) when (ConsultationHandoffService.IsDefinitiveRejection(ex))
        {
            Feedback.ShowWarning($"No se pudo tomar la consulta: {ConsultationHandoffService.Describe(ex)}.");
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Respuesta dudosa al tomar la consulta {row.Id}: {ex.Message}");
        }

        try
        {
            var (state, owner) = await _handoff!.GetAcceptStateAsync(row.Id);
            if (state == ConsultationHandoffService.StateAccepted && owner == token) return true;

            if (state == ConsultationHandoffService.StateSent)
            {
                Feedback.ShowWarning("No se pudo tomar la consulta. Intente de nuevo.");
            }
            else
            {
                Feedback.ShowWarning(state == ConsultationHandoffService.StateCancelled
                    ? $"{row.FromDeviceName} recuperó la consulta."
                    : state == ConsultationHandoffService.StateAccepted
                        ? "Otro equipo ya tomó esta consulta."
                        : "La consulta ya no está disponible.");
                _dismissedIncoming.Add(row.Id);
                ShowIncomingNotice(null, accepted: false);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"No se pudo confirmar si se tomó la consulta {row.Id}: {ex.Message}");
            Feedback.ShowWarning("Sin conexión: si la consulta quedó tomada en este equipo, aparecerá para abrirla en cuanto vuelva la conexión.");
        }
        return false;
    }

    private async Task<(MappedHandoff? Mapped, string? Error)> MapIncomingDraftAsync(ConsultationDraft payload, HandoffRow row)
    {
        if (_inventoryService == null || payload.PatientSyncId is not Guid patientSyncId)
        {
            return (null, "La copia recibida no trae el expediente.");
        }

        // Instancias recién leídas de la base (no la que estuviera abierta con datos de antes)
        var selected = Session.SelectedPatient;
        if (selected != null && selected.SyncId == patientSyncId) Session.ForceFreshPatientIds.Add(selected.Id);
        await LoadPatientsAsync();
        await LoadServicesAsync();

        var patient = Session.Patients.FirstOrDefault(p => p.SyncId == patientSyncId);
        if (patient == null)
        {
            return (null, $"Todavía no llega el expediente de {row.PatientName} a este equipo. Intente de nuevo en unos segundos.");
        }
        if (payload.PatientUpdatedAtUtc is DateTime version && patient.UpdatedAtUtc < version)
        {
            return (null, $"Todavía no llegan los últimos cambios del expediente de {row.PatientName}. Intente de nuevo en unos segundos.");
        }
        if (payload.ProgressNoteSyncId is Guid noteSyncId && !await _inventoryService.ConsultationNoteExistsAsync(noteSyncId))
        {
            return (null, "Todavía no llega el avance de la nota de esta consulta. Intente de nuevo en unos segundos.");
        }

        var refs = await _inventoryService.GetCatalogRefsAsync();
        var freeLines = new List<string>();
        foreach (var item in payload.Items)
        {
            var kind = item.LineKind switch
            {
                LineProduct => (CatalogRefKind?)CatalogRefKind.Product,
                LineService => CatalogRefKind.Service,
                _ => null
            };
            var match = kind == null ? null : FindCatalogRef(refs, kind.Value, item);
            if (match == null)
            {
                if (kind != null) freeLines.Add(item.Name);
                item.ProductId = ConsultationAccount.FreeChargeId(item.Name);
            }
            else
            {
                item.ProductId = match.Kind == CatalogRefKind.Product ? match.Id : -match.Id;
            }
        }

        int? appointmentId = payload.AttendingAppointmentSyncId is Guid appointmentSyncId
            ? await _inventoryService.FindAppointmentIdAsync(appointmentSyncId)
            : null;
        payload.PatientId = patient.Id;
        payload.AttendingAppointmentId = appointmentId;
        payload.Handoff = null;
        return (new MappedHandoff(patient, payload, freeLines), null);
    }

    /// <summary>Por identificador; si no llegó, el mismo código Y nombre (activo).</summary>
    private static CatalogRef? FindCatalogRef(List<CatalogRef> refs, CatalogRefKind kind, ConsultationProductItem item)
    {
        if (item.CatalogSyncId is Guid syncId)
        {
            var bySync = refs.FirstOrDefault(r => r.Kind == kind && r.SyncId == syncId && r.IsActive);
            if (bySync != null) return bySync;
        }
        var name = TextSearch.Normalize(item.Name);
        return refs.FirstOrDefault(r => r.Kind == kind && r.IsActive
                                        && string.Equals(r.Code, item.Code, StringComparison.OrdinalIgnoreCase)
                                        && TextSearch.Normalize(r.Name) == name);
    }

    private void ApplyReceivedConsultation(HandoffRow row, MappedHandoff mapped, string token)
    {
        CloseConsultationSheets();
        IsSendConsultationSheetOpen = false;

        // Con su envío y su ficha: al quedar guardada en disco se borra la copia de la nube y no se
        // vuelve a ofrecer, aunque el programa se cierre antes
        mapped.Draft.ReceivedHandoffId = row.Id;
        mapped.Draft.ReceivedHandoffToken = token;
        _appliedIncoming.Add(row.Id);

        ApplyConsultationDraft(mapped.Patient, mapped.Draft);
        IsConsultationRecovered = false;
        if (!SaveConsultationDraftChecked())
        {
            Feedback.ShowWarning("La consulta se abrió, pero todavía no se pudo guardar su copia en este equipo; la de la nube se conserva hasta lograrlo.");
        }

        _dismissedIncoming.Add(row.Id);
        ShowIncomingNotice(null, accepted: false);
        AutosaveStatus = $"Recibida de {row.FromDeviceName} · {DateTime.Now:HH:mm}";
        Navigation.PatientsPage = PageConsultation;
        NotifyAccountChanged();
        NotifyHandoffState();
        Feedback.ShowSuccess($"Consulta de {row.PatientName} recibida de {row.FromDeviceName}");
        if (mapped.FreeLines.Count > 0)
        {
            Feedback.ShowWarning($"No están en el catálogo de este equipo y se cobrarán sin descontar existencias: {string.Join(", ", mapped.FreeLines)}.");
        }
    }

    /// <summary>
    /// La copia de una consulta recibida quedó en disco (al abrirla, en un guardado posterior o al
    /// volver a abrir el programa): se anota como recibida y se borra su copia de la nube.
    /// </summary>
    private void OnReceivedHandoffPersisted(ConsultationDraft draft)
    {
        var id = draft.ReceivedHandoffId;
        if (string.IsNullOrEmpty(id) || _draftStore == null) return;
        lock (_finalizedReceived)
        {
            if (!_finalizedReceived.Add(id)) return;
        }
        if (!_draftStore.IsHandoffApplied(id) && !_draftStore.MarkHandoffApplied(id))
        {
            lock (_finalizedReceived) _finalizedReceived.Remove(id);
            return;
        }
        if (!string.IsNullOrEmpty(draft.ReceivedHandoffToken)) _ = ClearIncomingPayloadAsync(id, draft.ReceivedHandoffToken);
    }

    private async Task ClearIncomingPayloadAsync(string handoffId, string token)
    {
        try
        {
            if (_handoff != null) await _handoff.ClearPayloadAsync(handoffId, token);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"No se pudo borrar de la nube la copia de la consulta {handoffId}: {ex.Message}");
        }
    }

    // ================================================================== hilo de la interfaz

    private Task OnUiAsync(Action action) => OnUiAsync(() =>
    {
        action();
        return Task.CompletedTask;
    });

    /// <summary>Ejecuta en el hilo de la interfaz y espera a que termine (sin interfaz, aquí mismo).</summary>
    private Task OnUiAsync(Func<Task> action) => _ui.RunAsync(action);

    private void DisposeHandoff()
    {
        StopHandoffPolling();
    }
}
