using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Hoja de insumos y material clínico (fase 6, paso 15a): buscar el insumo, agregarlo al expediente y,
/// durante la consulta, cargarlo a la cuenta. El catálogo completo se abre con el explorador; lo que se
/// elige ahí vuelve aquí (el coordinador conecta CatalogBrowser.AddSupply).
/// </summary>
public partial class SuppliesSheetViewModel : ObservableObject
{
    private readonly InventoryService? _inventoryService;
    private readonly CatalogStore _catalog;
    private readonly PatientSession _session;
    private readonly ActiveConsultationSession _active;
    private readonly ConsultationAccount _account;
    private readonly InfoDialogState _info;
    private readonly PatientsFeedback _feedback;
    private readonly CatalogBrowserViewModel _catalogBrowser;
    private readonly IConsultationGate _gate;
    private readonly IPatientsWorkflow _workflow;

    public SuppliesSheetViewModel(
        InventoryService? inventoryService,
        CatalogStore catalog,
        PatientSession session,
        ActiveConsultationSession active,
        ConsultationAccount account,
        InfoDialogState info,
        PatientsFeedback feedback,
        CatalogBrowserViewModel catalogBrowser,
        IConsultationGate gate,
        IPatientsWorkflow workflow)
    {
        _inventoryService = inventoryService;
        _catalog = catalog;
        _session = session;
        _active = active;
        _account = account;
        _info = info;
        _feedback = feedback;
        _catalogBrowser = catalogBrowser;
        _gate = gate;
        _workflow = workflow;
    }

    /// <summary>La consulta en curso es la del expediente abierto (la misma expresión que la ficha).</summary>
    private bool IsConsultationForSelectedPatient =>
        _active.HasActiveConsultation && _session.SelectedPatient != null && _active.ActiveConsultation?.PatientId == _session.SelectedPatient.Id;

    // ==========================================
    // MODAL DE INSUMOS Y MATERIAL CLÍNICO
    // ==========================================
    [ObservableProperty]
    private bool _isSuppliesModalOpen;

    [ObservableProperty]
    private string _supplySearchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<Product> _supplyProductSuggestions = new();

    [ObservableProperty]
    private bool _isSupplyDropdownOpen;

    [ObservableProperty]
    private Product? _selectedSupplyProduct;

    [ObservableProperty]
    private string _supplyInstructions = string.Empty;

    [ObservableProperty]
    private int _supplyQuantity = 1;

    partial void OnSupplySearchTextChanged(string value)
    {
        ApplySupplySuggestions(value);
    }

    public void ApplySupplySuggestions(string query)
    {
        var matches = string.IsNullOrWhiteSpace(query)
            ? _catalog.AvailableProducts.Where(CatalogStore.IsProductSupply).Take(20).ToList()
            : _catalog.AvailableProducts
                .Where(CatalogStore.IsProductSupply)
                .Where(p => TextSearch.Matches(TextSearch.Normalize(query), p.Name, p.GenericName, p.Code))
                .Take(20)
                .ToList();

        SupplyProductSuggestions = new ObservableCollection<Product>(matches);
        SelectedSupplyProduct = SupplyProductSuggestions.FirstOrDefault();
        IsSupplyDropdownOpen = !string.IsNullOrWhiteSpace(query) && SupplyProductSuggestions.Count > 0;
    }

    // ==========================================
    // OPERACIONES DE MODAL DE INSUMOS CLÍNICOS
    // ==========================================

    [RelayCommand]
    public async Task OpenSuppliesModalAsync()
    {
        OpenSuppliesModal();
        if (_catalog.AvailableProducts.Count == 0 && _inventoryService != null)
        {
            await _workflow.LoadServicesAsync();
            ApplySupplySuggestions(string.Empty);
            IsSupplyDropdownOpen = false;
        }
    }

    public void OpenSuppliesModal()
    {
        if (_gate.BlockIfConsultationReadOnly()) return;
        if (_session.SelectedPatient == null)
        {
            _info.InfoModalMessage = "Seleccione o agregue un paciente primero para gestionar sus insumos y material clínico.";
            _info.IsInfoModalVisible = true;
            return;
        }

        SupplySearchText = string.Empty;
        SupplyInstructions = string.Empty;
        SupplyQuantity = 1;
        ApplySupplySuggestions(string.Empty);
        IsSupplyDropdownOpen = false;
        MarkRowsFromEarlierConsultations();
        IsSuppliesModalOpen = true;
    }

