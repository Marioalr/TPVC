using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Hoja de receta (fase 6, paso 16): buscar el medicamento, la posología y la dosis pediátrica por peso
/// (plantillas del Excel del consultorio), la alerta de alergia antes de recetar, repetir la receta
/// anterior y "Se surte aquí" (lo que se surte en el consultorio se carga a la cuenta). Cada cambio a la
/// receta le pide al coordinador que avise a la ficha, a la hoja y a la vista previa. El peso de la
/// consulta en curso llega por currentWeight (sugerencia de la dosis pediátrica).
/// </summary>
public partial class PrescriptionSheetViewModel : ObservableObject
{
    private readonly InventoryService? _inventoryService;
    private readonly CatalogStore _catalog;
    private readonly PrescriptionTemplateStore _templates;
    private readonly PatientSession _session;
    private readonly ActiveConsultationSession _active;
    private readonly ConsultationRecordState _record;
    private readonly ConsultationAccount _account;
    private readonly InfoDialogState _info;
    private readonly PatientsFeedback _feedback;
    private readonly CatalogBrowserViewModel _catalogBrowser;
    private readonly IConsultationGate _gate;
    private readonly IPatientsWorkflow _workflow;
    private readonly IUiThread _ui;
    private readonly Func<string> _currentWeight;

    public PrescriptionSheetViewModel(
        InventoryService? inventoryService,
        CatalogStore catalog,
        PrescriptionTemplateStore templates,
        PatientSession session,
        ActiveConsultationSession active,
        ConsultationRecordState record,
        ConsultationAccount account,
        InfoDialogState info,
        PatientsFeedback feedback,
        CatalogBrowserViewModel catalogBrowser,
        IConsultationGate gate,
        IPatientsWorkflow workflow,
        IUiThread ui,
        Func<string> currentWeight)
    {
        _inventoryService = inventoryService;
        _catalog = catalog;
        _templates = templates;
        _session = session;
        _active = active;
        _record = record;
        _account = account;
        _info = info;
        _feedback = feedback;
        _catalogBrowser = catalogBrowser;
        _gate = gate;
        _workflow = workflow;
        _ui = ui;
        _currentWeight = currentWeight;
    }

    // ==========================================
    // CAPTURA INTERACTIVA DE RECETA MÉDICA (MEDICAMENTOS E INSUMOS)
    // ==========================================
    [ObservableProperty]
    private string _prescriptionSearchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<Product> _prescriptionProductSuggestions = new();

    [ObservableProperty]
    private bool _isPrescriptionDropdownOpen;

    [ObservableProperty]
    private Product? _selectedPrescriptionProduct;

    [ObservableProperty]
    private string _prescriptionMedicineName = string.Empty;

    [ObservableProperty]
    private string _prescriptionDosage = string.Empty;

    [ObservableProperty]
    private int _prescriptionQuantity = 1;

    [ObservableProperty]
    private bool _isPrescriptionModalOpen;

    // ==========================================
    // ALERTA DE ALERGIA FARMACOLÓGICA (SAFETY CHECK)
    // ==========================================
    [ObservableProperty]
    private bool _isAllergyAlertOpen;

    [ObservableProperty]
    private string _allergyAlertTitle = "Alerta de alergia a medicamento";

    [ObservableProperty]
    private string _allergyAlertMessage = string.Empty;

    [ObservableProperty]
    private string _allergyPatientAllergies = string.Empty;

    private Product? _pendingAllergyProduct;
    private string _pendingAllergyMedName = string.Empty;
    private string _pendingAllergyDosage = string.Empty;
    private int _pendingAllergyQty = 1;

    [RelayCommand]
    public void CancelAllergyPrescription()
    {
        _pendingAllergyProduct = null;
        _pendingAllergyMedName = string.Empty;
        _pendingAllergyDosage = string.Empty;
        _pendingAllergyQty = 1;
        IsAllergyAlertOpen = false;
    }

