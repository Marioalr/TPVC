using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// La sección 6 de la consulta, Laboratorio (desde v1.25.0): buscar y agregar estudios (catálogo, frecuentes, paquetes
/// y los que el consultorio ya pidió), las indicaciones para el paciente y la solicitud con su vista previa,
/// impresión y documento. La orden vive en <see cref="ConsultationLabOrder"/> y se guarda con la nota.
/// </summary>
public partial class LabOrderViewModel : ObservableObject
{
    private readonly InventoryService? _inventoryService;
    private readonly ConsultationLabOrder _order;
    private readonly PatientSession _session;
    private readonly ClinicInfo _clinic;
    private readonly InfoDialogState _info;
    private readonly PatientsFeedback _feedback;
    private readonly ClinicProfileService? _clinicProfileService;
    private readonly IPrescriptionOutput? _printing;
    private readonly Func<string> _currentDiagnosis;

    public LabOrderViewModel(
        InventoryService? inventoryService,
        ConsultationLabOrder order,
        PatientSession session,
        ClinicInfo clinic,
        InfoDialogState info,
        PatientsFeedback feedback,
        ClinicProfileService? clinicProfileService,
        IPrescriptionOutput? printing,
        Func<string> currentDiagnosis)
    {
        _inventoryService = inventoryService;
        _order = order;
        _session = session;
        _clinic = clinic;
        _info = info;
        _feedback = feedback;
        _clinicProfileService = clinicProfileService;
        _printing = printing;
        _currentDiagnosis = currentDiagnosis;
    }

    /// <summary>La orden de la consulta en curso (estudios e indicaciones).</summary>
    public ConsultationLabOrder Order => _order;

    public IReadOnlyList<LabCatalogStudy> FrequentStudies => LabCatalog.Frequent;
    public IReadOnlyList<LabPanel> Panels => LabCatalog.Panels;
    public IReadOnlyList<LabIndicationPreset> IndicationPresets => LabCatalog.IndicationPresets;

    // ================================================================== buscar y agregar

    // Lo que el consultorio ya pidió fuera del catálogo; se lee una vez, al escribir por primera vez
    private List<LabCatalogStudy> _customStudies = new();
    private Task? _customStudiesLoad;