    [RelayCommand]
    public async Task OpenSupplyCatalogAsync()
    {
        if (_gate.BlockIfConsultationReadOnly()) return;
        if (_catalog.AvailableProducts.Count == 0 && _inventoryService != null)
        {
            await _workflow.LoadServicesAsync();
        }
        _catalogBrowser.CatalogBrowserMode = "Supplies";
        _catalogBrowser.CatalogBrowserTitle = "Catálogo de Insumos y Material de Curación";
        _catalogBrowser.CatalogBrowserSubtitle = "Explore todos los insumos clínicos disponibles y agregue el material utilizado al expediente.";
        _catalogBrowser.CatalogBrowserSearchText = string.Empty;
        _catalogBrowser.ApplyCatalogBrowserFilter();
        _catalogBrowser.IsCatalogBrowserModalOpen = true;
    }

    [RelayCommand]
    public void CloseSuppliesModal()
    {
        IsSuppliesModalOpen = false;
    }

    [RelayCommand]
    public async Task SelectAndAddSupplyProductAsync(Product? product)
    {
        if (_session.SelectedPatient == null || _gate.BlockIfConsultationReadOnly()) return;

        string name;
        decimal unitPrice;
        if (product != null)
        {
            name = string.IsNullOrWhiteSpace(product.Presentation)
                ? product.Name
                : $"{product.Name} ({product.Presentation})";
            unitPrice = product.Price;
        }
        else if (!string.IsNullOrWhiteSpace(SupplySearchText))
        {
            name = SupplySearchText.Trim();
            unitPrice = 35.00m;
        }
        else
        {
            return;
        }

        var instructions = string.IsNullOrWhiteSpace(SupplyInstructions)
            ? "Uso clínico en consulta"
            : SupplyInstructions.Trim();

        var qty = SupplyQuantity > 0 ? SupplyQuantity : 1;

        var newItem = new PatientSupplyItem
        {
            SupplyName = name,
            Instructions = instructions,
            Quantity = qty,
            UnitPrice = unitPrice
        };

        var list = new List<PatientSupplyItem>(_session.SelectedPatient.ActiveSupplies) { newItem };
        _session.SelectedPatient.ActiveSupplies = list;
        _session.SelectedPatient.SyncSuppliesJson();

        // Durante la consulta, lo que se usa se carga de inmediato a la cuenta (no se olvida cobrarlo);
        // el renglón recuerda qué consulta lo cargó para que + / − y la X muevan solo eso
        if (_active.HasActiveConsultation && _account.ChargeSupply(newItem, product ?? _catalog.FindCatalogProduct(name)))
        {
            newItem.ChargedConsultationFolio = _active.ActiveConsultation?.Folio;
            newItem.ChargedUnits = qty;
            _session.SelectedPatient.SyncSuppliesJson();
        }
        newItem.IsFromEarlierConsultation = !IsFromThisConsultation(newItem);

        if (_inventoryService != null)
        {
            try
            {
                await _inventoryService.SavePatientAsync(_session.SelectedPatient);
            }
            catch { }
        }

        SupplySearchText = string.Empty;
        SupplyInstructions = string.Empty;
        SupplyQuantity = 1;
        SelectedSupplyProduct = null;
        ApplySupplySuggestions(string.Empty);

        _session.NotifySelectedPatientMutated();
    }

    [RelayCommand]
    public async Task RemoveSupplyItemFromPatientAsync(PatientSupplyItem? item)
    {
        if (_session.SelectedPatient == null || item == null || _gate.BlockIfConsultationReadOnly()) return;

        // Lo que esta consulta cargó a la cuenta por el renglón deja de cobrarse (lo de una consulta
        // anterior ya se cobró con ella)
        var charged = ChargedInThisConsultation(item);
        if (charged > 0)
        {
            _account.UnchargeSupplyUnits(item, charged);
        }

        var list = new List<PatientSupplyItem>(_session.SelectedPatient.ActiveSupplies);
        list.Remove(item);
        _session.SelectedPatient.ActiveSupplies = list;
        _session.SelectedPatient.SyncSuppliesJson();

        if (_inventoryService != null)
        {
            try
            {
                await _inventoryService.SavePatientAsync(_session.SelectedPatient);
            }
            catch { }
        }

        _session.NotifySelectedPatientMutated();
    }

    // ==========================================
    // CANTIDAD CON + / − (COMO LA CUENTA DEL PUNTO DE VENTA)
    // ==========================================

    [RelayCommand]
    public Task IncreaseSupplyQuantityAsync(PatientSupplyItem? item) =>
        item == null ? Task.CompletedTask : SetSupplyQuantityAsync(item, item.Quantity + 1);