    [RelayCommand]
    public async Task ConfirmOverrideAllergyPrescriptionAsync()
    {
        IsAllergyAlertOpen = false;
        var prod = _pendingAllergyProduct;
        var name = _pendingAllergyMedName;
        var dosage = _pendingAllergyDosage;
        var qty = _pendingAllergyQty;

        _pendingAllergyProduct = null;
        _pendingAllergyMedName = string.Empty;
        _pendingAllergyDosage = string.Empty;
        _pendingAllergyQty = 1;

        if (!string.IsNullOrWhiteSpace(name))
        {
            await AddPrescriptionItemInternalAsync(prod, name, dosage, qty);
        }
    }

    partial void OnPrescriptionSearchTextChanged(string value)
    {
        ApplyPrescriptionSuggestions(value);
    }

    public void ApplyPrescriptionSuggestions(string query)
    {
        var matches = string.IsNullOrWhiteSpace(query)
            ? _catalog.AvailableProducts.Where(CatalogStore.IsProductMedication).Take(20).ToList()
            : _catalog.AvailableProducts
                .Where(CatalogStore.IsProductMedication)
                .Where(p => TextSearch.Matches(TextSearch.Normalize(query), p.Name, p.GenericName, p.Code))
                .Take(20)
                .ToList();

        PrescriptionProductSuggestions = new ObservableCollection<Product>(matches);
        SelectedPrescriptionProduct = PrescriptionProductSuggestions.FirstOrDefault();
        IsPrescriptionDropdownOpen = !string.IsNullOrWhiteSpace(query) && PrescriptionProductSuggestions.Count > 0;
    }

    // ==========================================
    // OPERACIONES DE RECETA MÉDICA (NOM-004-SSA3)
    // ==========================================

    [RelayCommand]
    public async Task SelectAndAddPrescriptionProductAsync(Product? product)
    {
        if (_session.SelectedPatient == null) return;

        string name;
        if (!string.IsNullOrWhiteSpace(PrescriptionMedicineName))
        {
            name = PrescriptionMedicineName.Trim();
        }
        else if (product != null)
        {
            name = string.IsNullOrWhiteSpace(product.Presentation)
                ? product.Name
                : $"{product.Name} ({product.Presentation})";
        }
        else if (!string.IsNullOrWhiteSpace(PrescriptionSearchText))
        {
            name = PrescriptionSearchText.Trim();
        }
        else
        {
            return;
        }

        // La lista de sugerencias puede dejar preseleccionado otro medicamento cuando el nombre se
        // escribe a mano: solo cuenta el producto que corresponde a lo recetado (alergias y surtido)
        if (product != null && !CatalogStore.ProductMatchesName(product, name))
        {
            product = _catalog.FindCatalogProduct(name);
        }

        var dosage = string.IsNullOrWhiteSpace(PrescriptionDosage)
            ? "Según indicación médica"
            : PrescriptionDosage.Trim();

        var qty = PrescriptionQuantity > 0 ? PrescriptionQuantity : 1;

        if (_session.SelectedPatient != null)
        {
            var allergyCheck = InventoryService.CheckMedicationAllergy(_session.SelectedPatient, product, name);
            if (allergyCheck.HasConflict)
            {
                _pendingAllergyProduct = product;
                _pendingAllergyMedName = name;
                _pendingAllergyDosage = dosage;
                _pendingAllergyQty = qty;
                AllergyAlertTitle = "Alerta de alergia a medicamento";
                AllergyAlertMessage = allergyCheck.WarningDetails;
                AllergyPatientAllergies = _session.SelectedPatient.Allergies ?? "Sin alergias declaradas";
                IsAllergyAlertOpen = true;
                return;
            }
        }

        await AddPrescriptionItemInternalAsync(product, name, dosage, qty);
    }

