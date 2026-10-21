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
using TPVC.Services.Rules;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Hoja de receta (fase 6, paso 16): buscar el medicamento, la posología y la dosis pediátrica por peso
/// (plantillas del Excel del consultorio), la alerta de alergia antes de recetar, repetir la receta
/// anterior y "Se surte aquí" (lo que se surte en el consultorio se carga a la cuenta). Cada cambio a la
/// receta le pide al coordinador que avise a la ficha, a la hoja y a la vista previa. El peso de la
/// consulta en curso llega por currentWeight (sugerencia de la dosis pediátrica).
/// <para>
/// Desde los pedidos del consultorio del 2026-10-14: elegir el medicamento (Enter o clic en las sugerencias, o el
/// catálogo) solo lo escoge y el cursor pasa a "Indicación"; se agrega con Enter en "Indicación" o "Cantidad" o con
/// "Agregar" (<see cref="AddPrescriptionAsync"/>). Las sugerencias ponen primero lo que hay en existencia
/// (<see cref="MedicineSearchRules"/>), el campo "Indicación" trae las indicaciones frecuentes
/// (<see cref="CaptureIndications"/>) y cada renglón se puede corregir con "Cambiar indicación"
/// (<see cref="SheetIndication"/> en la hoja y <see cref="PlanIndication"/> en la sección 5 de la consulta).
/// </para>
/// <para>
/// Pedido del médico del 2026-10-21: el renglón lleva la marca afuera y el principio activo entre paréntesis
/// (<see cref="PrescriptionNaming.MedicineLine"/>), y la casilla «Mostrar (Paracetamol)» decide si el paréntesis va: junto a
/// "Indicación" para el medicamento elegido (<see cref="ShowChosenActiveIngredient"/>, empieza con la última elección de este
/// equipo) y en cada renglón agregado (<see cref="ToggleActiveIngredientCommand"/>).
/// </para>
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
    private readonly Func<string> _currentWeight;
    private readonly LocalPreferencesStore? _preferences;

    // La última elección de «Mostrar (…)» en este equipo (LocalPreferences; sin preferencias, solo mientras dura la pantalla)
    private bool _showActiveIngredientByDefault;

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
        Func<string> currentWeight,
        LocalPreferencesStore? preferences = null)
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
        _currentWeight = currentWeight;
        _preferences = preferences;
        _showActiveIngredientByDefault = preferences?.Current.HidePrescriptionActiveIngredient != true;

        CaptureIndications = new FrequentIndicationsPicker(templates, () => PrescriptionDosage, text => PrescriptionDosage = text);
        SheetIndication = new PrescriptionIndicationEditor(templates, CanChangeIndication, SaveIndicationAsync);
        PlanIndication = new PrescriptionIndicationEditor(templates, CanChangeIndication, SaveIndicationAsync);
        ResetChosenActiveIngredient();
    }

    /// <summary>Indicaciones frecuentes del campo "Indicación" de la captura.</summary>
    public FrequentIndicationsPicker CaptureIndications { get; }

    /// <summary>"Cambiar indicación" en la lista de la hoja de receta.</summary>
    public PrescriptionIndicationEditor SheetIndication { get; }

    /// <summary>"Cambiar indicación" en la lista de la sección 5 (Plan y receta) de la consulta.</summary>
    public PrescriptionIndicationEditor PlanIndication { get; }

    /// <summary>La indicación cuando el médico no escribió ninguna.</summary>
    public const string DefaultDosage = "Según indicación médica";

    /// <summary>Se agregó un medicamento: el cursor vuelve al buscador para el siguiente.</summary>
    public event Action? FocusSearchRequested;

    // ==========================================
    // CAPTURA INTERACTIVA DE RECETA MÉDICA (MEDICAMENTOS E INSUMOS)
    // ==========================================
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddPrescriptionCommand))]
    private string _prescriptionSearchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<Product> _prescriptionProductSuggestions = new();

    [ObservableProperty]
    private bool _isPrescriptionDropdownOpen;

    [ObservableProperty]
    private Product? _selectedPrescriptionProduct;

    /// <summary>
    /// Nombre del medicamento que no se escribe en el buscador: lo pone la dosis pediátrica ("Usar") cuando el buscador
    /// está vacío. Elegir un medicamento lo borra.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddPrescriptionCommand))]
    private string _prescriptionMedicineName = string.Empty;

    [ObservableProperty]
    private string _prescriptionDosage = string.Empty;

    /// <summary>
    /// Ya se eligió el medicamento (pedido del 2026-10-14): el buscador lo muestra, las sugerencias se cierran y falta la
    /// indicación. Del catálogo (<see cref="ChosenPrescriptionProduct"/>) o escrito a mano (sin producto). Escribir otra
    /// vez en el buscador, o la X del buscador, deshacen la elección.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChosenMedicineCaption))]
    [NotifyPropertyChangedFor(nameof(HasChosenActiveIngredient))]
    [NotifyPropertyChangedFor(nameof(ChosenActiveIngredientToggleText))]
    private bool _isMedicineChosen;

    /// <summary>El producto del catálogo que se eligió; null si se eligió lo escrito a mano.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChosenMedicineCaption))]
    [NotifyPropertyChangedFor(nameof(HasChosenActiveIngredient))]
    [NotifyPropertyChangedFor(nameof(ChosenActiveIngredientToggleText))]
    private Product? _chosenPrescriptionProduct;

    /// <summary>Debajo del buscador, qué se eligió: del inventario (con sus existencias) o escrito a mano.</summary>
    public string ChosenMedicineCaption => !IsMedicineChosen
        ? string.Empty
        : ChosenPrescriptionProduct is { } product
            ? $"Elegido del inventario · {product.StockBadgeText}"
            : "Escrito a mano (no se eligió del inventario)";

    // Mientras se escribe en el buscador el medicamento elegido, el cambio de texto no es una búsqueda nueva
    private bool _choosingMedicine;

    /// <summary>
    /// «Mostrar (Paracetamol)» junto a "Indicación" (pedido del 2026-10-21): el medicamento elegido se agrega con el principio
    /// activo entre paréntesis o sin él, y el buscador lo muestra así. Empieza con la última elección de este equipo
    /// (<see cref="LocalPreferences.HidePrescriptionActiveIngredient"/>; la primera vez, marcada).
    /// </summary>
    [ObservableProperty]
    private bool _showChosenActiveIngredient;

    // Al volver a la última elección (elegir otro medicamento, agregar, abrir la hoja) la casilla no es una elección nueva
    private bool _resettingActiveIngredient;

    /// <summary>Las partes del renglón del medicamento elegido del catálogo; null si se eligió lo escrito a mano.</summary>
    private PrescriptionNaming.MedicineLineParts? ChosenMedicineParts =>
        IsMedicineChosen && !_chosenAsTyped && ChosenPrescriptionProduct is { } product ? PrescriptionNaming.Parts(product) : null;

    /// <summary>La casilla «Mostrar (…)» se ve: el medicamento elegido es de marca (tiene algo entre paréntesis).</summary>
    public bool HasChosenActiveIngredient => !string.IsNullOrEmpty(ChosenMedicineParts?.ActiveIngredient);

    /// <summary>«Mostrar (Paracetamol)», con el texto que va entre paréntesis.</summary>
    public string ChosenActiveIngredientToggleText => ChosenMedicineParts?.ActiveIngredient is { Length: > 0 } ingredient ? $"Mostrar ({ingredient})" : string.Empty;

    partial void OnShowChosenActiveIngredientChanged(bool value)
    {
        if (!_resettingActiveIngredient) RememberActiveIngredientChoice(value);
        // El buscador muestra el renglón como se va a agregar
        if (ChosenMedicineParts is { ActiveIngredient.Length: > 0 } parts)
        {
            _choosingMedicine = true;
            try { PrescriptionSearchText = parts.Compose(value); }
            finally { _choosingMedicine = false; }
        }
    }

    /// <summary>La casilla del medicamento elegido vuelve a la última elección de este equipo.</summary>
    private void ResetChosenActiveIngredient()
    {
        _resettingActiveIngredient = true;
        try { ShowChosenActiveIngredient = _showActiveIngredientByDefault; }
        finally { _resettingActiveIngredient = false; }
    }

    /// <summary>La elección queda para el siguiente medicamento en este equipo (no se sincroniza).</summary>
    private void RememberActiveIngredientChoice(bool show)
    {
        _showActiveIngredientByDefault = show;
        if (_preferences == null || _preferences.Current.HidePrescriptionActiveIngredient == !show) return;
        _preferences.Current.HidePrescriptionActiveIngredient = !show;
        _preferences.Save();
    }

    // Se eligió lo escrito tal cual (Enter con la lista cerrada): el nombre es el texto; el producto, si lo hay, el del
    // catálogo con ese nombre (alergias por su genérico, "Se surte aquí" y el aviso "Elegido del inventario")
    private bool _chosenAsTyped;

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
    private string? _pendingAllergyPresentation;
    private string? _pendingAllergyActiveIngredient;
    private bool? _pendingAllergyShowActiveIngredient;

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
        var presentation = _pendingAllergyPresentation;
        var activeIngredient = _pendingAllergyActiveIngredient;
        var showActiveIngredient = _pendingAllergyShowActiveIngredient;
        _pendingAllergyPresentation = null;
        _pendingAllergyActiveIngredient = null;
        _pendingAllergyShowActiveIngredient = null;

        _pendingAllergyProduct = null;
        _pendingAllergyMedName = string.Empty;
        _pendingAllergyDosage = string.Empty;
        _pendingAllergyQty = 1;

        if (!string.IsNullOrWhiteSpace(name))
        {
            await AddPrescriptionItemInternalAsync(prod, name, dosage, qty, presentation, activeIngredient, showActiveIngredient);
        }
    }

    partial void OnPrescriptionSearchTextChanged(string value)
    {
        if (_choosingMedicine) return;
        // Escribir otra vez en el buscador es buscar otro medicamento: la elección anterior ya no cuenta
        if (IsMedicineChosen)
        {
            IsMedicineChosen = false;
            ChosenPrescriptionProduct = null;
        }
        ApplyPrescriptionSuggestions(value);
    }

    partial void OnPrescriptionDosageChanged(string value) => CaptureIndications.Filter(value);

    /// <summary>
    /// Las sugerencias del buscador: los medicamentos del catálogo que coinciden, con existencias primero y por qué tan bien
    /// coinciden (<see cref="MedicineSearchRules.Rank"/>), hasta 20; sin texto, los primeros 20 en ese orden. La primera queda
    /// marcada. La lista se abre al escribir algo que coincide (con el medicamento ya elegido, no).
    /// </summary>
    public void ApplyPrescriptionSuggestions(string query)
    {
        var matches = CatalogSearchRules.PrescriptionSuggestions(_catalog.AvailableProducts, query);

        PrescriptionProductSuggestions = new ObservableCollection<Product>(matches);
        SelectedPrescriptionProduct = PrescriptionProductSuggestions.FirstOrDefault();
        IsPrescriptionDropdownOpen = !IsMedicineChosen && !string.IsNullOrWhiteSpace(query) && PrescriptionProductSuggestions.Count > 0;
    }

    /// <summary>
    /// ↑ ↓ en el buscador: con la lista cerrada, ↓ la abre (también sin texto: los que hay en existencia primero); abierta,
    /// cambian la sugerencia marcada (de la última se vuelve a la primera). Con el medicamento ya elegido no hacen nada.
    /// </summary>
    public void MovePrescriptionSuggestion(int delta)
    {
        var list = PrescriptionProductSuggestions;
        if (list.Count == 0 || IsMedicineChosen || delta == 0) return;
        if (!IsPrescriptionDropdownOpen)
        {
            if (delta < 0) return;
            SelectedPrescriptionProduct ??= list[0];
            IsPrescriptionDropdownOpen = true;
            return;
        }

        var index = SelectedPrescriptionProduct == null ? -1 : list.IndexOf(SelectedPrescriptionProduct);
        index = delta > 0
            ? (index < 0 || index >= list.Count - 1 ? 0 : index + 1)
            : (index <= 0 || index >= list.Count ? list.Count - 1 : index - 1);
        SelectedPrescriptionProduct = list[index];
    }

    /// <summary>Esc con las sugerencias abiertas: solo se cierra la lista (lo escrito se queda).</summary>
    public void ClosePrescriptionSuggestions() => IsPrescriptionDropdownOpen = false;

    /// <summary>
    /// Elegir un medicamento del catálogo (Enter o clic en las sugerencias, o el catálogo): el buscador lo muestra como se va a
    /// escribir en la receta (con el principio activo entre paréntesis según «Mostrar (…)», que vuelve a la última elección),
    /// las sugerencias se cierran y falta escribir la indicación. No lo agrega.
    /// </summary>
    public void ChoosePrescriptionProduct(Product? product)
    {
        if (product == null) return;
        ResetChosenActiveIngredient();
        _choosingMedicine = true;
        try
        {
            PrescriptionSearchText = PrescriptionNaming.MedicineLine(product, ShowChosenActiveIngredient);
        }
        finally
        {
            _choosingMedicine = false;
        }
        PrescriptionMedicineName = string.Empty;
        _chosenAsTyped = false;
        ChosenPrescriptionProduct = product;
        IsMedicineChosen = true;
        IsPrescriptionDropdownOpen = false;
    }

    /// <summary>
    /// Enter en el buscador. Con la lista abierta elige la sugerencia marcada; con la lista cerrada (nada coincide, o se
    /// cerró con Esc) elige lo escrito tal cual: si es el nombre de un producto del catálogo (una marca, por ejemplo),
    /// ese producto cuenta para las alergias y "Se surte aquí"; si no, es un medicamento escrito a mano. Sin nada escrito
    /// no hace nada. true si ya hay medicamento elegido (el cursor pasa a "Indicación").
    /// </summary>
    public bool ConfirmPrescriptionSearch()
    {
        if (IsMedicineChosen) return true;
        if (IsPrescriptionDropdownOpen && SelectedPrescriptionProduct != null)
        {
            ChoosePrescriptionProduct(SelectedPrescriptionProduct);
            return true;
        }
        if (string.IsNullOrWhiteSpace(PrescriptionSearchText)) return false;

        PrescriptionMedicineName = string.Empty;
        _chosenAsTyped = true;
        ChosenPrescriptionProduct = _catalog.FindCatalogProduct(PrescriptionSearchText);
        IsMedicineChosen = true;
        IsPrescriptionDropdownOpen = false;
        return true;
    }

    /// <summary>La X del buscador: deshace la elección y deja el buscador vacío.</summary>
    [RelayCommand]
    public void ClearChosenMedicine()
    {
        PrescriptionSearchText = string.Empty;
        IsMedicineChosen = false;
        ChosenPrescriptionProduct = null;
        IsPrescriptionDropdownOpen = false;
    }

    private bool CanAddPrescription() =>
        !string.IsNullOrWhiteSpace(PrescriptionSearchText) || !string.IsNullOrWhiteSpace(PrescriptionMedicineName);

    /// <summary>
    /// "Agregar" (también Enter en "Indicación" o "Cantidad"): agrega el medicamento elegido con la indicación y la cantidad
    /// escritas. Si se escribió el medicamento y se pasó directo a la indicación sin elegirlo, primero se elige como con Enter
    /// en el buscador. Sin medicamento no hace nada.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddPrescription))]
    public async Task AddPrescriptionAsync()
    {
        // La dosis pediátrica ("Usar") deja su medicamento sin escribirlo en el buscador: con el buscador vacío y nada
        // elegido, ese es el medicamento (antes que una sugerencia marcada en una lista que quedó abierta)
        if (!IsMedicineChosen && string.IsNullOrWhiteSpace(PrescriptionSearchText) && !string.IsNullOrWhiteSpace(PrescriptionMedicineName))
        {
            IsPrescriptionDropdownOpen = false;
            await SelectAndAddPrescriptionProductAsync(null);
            return;
        }
        if (!IsMedicineChosen && !ConfirmPrescriptionSearch()) return;

        // Lo escrito tal cual se agrega con su texto (el producto se busca por ese nombre al agregar)
        if (_chosenAsTyped)
        {
            await SelectAndAddPrescriptionProductAsync(null);
            return;
        }

        // El catálogo pudo volver a leerse desde que se eligió: las existencias de ahora
        var chosen = ChosenPrescriptionProduct;
        if (chosen is { Id: > 0 }) chosen = _catalog.AvailableProducts.FirstOrDefault(p => p.Id == chosen.Id) ?? chosen;
        await SelectAndAddPrescriptionProductAsync(chosen);
    }

    // ==========================================
    // OPERACIONES DE RECETA MÉDICA (NOM-004-SSA3)
    // ==========================================

    /// <summary>
    /// Agrega a la receta el medicamento (el producto del catálogo o lo escrito) con la indicación y la cantidad capturadas:
    /// revisa alergias (la alerta pide confirmar), marca "Se surte aquí" si hay existencias y lo carga a la cuenta. Desde el
    /// 2026-10-14 la hoja lo llama solo al confirmar (<see cref="AddPrescriptionAsync"/>); elegir el medicamento ya no agrega.
    /// </summary>
    [RelayCommand]
    public async Task SelectAndAddPrescriptionProductAsync(Product? product)
    {
        if (_session.SelectedPatient == null || _gate.BlockIfConsultationReadOnly()) return;

        string name;
        string? activeIngredient = null;
        bool? showActiveIngredient = null;
        if (!string.IsNullOrWhiteSpace(PrescriptionMedicineName))
        {
            name = PrescriptionMedicineName.Trim();
        }
        else if (product != null)
        {
            // La marca con su concentración y forma, y el principio activo entre paréntesis si «Mostrar (…)» está marcada; la
            // presentación aparte. El renglón guarda el principio activo para poder quitarlo o ponerlo después
            var parts = PrescriptionNaming.Parts(product);
            name = parts.Compose(ShowChosenActiveIngredient);
            if (!string.IsNullOrEmpty(parts.ActiveIngredient))
            {
                activeIngredient = parts.ActiveIngredient;
                showActiveIngredient = ShowChosenActiveIngredient;
            }
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
        // escribe a mano: solo cuenta el producto que corresponde a lo recetado (alergias y surtido). Sin
        // producto, el del catálogo con ese nombre: una marca escrita a mano ("Tempra 500 mg") se revisa contra
        // las alergias por su genérico, como el producto que se surtirá y se cobrará
        if (product == null || !CatalogStore.ProductMatchesName(product, name))
        {
            product = _catalog.FindCatalogProduct(name);
        }

        var dosage = string.IsNullOrWhiteSpace(PrescriptionDosage)
            ? DefaultDosage
            : PrescriptionDosage.Trim();

        var qty = PrescriptionQuantity > 0 ? PrescriptionQuantity : 1;
        var presentation = product != null && PrescriptionNaming.Presentation(product) is { Length: > 0 } p ? p : null;

        if (_session.SelectedPatient != null)
        {
            var allergyCheck = AllergyRules.CheckMedication(_session.SelectedPatient, product, name, activeIngredient);
            if (allergyCheck.HasConflict)
            {
                _pendingAllergyProduct = product;
                _pendingAllergyMedName = name;
                _pendingAllergyDosage = dosage;
                _pendingAllergyQty = qty;
                _pendingAllergyPresentation = presentation;
                _pendingAllergyActiveIngredient = activeIngredient;
                _pendingAllergyShowActiveIngredient = showActiveIngredient;
                AllergyAlertTitle = "Alerta de alergia a medicamento";
                AllergyAlertMessage = allergyCheck.WarningDetails;
                AllergyPatientAllergies = _session.SelectedPatient.Allergies ?? "Sin alergias declaradas";
                IsAllergyAlertOpen = true;
                return;
            }
        }

        await AddPrescriptionItemInternalAsync(product, name, dosage, qty, presentation, activeIngredient, showActiveIngredient);
    }

    private async Task AddPrescriptionItemInternalAsync(Product? product, string name, string dosage, int qty, string? presentation = null,
        string? activeIngredient = null, bool? showActiveIngredient = null)
    {
        if (_session.SelectedPatient == null) return;

        var newItem = new PatientPrescriptionItem
        {
            MedicineName = name,
            Dosage = dosage,
            RecommendedQuantity = qty,
            Presentation = presentation,
            ActiveIngredient = string.IsNullOrWhiteSpace(activeIngredient) ? null : activeIngredient,
            ShowActiveIngredient = string.IsNullOrWhiteSpace(activeIngredient) ? null : showActiveIngredient ?? true
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

        CaptureIndications.Close();
        PrescriptionSearchText = string.Empty;
        IsMedicineChosen = false;
        _chosenAsTyped = false;
        ChosenPrescriptionProduct = null;
        PrescriptionMedicineName = string.Empty;
        PrescriptionDosage = string.Empty;
        PrescriptionQuantity = 1;
        SelectedPrescriptionProduct = null;
        ResetChosenActiveIngredient();
        ApplyPrescriptionSuggestions(string.Empty);

        _session.NotifySelectedPatientMutated();
        _workflow.NotifyPrescriptionStateChanged();
        FocusSearchRequested?.Invoke();
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
        // Quitar el renglón que se está corrigiendo cierra su corrección (si no, un campo invisible se quedaría con la Esc)
        if (ReferenceEquals(SheetIndication.Item, item)) SheetIndication.Cancel();
        if (ReferenceEquals(PlanIndication.Item, item)) PlanIndication.Cancel();

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

    /// <summary>
    /// "+ Medicamento": una "Cambiar indicación" abierta en la sección 5 se guarda antes de abrir la hoja, que la taparía
    /// (si no se puede, la hoja no se abre y la corrección sigue ahí).
    /// </summary>
    [RelayCommand]
    public async Task OpenPrescriptionModalAsync()
    {
        if (!await CommitPendingPrescriptionEditsAsync()) return;
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
        IsMedicineChosen = false;
        ChosenPrescriptionProduct = null;
        PrescriptionMedicineName = string.Empty;
        PrescriptionDosage = string.Empty;
        PrescriptionQuantity = 1;
        ResetChosenActiveIngredient();
        ApplyPrescriptionSuggestions(string.Empty);
        IsPrescriptionDropdownOpen = false;
        CaptureIndications.Close();
        // "+ Medicamento" ya guardó la corrección abierta en la sección 5; aquí solo queda cerrar la de un renglón que ya no
        // está (o la de quien abre la hoja directamente)
        PlanIndication.Cancel();
        SheetIndication.Cancel();
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
        _catalogBrowser.CatalogBrowserSubtitle = "Los que hay en existencia van primero. Elija el medicamento y luego escriba su indicación en la receta.";
        _catalogBrowser.CatalogBrowserSearchText = string.Empty;
        _catalogBrowser.ApplyCatalogBrowserFilter();
        _catalogBrowser.IsCatalogBrowserModalOpen = true;
    }

    [RelayCommand]
    public void ClosePrescriptionModal()
    {
        IsPrescriptionModalOpen = false;
        CaptureIndications.Close();
        SheetIndication.Cancel();
    }

    /// <summary>
    /// "Guardar" del pie de la hoja. Lo pendiente no se pierde (<see cref="CommitPendingPrescriptionEditsAsync"/>): una
    /// "Cambiar indicación" abierta se guarda y el medicamento elegido (o el de la dosis pediátrica) se agrega con su
    /// indicación. Si algo de eso no se pudo (la consulta está en otro equipo, o la alerta de alergia pide confirmar), la
    /// hoja sigue abierta y no dice "guardada". Lo escrito sin elegir se queda como antes (no se agrega).
    /// </summary>
    [RelayCommand]
    public async Task SavePrescriptionModalAsync()
    {
        if (!await CommitPendingPrescriptionEditsAsync(addPendingMedicine: true)) return;

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
    // DOSIS PEDIÁTRICA (PLANTILLAS DEL EXCEL DEL CONSULTORIO)
    // La posología frecuente se elige dentro del campo "Indicación" (CaptureIndications)
    // ==========================================
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

    partial void OnSelectedPediatricRuleChanged(PediatricDoseRule? value)
    {
        if (value != null && string.IsNullOrWhiteSpace(PediatricWeightText))
        {
            PediatricWeightText = SuggestPatientWeight();
        }
        RecalculatePediatricDose();
    }

    partial void OnPediatricWeightTextChanged(string value) => RecalculatePediatricDose();

    // Paso 8.26: las reglas de la calculadora viven en PediatricDoseRules (vectores pediatric-dose.json)
    private string SuggestPatientWeight() =>
        PediatricDoseRules.SuggestWeight(_active.HasActiveConsultation, _active.HasActiveConsultation ? _currentWeight() : null, _session.SelectedPatient?.Consultations);

    private void RecalculatePediatricDose() => PediatricDoseResult = PediatricDoseRules.ResultText(SelectedPediatricRule, PediatricWeightText);

    /// <summary>Copia la dosis calculada a las indicaciones del medicamento ("Dar 7 ml vía oral ...").</summary>
    [RelayCommand]
    public void ApplyPediatricDose()
    {
        var rule = SelectedPediatricRule;
        if (PediatricDoseRules.DosageText(rule, PediatricWeightText) is not { } text) return;

        PrescriptionDosage = text;
        if (string.IsNullOrWhiteSpace(PrescriptionSearchText) && string.IsNullOrWhiteSpace(PrescriptionMedicineName))
        {
            PrescriptionMedicineName = rule!.Medication;
            // Una lista de sugerencias abierta con ↓ no debe quedar marcando otro medicamento
            IsPrescriptionDropdownOpen = false;
        }
    }

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
            // Con el principio activo guardado: el renglón oculto avisa aunque su producto ya no esté en el catálogo
            if (AllergyRules.CheckMedication(_session.SelectedPatient, product, item.MedicineName, item.ActiveIngredient).HasConflict)
            {
                skipped.Add(item.MedicineName);
                continue;
            }
            // Tal como se recetó: con su principio activo mostrado u oculto (un renglón de antes, sin él, queda igual)
            await AddPrescriptionItemInternalAsync(product, item.MedicineName, item.Dosage, Math.Max(1, item.RecommendedQuantity), item.Presentation,
                item.ActiveIngredient, item.ShowActiveIngredient);
        }

        _workflow.NotifyPrescriptionStateChanged();
        if (skipped.Count > 0)
        {
            _info.InfoModalMessage = $"No se repitió {string.Join(", ", skipped)}: coincide con las alergias registradas del paciente. Si de todos modos debe recetarse, agréguelo a mano para confirmar la alerta.";
            _info.IsInfoModalVisible = true;
        }
    }

    // ==========================================
    // "CAMBIAR INDICACIÓN" DE UN RENGLÓN (PEDIDO DEL 2026-10-14)
    // ==========================================

    /// <summary>
    /// La corrección está abierta en un renglón que sigue en la receta que se ve (la escalera de Esc no cierra una
    /// corrección que ya no se ve, p. ej. de un renglón que se quitó o que cambió con una recarga).
    /// </summary>
    public bool IsEditingAShownRow(PrescriptionIndicationEditor editor) =>
        editor.Item is { } item && _session.SelectedPatient?.ActivePrescription.Contains(item) == true;

    /// <summary>
    /// Lo pendiente de la receta antes de cualquier salida que la usa o la tapa: terminar la consulta, enviarla a otro
    /// equipo, "+ Medicamento", la vista previa y el "Guardar" de la hoja. Un solo camino para todas, así una salida nueva
    /// no lo olvida. Una "Cambiar indicación" abierta (sección 5 u hoja) se guarda; si su renglón ya no está, solo se
    /// cierra. Con <paramref name="addPendingMedicine"/> (el "Guardar" de la hoja) también se agrega el medicamento listo
    /// para agregar: el elegido, o el de la dosis pediátrica con el buscador vacío, con la revisión de alergias y la
    /// guarda de solo lectura de siempre. false si algo quedó pendiente (la consulta está en otro equipo, o la alerta de
    /// alergia pide confirmar): la salida no sigue y nada se pierde.
    /// </summary>
    public async Task<bool> CommitPendingPrescriptionEditsAsync(bool addPendingMedicine = false)
    {
        foreach (var editor in new[] { PlanIndication, SheetIndication })
        {
            if (!editor.IsEditing) continue;
            await editor.SaveAsync();
            if (editor.IsEditing) return false;
        }
        if (addPendingMedicine && HasPendingMedicine())
        {
            await AddPrescriptionAsync();
            if (IsAllergyAlertOpen || HasPendingMedicine()) return false;
        }
        return true;
    }

    /// <summary>
    /// Un medicamento listo para agregar que aún no se agrega: el elegido, o el que dejó la dosis pediátrica ("Usar") con el
    /// buscador vacío. Lo escrito sin elegir no cuenta (como en v1.39).
    /// </summary>
    private bool HasPendingMedicine() =>
        IsMedicineChosen || (string.IsNullOrWhiteSpace(PrescriptionSearchText) && !string.IsNullOrWhiteSpace(PrescriptionMedicineName));

    /// <summary>Terminó la consulta o cambió el paciente: ninguna corrección queda abierta para la siguiente.</summary>
    public void CancelIndicationEdits()
    {
        SheetIndication.Cancel();
        PlanIndication.Cancel();
    }

    /// <summary>
    /// Un renglón se corrige si es de la receta que se ve (el mismo objeto) y la consulta no está en otro equipo (con aviso).
    /// </summary>
    private bool CanChangeIndication(PatientPrescriptionItem item)
    {
        if (_session.SelectedPatient?.ActivePrescription.Contains(item) != true) return false;
        return !_gate.BlockIfConsultationReadOnly();
    }

    /// <summary>
    /// Guarda la indicación nueva en el mismo renglón: no se rehace el objeto, así conserva su RowId, lo que trae de otro
    /// equipo (ExtensionData), la presentación, la cantidad y "Se surte aquí" (la cuenta no cambia). Se guarda como las demás
    /// ediciones de la receta; desde el paso 8.22 el guardado escribe solo los renglones que cambiaron. Sin texto queda
    /// "Según indicación médica", como al agregar. false si la consulta está en otro equipo (el campo sigue abierto).
    /// </summary>
    private async Task<bool> SaveIndicationAsync(PatientPrescriptionItem item, string text)
    {
        var patient = _session.SelectedPatient;
        // El renglón ya no está (se quitó o cambió el paciente): no hay nada que guardar
        if (patient == null || !patient.ActivePrescription.Contains(item)) return true;
        if (_gate.BlockIfConsultationReadOnly()) return false;

        var dosage = string.IsNullOrWhiteSpace(text) ? DefaultDosage : text.Trim();
        if (dosage == item.Dosage) return true;

        item.Dosage = dosage;
        patient.SyncPrescriptionJson();
        _session.NotifySelectedPatientMutated();
        _workflow.NotifyPrescriptionStateChanged();
        if (_inventoryService != null)
        {
            try { await _inventoryService.SavePatientAsync(patient); }
            catch (Exception ex) { AppLogger.Error("No se pudo guardar la indicación de la receta", ex); }
        }
        return true;
    }

    // ==========================================
    // «MOSTRAR (PRINCIPIO ACTIVO)» DE UN RENGLÓN (PEDIDO DEL 2026-10-21)
    // ==========================================

    /// <summary>
    /// La casilla «Mostrar (…)» de un renglón (hoja de receta y sección 5): la casilla ya cambió
    /// <see cref="PatientPrescriptionItem.ShowsActiveIngredient"/>; aquí se quita o se pone el paréntesis y se guarda.
    /// </summary>
    [RelayCommand]
    public async Task ToggleActiveIngredientAsync(PatientPrescriptionItem? item)
    {
        if (item == null) return;
        await ApplyActiveIngredientAsync(item, item.ShowsActiveIngredient);
    }

    /// <summary>Mostrar u ocultar el principio activo de un renglón, como la casilla. false si no se pudo (sigue como estaba).</summary>
    public Task<bool> SetActiveIngredientShownAsync(PatientPrescriptionItem item, bool show)
    {
        // Un renglón sin principio activo (de antes, genérico o escrito a mano) no tiene casilla ni cambia
        if (!item.HasActiveIngredient) return Task.FromResult(false);
        item.ShowsActiveIngredient = show;
        return ApplyActiveIngredientAsync(item, show);
    }

    /// <summary>
    /// El mismo renglón cambia su texto (no se rehace: conserva su RowId, lo que trae de otro equipo, la indicación, la cantidad
    /// y "Se surte aquí"; la cuenta no cambia) y se guarda como las demás ediciones de la receta, con la marca del renglón del
    /// paso 8.22. Solo un renglón de la receta que se ve y que tiene principio activo; con la consulta en otro equipo, avisa y la
    /// casilla regresa. No cambia la elección de este equipo para el siguiente medicamento (decisión del 2026-10-21).
    /// </summary>
    private async Task<bool> ApplyActiveIngredientAsync(PatientPrescriptionItem item, bool show)
    {
        var patient = _session.SelectedPatient;
        var ingredient = item.ActiveIngredient;
        bool shownNow = PrescriptionNaming.EndsWithActiveIngredient(item.MedicineName, ingredient);
        if (patient == null || !item.HasActiveIngredient || !patient.ActivePrescription.Contains(item) || _gate.BlockIfConsultationReadOnly())
        {
            // La casilla regresa a lo que dice el renglón
            if (item.HasActiveIngredient) item.ShowActiveIngredient = shownNow;
            return false;
        }

        // Solo la casilla de "Indicación" (al recetar) es la elección para el siguiente medicamento; la de un renglón no
        item.ShowActiveIngredient = show;
        item.MedicineName = PrescriptionNaming.WithActiveIngredient(item.MedicineName, ingredient, show);
        patient.SyncPrescriptionJson();
        _session.NotifySelectedPatientMutated();
        _workflow.NotifyPrescriptionStateChanged();
        if (_inventoryService != null)
        {
            try { await _inventoryService.SavePatientAsync(patient); }
            catch (Exception ex) { AppLogger.Error("No se pudo guardar el renglón de la receta", ex); }
        }
        return true;
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