    [RelayCommand]
    public Task DecreaseSupplyQuantityAsync(PatientSupplyItem? item) =>
        item == null ? Task.CompletedTask : SetSupplyQuantityAsync(item, item.Quantity - 1);

    /// <summary>El renglón lo cargó la consulta en curso del paciente (los demás ya se cobraron o se descartaron).</summary>
    private bool IsFromThisConsultation(PatientSupplyItem item) =>
        IsConsultationForSelectedPatient && item.ChargedConsultationFolio == _active.ActiveConsultation?.Folio;

    /// <summary>Piezas del renglón que están en la cuenta de la consulta en curso (0 si las cargó otra).</summary>
    private int ChargedInThisConsultation(PatientSupplyItem item) => IsFromThisConsultation(item) ? item.ChargedUnits : 0;

    /// <summary>
    /// La hoja conserva lo usado en consultas anteriores: esos renglones se ven y se pueden quitar, pero
    /// + / − solo cambian lo de la consulta en curso (así la cuenta de hoy no cobra ni descuenta lo de otra).
    /// </summary>
    private void MarkRowsFromEarlierConsultations()
    {
        if (_session.SelectedPatient == null) return;
        foreach (var row in _session.SelectedPatient.ActiveSupplies)
        {
            row.IsFromEarlierConsultation = !IsFromThisConsultation(row);
        }
    }

    /// <summary>
    /// Cambia las piezas de un insumo usado en la consulta en curso (+, − o escritas a mano): lo que se
    /// suma se carga a la cuenta y lo que se resta se descuenta; llegar a 0 lo quita como la X. Como en
    /// el Punto de venta, un insumo del inventario no pasa de sus existencias (cuenta todo lo del
    /// producto que ya está en la cuenta: otros renglones, servicios, receta).
    /// </summary>
    public async Task SetSupplyQuantityAsync(PatientSupplyItem item, int quantity)
    {
        if (_session.SelectedPatient == null || _gate.BlockIfConsultationReadOnly()) return;
        // Un renglón que ya se quitó (p. ej. el cuadro de cantidad pierde el foco al quitarlo) o de otra consulta
        if (!_session.SelectedPatient.ActiveSupplies.Contains(item) || !IsFromThisConsultation(item)) return;

        if (quantity <= 0)
        {
            await RemoveSupplyItemFromPatientAsync(item);
            return;
        }

        // El mismo producto (o cargo libre) con que se cargó, aunque otro del inventario se llame igual
        int lineId = _account.SupplyLineId(item);
        var product = lineId > 0 ? _catalog.AvailableProducts.FirstOrDefault(p => p.Id == lineId) : null;
        int difference = quantity - item.Quantity;
        if (difference > 0 && lineId > 0 && product == null)
        {
            _feedback.ShowWarning($"'{item.SupplyName}' ya no está en el inventario: no se pueden cargar más piezas.");
            return;
        }
        if (difference > 0 && product != null)
        {
            int room = product.CurrentStock - _account.ChargedProductUnits(product.Id);
            if (difference > room)
            {
                _feedback.ShowWarning($"No hay más existencias de '{item.SupplyName}' ({product.CurrentStock}).");
                difference = Math.Max(0, room);
            }
        }
        if (difference == 0) return;

        if (difference > 0)
        {
            if (!_account.ChargeSupplyUnits(item, product, difference)) return;
            item.ChargedUnits += difference;
        }
        else
        {
            int units = Math.Min(-difference, item.ChargedUnits);
            if (units > 0) _account.UnchargeSupplyUnits(item, units);
            item.ChargedUnits -= units;
        }

        item.Quantity += difference;
        _session.SelectedPatient.SyncSuppliesJson();

        if (_inventoryService != null)
        {
            try
            {
                await _inventoryService.SavePatientAsync(_session.SelectedPatient);
            }
            catch { }
        }

        _session.NotifySelectedPatientMutated();
    }

    [RelayCommand]
    public async Task SaveSuppliesModalAsync()
    {
        if (_gate.BlockIfConsultationReadOnly()) return;
        if (_session.SelectedPatient != null)
        {
            _session.SelectedPatient.SyncSuppliesJson();
            if (_inventoryService != null)
            {
                try
                {
                    await _inventoryService.SavePatientAsync(_session.SelectedPatient);
                }
                catch { }
            }
            _session.NotifySelectedPatientMutated();
        }

        IsSuppliesModalOpen = false;
        _feedback.ShowSuccess($"¡Insumos y materiales clínicos guardados correctamente en el expediente de {_session.SelectedPatient?.FullName}!");
    }
}