    private async Task AddPrescriptionItemInternalAsync(Product? product, string name, string dosage, int qty)
    {
        if (_session.SelectedPatient == null) return;

        var newItem = new PatientPrescriptionItem
        {
            MedicineName = name,
            Dosage = dosage,
            RecommendedQuantity = qty
        };

        // "Se surte aquí" marcado por defecto cuando hay existencias: se carga solo a la cuenta.
        // El producto solo cuenta si corresponde al nombre recetado (la lista de sugerencias puede
        // traer preseleccionado otro medicamento cuando el nombre se escribió a mano).
        if (_active.HasActiveConsultation)
        {
            var dispensed = product != null && CatalogStore.ProductMatchesName(product, name) ? product : null;
            newItem.DispenseInClinic = _account.ChargePrescription(newItem, dispensed);
        }

        var currentList = new List<PatientPrescriptionItem>(_session.SelectedPatient.ActivePrescription) { newItem };
        _session.SelectedPatient.ActivePrescription = currentList;
        _session.SelectedPatient.SyncPrescriptionJson();

        if (_inventoryService != null)
        {
            try
            {
                await _inventoryService.SavePatientAsync(_session.SelectedPatient);
            }
            catch { }
        }

        PrescriptionSearchText = string.Empty;
        PrescriptionMedicineName = string.Empty;
        PrescriptionDosage = string.Empty;
        PrescriptionQuantity = 1;
        SelectedPrescriptionProduct = null;
        ApplyPrescriptionSuggestions(string.Empty);

        _session.NotifySelectedPatientMutated();
        _workflow.NotifyPrescriptionStateChanged();
    }

    [RelayCommand]
    public async Task RemovePrescriptionItemFromPatientAsync(PatientPrescriptionItem? item)
    {
        if (_session.SelectedPatient == null || item == null) return;

        if (item.DispenseInClinic && _active.HasActiveConsultation)
        {
            _account.UnchargePrescription(item);
        }

        var list = new List<PatientPrescriptionItem>(_session.SelectedPatient.ActivePrescription);
        list.Remove(item);
        _session.SelectedPatient.ActivePrescription = list;
        _session.SelectedPatient.SyncPrescriptionJson();

        if (_inventoryService != null)
        {
            try
            {
                await _inventoryService.SavePatientAsync(_session.SelectedPatient);
            }
            catch
            {
            }
        }

        _session.NotifySelectedPatientMutated();
        _workflow.NotifyPrescriptionStateChanged();
    }

    [RelayCommand]
    public async Task OpenPrescriptionModalAsync()
    {
        OpenPrescriptionModal();
        if (_catalog.AvailableProducts.Count == 0 && _inventoryService != null)
        {
            await _workflow.LoadServicesAsync();
            ApplyPrescriptionSuggestions(string.Empty);
            IsPrescriptionDropdownOpen = false;
        }
    }

    public void OpenPrescriptionModal()
    {
        if (_gate.BlockIfConsultationReadOnly()) return;
        if (_session.SelectedPatient == null)
        {
            _info.InfoModalMessage = "Seleccione o agregue un paciente primero para gestionar su receta médica.";
            _info.IsInfoModalVisible = true;
            return;
        }

        PrescriptionSearchText = string.Empty;
        PrescriptionMedicineName = string.Empty;
        PrescriptionDosage = string.Empty;
        PrescriptionQuantity = 1;
        ApplyPrescriptionSuggestions(string.Empty);
        IsPrescriptionDropdownOpen = false;
        IsPrescriptionModalOpen = true;

        // Plantillas recién importadas del Excel (o llegadas por sincronización)
        _ = _templates.LoadPrescriptionTemplatesAsync();
        SelectedPediatricRule = null;
        PediatricWeightText = string.Empty;
    }

    [RelayCommand]
    public async Task OpenPrescriptionCatalogAsync()
    {
        if (_gate.BlockIfConsultationReadOnly()) return;
        if (_catalog.AvailableProducts.Count == 0 && _inventoryService != null)
        {
            await _workflow.LoadServicesAsync();
        }
        _catalogBrowser.CatalogBrowserMode = "Medications";
        _catalogBrowser.CatalogBrowserTitle = "Catálogo de Medicamentos";
        _catalogBrowser.CatalogBrowserSubtitle = "Explore todos los fármacos registrados y agregue el medicamento a la receta médica del paciente.";
        _catalogBrowser.CatalogBrowserSearchText = string.Empty;
        _catalogBrowser.ApplyCatalogBrowserFilter();
        _catalogBrowser.IsCatalogBrowserModalOpen = true;
    }

    [RelayCommand]
    public void ClosePrescriptionModal()
    {
        IsPrescriptionModalOpen = false;
    }