    [ObservableProperty]
    private string _studySearchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestions))]
    private IReadOnlyList<LabCatalogStudy> _suggestions = Array.Empty<LabCatalogStudy>();

    /// <summary>La sugerencia que toma Enter (↑ ↓ la cambian); -1 sin sugerencias.</summary>
    [ObservableProperty]
    private int _selectedSuggestionIndex = -1;

    public bool HasSuggestions => Suggestions.Count > 0;

    partial void OnStudySearchTextChanged(string value)
    {
        RefreshSuggestions();
        if (_customStudiesLoad == null && _inventoryService != null && !string.IsNullOrWhiteSpace(value))
        {
            _customStudiesLoad = LoadCustomStudiesAsync();
        }
    }

    private void RefreshSuggestions()
    {
        Suggestions = LabCatalog.Search(StudySearchText, _customStudies);
        SelectedSuggestionIndex = Suggestions.Count > 0 ? 0 : -1;
    }

    private async Task LoadCustomStudiesAsync()
    {
        try
        {
            var studies = await _inventoryService!.GetCustomLabStudiesAsync();
            _customStudies = studies.Select(s => new LabCatalogStudy(s.Name, string.IsNullOrWhiteSpace(s.Area) ? LabCatalog.OtherArea : s.Area) { IsCustom = true }).ToList();
            if (!string.IsNullOrWhiteSpace(StudySearchText)) RefreshSuggestions();
        }
        catch (Exception ex)
        {
            // Sin los del consultorio se sugiere solo el catálogo
            AppLogger.Warn($"No se pudieron leer los estudios que ya pidió el consultorio: {ex.Message}");
        }
    }

    /// <summary>Las pruebas esperan a que se lean los estudios del consultorio.</summary>
    internal Task CustomStudiesLoaded => _customStudiesLoad ?? Task.CompletedTask;

    [RelayCommand]
    public void NextSuggestion()
    {
        if (Suggestions.Count == 0) return;
        SelectedSuggestionIndex = (SelectedSuggestionIndex + 1) % Suggestions.Count;
    }

    [RelayCommand]
    public void PreviousSuggestion()
    {
        if (Suggestions.Count == 0) return;
        SelectedSuggestionIndex = SelectedSuggestionIndex <= 0 ? Suggestions.Count - 1 : SelectedSuggestionIndex - 1;
    }

    /// <summary>Enter: la sugerencia marcada o, si no hay ninguna, lo escrito tal cual (un estudio que no está en el catálogo).</summary>
    [RelayCommand]
    public void AcceptStudy()
    {
        if (SelectedSuggestionIndex >= 0 && SelectedSuggestionIndex < Suggestions.Count)
        {
            AddSuggestion(Suggestions[SelectedSuggestionIndex]);
            return;
        }
        AddTypedStudy();
    }

    [RelayCommand]
    public void AddSuggestion(LabCatalogStudy? study)
    {
        if (study == null) return;
        Add(new LabOrderStudy { Name = study.Name, Area = study.Area });
        StudySearchText = string.Empty;
    }

    /// <summary>Agrega lo escrito como estudio (si es del catálogo, con su nombre completo y su área).</summary>
    [RelayCommand]
    public void AddTypedStudy()
    {
        var text = (StudySearchText ?? string.Empty).Trim();
        if (text.Length == 0) return;
        var study = LabCatalog.ToStudy(text);
        if (Add(study) && LabCatalog.Find(text) == null && !_customStudies.Any(c => TextSearch.Normalize(c.Name) == TextSearch.Normalize(text)))
        {
            _customStudies.Insert(0, new LabCatalogStudy(study.Name, study.Area) { IsCustom = true });
        }
        StudySearchText = string.Empty;
    }

    [RelayCommand]
    public void AddFrequent(LabCatalogStudy? study) => AddSuggestion(study);

    /// <summary>Agrega los estudios del paquete que falten.</summary>
    [RelayCommand]
    public void AddPanel(LabPanel? panel)
    {
        if (panel == null) return;
        int added = panel.Studies.Count(name => _order.Add(LabCatalog.ToStudy(name)));
        if (added == 0) _feedback.ShowWarning($"Los estudios de \"{panel.Name}\" ya están en la orden");
    }

    [RelayCommand]
    public void RemoveStudy(LabOrderStudy? study) => _order.Remove(study);

    [RelayCommand]
    public void AppendIndication(LabIndicationPreset? preset) => _order.AppendIndication(preset?.Text);

    private bool Add(LabOrderStudy study)
    {
        if (_order.Add(study)) return true;
        _feedback.ShowWarning($"{study.Name} ya está en la orden");
        return false;
    }

    // ================================================================== vista previa, impresión y documento

    [ObservableProperty]
    private bool _isPreviewOpen;

    /// <summary>La solicitud tal como saldrá (la vista la dibuja con el mismo documento que se imprime).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaperDescription))]
    private LabOrderSheet? _previewSheet;

    public string PaperDescription => PreviewSheet?.Paper == PrescriptionPaper.Letter
        ? "Así se imprime o se guarda: no cabe en una media carta, así que sale en hoja carta vertical"
        : "Así se imprime o se guarda: media carta horizontal, con el membrete de la receta";

    private ClinicProfile CurrentProfile() => _clinicProfileService?.CurrentProfile ?? new ClinicProfile
    {
        DoctorName = _clinic.DoctorName,
        DoctorLicense = _clinic.DoctorLicense,
        DoctorSpecialty = _clinic.DoctorSpecialty,
        ClinicName = _clinic.ClinicName,
        ClinicAddress = _clinic.ClinicAddress,
        ClinicPhone = _clinic.ClinicPhone
    };

    private bool CanOutput(out PatientRecord patient)
    {
        patient = _session.SelectedPatient!;
        if (patient == null)
        {
            _info.InfoModalMessage = "Seleccione un paciente para la solicitud de laboratorio.";
            _info.IsInfoModalVisible = true;
            return false;
        }
        if (!_order.HasStudies)
        {
            _info.InfoModalMessage = "Agregue al menos un estudio a la orden de laboratorio.";
            _info.IsInfoModalVisible = true;
            return false;
        }
        return true;
    }

    [RelayCommand]
    public void OpenPreview()
    {
        if (!CanOutput(out var patient)) return;
        var order = _order.Snapshot();
        PreviewSheet = _printing?.CreateLabOrderSheet(patient, order, CurrentProfile(), _currentDiagnosis())
                       ?? LabOrderSheet.Create(patient, order, CurrentProfile(), _currentDiagnosis(), DateTime.Now);
        IsPreviewOpen = true;
    }

    [RelayCommand]
    public void ClosePreview() => IsPreviewOpen = false;

    [RelayCommand]
    public void Print()
    {
        if (!CanOutput(out var patient) || _printing == null) return;
        if (_printing.PrintLabOrder(patient, _order.Snapshot(), CurrentProfile(), _currentDiagnosis()))
        {
            _feedback.ShowSuccess("Solicitud de laboratorio enviada a la impresora");
            IsPreviewOpen = false;
        }
    }

    [RelayCommand]
    public void Export()
    {
        if (!CanOutput(out var patient) || _printing == null) return;
        if (_printing.ExportLabOrderDocument(patient, _order.Snapshot(), CurrentProfile(), _currentDiagnosis()) is { } path)
        {
            _info.InfoModalMessage = $"Solicitud de laboratorio guardada en:\n{path}";
            _info.IsInfoModalVisible = true;
            IsPreviewOpen = false;
        }
    }
}
