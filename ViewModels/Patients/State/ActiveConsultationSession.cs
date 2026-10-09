using System;
using CommunityToolkit.Mvvm.ComponentModel;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Consulta en curso (fase 6, paso 8a): lo que muestra del servicio de consultas (folio, paciente,
/// total) y el cronómetro. No escucha al servicio: el coordinador sincroniza y le pide avisar, en el
/// mismo orden de siempre.
/// </summary>
public partial class ActiveConsultationSession : ObservableObject
{
    private readonly ConsultationService? _consultationService;
    private readonly IUiThread _ui;
    private readonly IPatientsHubs _hubs;

    public ActiveConsultationSession(ConsultationService? consultationService, IUiThread ui, IPatientsHubs hubs)
    {
        _consultationService = consultationService;
        _ui = ui;
        _hubs = hubs;
    }

    public bool HasActiveConsultation => _consultationService?.HasActiveConsultation ?? false;
    public ActiveConsultation? ActiveConsultation => _consultationService?.CurrentConsultation;
    public string ActiveConsultationFolio => _consultationService?.CurrentConsultation?.Folio ?? string.Empty;
    public string ActiveConsultationPatientName => _consultationService?.CurrentConsultation?.PatientName ?? string.Empty;
    public decimal ActiveConsultationTotalAmount => _consultationService?.CurrentConsultation?.TotalAmount ?? 0m;

    [ObservableProperty]
    private string _activeConsultationElapsedTime = "00:00";

    /// <summary>El aviso de consulta en curso lleva el cronómetro: el coordinador lo actualiza aquí, antes del aviso.</summary>
    partial void OnActiveConsultationElapsedTimeChanged(string value) => _hubs.OnActiveConsultationElapsedTimeChanged();

    private System.Threading.Timer? _consultationTimer;

    /// <summary>Inicio, cargos y cierre de la consulta (lo primero que avisa SyncActiveConsultation).</summary>
    public void RaiseConsultationChanged()
    {
        OnPropertyChanged(nameof(HasActiveConsultation));
        OnPropertyChanged(nameof(ActiveConsultation));
        OnPropertyChanged(nameof(ActiveConsultationFolio));
        OnPropertyChanged(nameof(ActiveConsultationPatientName));
        OnPropertyChanged(nameof(ActiveConsultationTotalAmount));
    }

    /// <summary>Total de lo cargado (lo último que avisa la cuenta de la consulta).</summary>
    public void RaiseTotalAmount() => OnPropertyChanged(nameof(ActiveConsultationTotalAmount));

    public void StartConsultationTimer()
    {
        _consultationTimer?.Dispose();
        UpdateActiveConsultationTimer();
        _consultationTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                if (_ui.IsOffUiThread)
                {
                    _ui.InvokeAsync(() => UpdateActiveConsultationTimer());
                }
                else
                {
                    UpdateActiveConsultationTimer();
                }
            }
            catch
            {
                // Ignored if app is terminating or in test runner
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void StopConsultationTimer()
    {
        _consultationTimer?.Dispose();
        _consultationTimer = null;
        ActiveConsultationElapsedTime = "00:00";
    }

    public void UpdateActiveConsultationTimer(DateTime? asOf = null)
    {
        if (_consultationService?.CurrentConsultation != null)
        {
            ActiveConsultationElapsedTime = _consultationService.CurrentConsultation.GetFormattedElapsedTime(asOf);
        }
        else
        {
            ActiveConsultationElapsedTime = "00:00";
        }
    }
}