    [RelayCommand]
    public async Task SavePrescriptionModalAsync()
    {
        if (_session.SelectedPatient != null)
        {
            _session.SelectedPatient.SyncPrescriptionJson();
            if (_inventoryService != null)
            {
                try
                {
                    await _inventoryService.SavePatientAsync(_session.SelectedPatient);
                }
                catch
                {
                }
            }
            _session.NotifySelectedPatientMutated();
        }

        IsPrescriptionModalOpen = false;
        _feedback.ShowSuccess($"¡Receta médica guardada correctamente en el expediente de {_session.SelectedPatient?.FullName}!");
    }

    // ==========================================
    // POSOLOGÍA Y DOSIS PEDIÁTRICA (PLANTILLAS DEL EXCEL DEL CONSULTORIO)
    // ==========================================
    [ObservableProperty]
    private PrescriptionTemplate? _selectedPosologyTemplate;

    [ObservableProperty]
    private PediatricDoseRule? _selectedPediatricRule;

    [ObservableProperty]
    private string _pediatricWeightText = string.Empty;

    [ObservableProperty]
    private string _pediatricDoseResult = string.Empty;

    /// <summary>La dosis pediátrica por peso solo se ofrece con pacientes menores de edad.</summary>
    public bool ShowsPediatricDose => _templates.HasPediatricDoseRules && _session.SelectedPatient?.IsPediatric == true;

    /// <summary>Cambió el paciente o llegaron las reglas de dosis pediátrica (coordinador).</summary>
    public void RaiseShowsPediatricDose() => OnPropertyChanged(nameof(ShowsPediatricDose));

    /// <summary>Elegir una posología la copia al campo de indicaciones del medicamento que se captura.</summary>
    partial void OnSelectedPosologyTemplateChanged(PrescriptionTemplate? value)
    {
        if (value == null) return;
        ApplyPosologyTemplate(value);
        _ui.ResetSelectionLater(() => SelectedPosologyTemplate = null);
    }

    public void ApplyPosologyTemplate(PrescriptionTemplate? template)
    {
        if (template == null) return;
        PrescriptionDosage = template.Text;
    }

    partial void OnSelectedPediatricRuleChanged(PediatricDoseRule? value)
    {
        if (value != null && string.IsNullOrWhiteSpace(PediatricWeightText))
        {
            PediatricWeightText = SuggestPatientWeight();
        }
        RecalculatePediatricDose();
    }

    partial void OnPediatricWeightTextChanged(string value) => RecalculatePediatricDose();

