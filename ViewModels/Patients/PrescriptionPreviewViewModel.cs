using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Vista previa de la receta (fase 6, paso 13): la hoja que se ve, se imprime y se exporta con sus
/// indicaciones. El plan que escribe el médico en la consulta en curso llega por currentTreatmentPlan;
/// el coordinador avisa (RaisePrescriptionIndications) cuando cambia lo que las indicaciones leen.
/// </summary>
public partial class PrescriptionPreviewViewModel : ObservableObject
{
    private readonly PatientSession _session;
    private readonly ActiveConsultationSession _active;
    private readonly ClinicInfo _clinic;
    private readonly InfoDialogState _info;
    private readonly ClinicProfileService? _clinicProfileService;
    private readonly IPrescriptionOutput? _printingService;
    private readonly Func<string> _currentTreatmentPlan;

    public PrescriptionPreviewViewModel(
        PatientSession session,
        ActiveConsultationSession active,
        ClinicInfo clinic,
        InfoDialogState info,
        ClinicProfileService? clinicProfileService,
        IPrescriptionOutput? printingService,
        Func<string> currentTreatmentPlan)
    {
        _session = session;
        _active = active;
        _clinic = clinic;
        _info = info;
        _clinicProfileService = clinicProfileService;
        _printingService = printingService;
        _currentTreatmentPlan = currentTreatmentPlan;
    }

    [ObservableProperty]
    private bool _isPrescriptionPdfModalOpen;

    /// <summary>Lo que se guarda como plan cuando la consulta termina sin plan escrito.</summary>
    internal const string SavedTreatmentPlanPlaceholder = "Tratamiento e indicaciones médicas según receta.";

    /// <summary>
    /// Indicaciones de la receta (vista previa, impresión y PDF): en consulta, el plan e indicaciones
    /// que escribe el médico; fuera de ella, las de la última consulta guardada; si no hay, el texto general.
    /// Antes la receta siempre llevaba el texto general aunque el médico hubiera escrito otras.
    /// </summary>
    public string PrescriptionIndications
    {
        get
        {
            if (_active.HasActiveConsultation && _session.SelectedPatient != null && _active.ActiveConsultation?.PatientId == _session.SelectedPatient.Id)
            {
                return string.IsNullOrWhiteSpace(_currentTreatmentPlan())
                    ? PrescriptionDefaults.Indications
                    : _currentTreatmentPlan().Trim();
            }
            var saved = _session.ConsultationsHistory.FirstOrDefault()?.TreatmentPlan?.Trim();
            return string.IsNullOrWhiteSpace(saved) || saved == SavedTreatmentPlanPlaceholder
                ? PrescriptionDefaults.Indications
                : saved;
        }
    }

    /// <summary>Cambió el plan de la consulta, el historial o la consulta en curso: las indicaciones se vuelven a leer.</summary>
    public void RaisePrescriptionIndications() => OnPropertyChanged(nameof(PrescriptionIndications));

    [RelayCommand]
    public void OpenPrescriptionPdfPreview()
    {
        if (_session.SelectedPatient == null)
        {
            _info.InfoModalMessage = "Seleccione un paciente para visualizar la receta médica.";
            _info.IsInfoModalVisible = true;
            return;
        }

        if (_session.SelectedPatient.ActivePrescription.Count == 0)
        {
            _info.InfoModalMessage = "No hay medicamentos ni consumibles registrados en la receta actual para generar el documento.";
            _info.IsInfoModalVisible = true;
            return;
        }

        IsPrescriptionPdfModalOpen = true;
    }

    [RelayCommand]
    public void ClosePrescriptionPdfPreview()
    {
        IsPrescriptionPdfModalOpen = false;
    }

    [RelayCommand]
    public void PrintOrExportPrescriptionPdf()
    {
        if (_session.SelectedPatient == null || _session.SelectedPatient.ActivePrescription.Count == 0)
        {
            _info.InfoModalMessage = "No hay medicamentos ni consumibles registrados en la receta actual para imprimir.";
            _info.IsInfoModalVisible = true;
            return;
        }

        var profile = _clinicProfileService?.CurrentProfile ?? new ClinicProfile
        {
            DoctorName = _clinic.DoctorName,
            DoctorLicense = _clinic.DoctorLicense,
            DoctorSpecialty = _clinic.DoctorSpecialty,
            ClinicName = _clinic.ClinicName,
            ClinicAddress = _clinic.ClinicAddress,
            ClinicPhone = _clinic.ClinicPhone
        };

        if (_printingService != null)
        {
            bool printed = _printingService.PrintPrescription(
                _session.SelectedPatient,
                _session.SelectedPatient.ActivePrescription,
                profile,
                PrescriptionIndications);

            if (printed)
            {
                _info.InfoModalMessage = "¡Receta médica enviada a la impresora física con éxito!";
            }
            else
            {
                _info.InfoModalMessage = "Impresión de receta cancelada o no se seleccionó impresora.";
            }
        }
        else
        {
            _info.InfoModalMessage = "La receta oficial NOM-004-SSA3 ha sido formateada con éxito para emisión física o guardado en PDF.";
        }

        _info.IsInfoModalVisible = true;
        IsPrescriptionPdfModalOpen = false;
    }

    [RelayCommand]
    public void ExportPrescriptionDocument()
    {
        if (_session.SelectedPatient == null || _session.SelectedPatient.ActivePrescription.Count == 0)
        {
            _info.InfoModalMessage = "No hay medicamentos ni consumibles registrados en la receta actual para exportar.";
            _info.IsInfoModalVisible = true;
            return;
        }

        var profile = _clinicProfileService?.CurrentProfile ?? new ClinicProfile
        {
            DoctorName = _clinic.DoctorName,
            DoctorLicense = _clinic.DoctorLicense,
            DoctorSpecialty = _clinic.DoctorSpecialty,
            ClinicName = _clinic.ClinicName,
            ClinicAddress = _clinic.ClinicAddress,
            ClinicPhone = _clinic.ClinicPhone
        };

        if (_printingService != null)
        {
            string? filePath = _printingService.ExportPrescriptionDocument(
                _session.SelectedPatient,
                _session.SelectedPatient.ActivePrescription,
                profile,
                PrescriptionIndications);

            if (!string.IsNullOrEmpty(filePath))
            {
                _info.InfoModalMessage = $"¡Receta médica oficial exportada con éxito!\nGuardada en: {filePath}";
            }
            else
            {
                _info.InfoModalMessage = "Exportación de receta cancelada.";
            }
        }
        else
        {
            _info.InfoModalMessage = "Servicio de exportación no disponible en este entorno.";
        }

        _info.IsInfoModalVisible = true;
        IsPrescriptionPdfModalOpen = false;
    }
}