    private string SuggestPatientWeight()
    {
        if (_active.HasActiveConsultation && TryParseNumber(_currentWeight(), out var current) && current > 0 && current < 70)
        {
            return current.ToString("0.#", CultureInfo.InvariantCulture);
        }

        var last = _session.SelectedPatient?.Consultations
            .Where(c => c.Weight.HasValue && c.Weight > 0)
            .OrderByDescending(c => c.Date)
            .FirstOrDefault();
        return last?.Weight?.ToString("0.#", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private void RecalculatePediatricDose()
    {
        var rule = SelectedPediatricRule;
        if (rule == null)
        {
            PediatricDoseResult = string.Empty;
            return;
        }

        if (!rule.CanCalculate)
        {
            PediatricDoseResult = "Esta regla no tiene fórmula de ml por kg en el Excel.";
            return;
        }

        if (!TryParseNumber(PediatricWeightText, out var weight) || weight <= 0)
        {
            PediatricDoseResult = "Capture el peso del paciente (kg).";
            return;
        }

        var ml = rule.CalculateMl(weight)!.Value;
        var drops = rule.CalculateDrops(weight);
        PediatricDoseResult = drops.HasValue
            ? $"{ml.ToString("0.#", CultureInfo.InvariantCulture)} ml ({drops.Value:0} gotas) por dosis"
            : $"{ml.ToString("0.#", CultureInfo.InvariantCulture)} ml por dosis";
    }

    /// <summary>Copia la dosis calculada a las indicaciones del medicamento ("Dar 7 ml vía oral ...").</summary>
    [RelayCommand]
    public void ApplyPediatricDose()
    {
        var rule = SelectedPediatricRule;
        if (rule == null || !TryParseNumber(PediatricWeightText, out var weight)) return;

        var ml = rule.CalculateMl(weight);
        if (ml == null) return;

        var drops = rule.CalculateDrops(weight);
        var amount = drops.HasValue
            ? $"{ml.Value.ToString("0.#", CultureInfo.InvariantCulture)} ml ({drops.Value:0} gotas)"
            : $"{ml.Value.ToString("0.#", CultureInfo.InvariantCulture)} ml";
        var text = $"Dar {amount} vía oral cada ___ hrs por ___ días";

        PrescriptionDosage = text;
        if (string.IsNullOrWhiteSpace(PrescriptionSearchText) && string.IsNullOrWhiteSpace(PrescriptionMedicineName))
        {
            PrescriptionMedicineName = rule.Medication;
        }
    }

    private static bool TryParseNumber(string? text, out double value) =>
        double.TryParse((text ?? string.Empty).Replace(',', '.').Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);

    // ==========================================
    // RECETA ANTERIOR Y "SE SURTE AQUÍ"
    // ==========================================
    public bool CanRepeatPreviousPrescription => _active.HasActiveConsultation && _record.PendingPreviousPrescription.Count > 0;

    public string PreviousPrescriptionSummary => string.Join(" · ", _record.PendingPreviousPrescription.Select(p => p.MedicineName));

    /// <summary>Repetir la receta anterior (NotifyPrescriptionStateChanged del coordinador, en este orden).</summary>
    public void RaiseRepeatState()
    {
        OnPropertyChanged(nameof(CanRepeatPreviousPrescription));
        OnPropertyChanged(nameof(PreviousPrescriptionSummary));
    }

    /// <summary>
    /// Copia a esta consulta los medicamentos de la receta anterior que falten. Se revisan otra vez
    /// las alergias (pudieron registrarse después) y las existencias para "Se surte aquí".
    /// </summary>
    [RelayCommand]
    public async Task RepeatPreviousPrescriptionAsync()
    {
        if (_gate.BlockIfConsultationReadOnly()) return;
        if (!_active.HasActiveConsultation || _session.SelectedPatient == null) return;

        var skipped = new List<string>();
        foreach (var item in _record.PendingPreviousPrescription)
        {
            var product = _catalog.FindCatalogProduct(item.MedicineName);
            if (InventoryService.CheckMedicationAllergy(_session.SelectedPatient, product, item.MedicineName).HasConflict)
            {
                skipped.Add(item.MedicineName);
                continue;
            }
            await AddPrescriptionItemInternalAsync(product, item.MedicineName, item.Dosage, Math.Max(1, item.RecommendedQuantity));
        }

        _workflow.NotifyPrescriptionStateChanged();
        if (skipped.Count > 0)
        {
            _info.InfoModalMessage = $"No se repitió {string.Join(", ", skipped)}: coincide con las alergias registradas del paciente. Si de todos modos debe recetarse, agréguelo a mano para confirmar la alerta.";
            _info.IsInfoModalVisible = true;
        }
    }

    /// <summary>Casilla "Se surte aquí" de cada medicamento de la receta.</summary>
    [RelayCommand]
    public async Task ToggleDispenseInClinicAsync(PatientPrescriptionItem? item)
    {
        if (item != null && _gate.BlockIfConsultationReadOnly())
        {
            // La casilla ya cambió el valor antes de llegar aquí
            item.DispenseInClinic = !item.DispenseInClinic;
            return;
        }
        if (item == null || _session.SelectedPatient == null) return;

        if (item.DispenseInClinic)
        {
            if (!_active.HasActiveConsultation || !_account.ChargePrescription(item))
            {
                item.DispenseInClinic = false;
                _info.InfoModalMessage = _active.HasActiveConsultation
                    ? $"'{item.MedicineName}' no tiene existencias suficientes en el consultorio; queda solo en la receta."
                    : "Inicie la consulta para cargar medicamentos a la cuenta.";
                _info.IsInfoModalVisible = true;
            }
        }
        else if (_active.HasActiveConsultation)
        {
            _account.UnchargePrescription(item);
        }

        _session.SelectedPatient.SyncPrescriptionJson();
        _session.NotifySelectedPatientMutated();
        if (_inventoryService != null)
        {
            try { await _inventoryService.SavePatientAsync(_session.SelectedPatient); }
            catch (Exception ex) { AppLogger.Error("No se pudo guardar la receta", ex); }
        }
    }
}
