using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Rules;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Hoja de servicios y procedimientos clínicos (fase 6, paso 15b): buscar el servicio, agregarlo al
/// expediente y, durante la consulta, cargarlo a la cuenta. El catálogo completo se abre con el
/// explorador; lo que se elige ahí vuelve aquí (el coordinador conecta CatalogBrowser.AddService).
/// Pedido del 2026-10-14: en la consulta en curso, cada servicio muestra sus insumos y el médico los ajusta
/// (− / +, X y agregar con la búsqueda) solo para esta consulta, y cambia sus unidades con − / +; la cuenta
/// carga o descuenta solo la diferencia (<see cref="ServiceRowSupplies"/>).
/// </summary>
public partial class ClinicalServicesSheetViewModel : ObservableObject
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

    public ClinicalServicesSheetViewModel(
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
    // MODAL DE SERVICIOS Y PROCEDIMIENTOS CLÍNICOS
    // ==========================================
    [ObservableProperty]
    private bool _isServicesModalOpen;

    [ObservableProperty]
    private string _serviceModalSearchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ClinicalService> _serviceModalSuggestions = new();

    [ObservableProperty]
    private bool _isServiceModalDropdownOpen;

    [ObservableProperty]
    private ClinicalService? _selectedServiceModalItem;

    [ObservableProperty]
    private string _serviceModalNotes = string.Empty;

    [ObservableProperty]
    private int _serviceModalQuantity = 1;

    partial void OnServiceModalSearchTextChanged(string value)
    {
        ApplyServiceModalSuggestions(value);
    }

    public void ApplyServiceModalSuggestions(string query)
    {
        var matches = string.IsNullOrWhiteSpace(query)
            ? _catalog.Services.Take(20).ToList()
            : _catalog.Services
                .Where(s => TextSearch.Matches(TextSearch.Normalize(query), s.Name, s.Code, s.Category))
                .Take(20)
                .ToList();

        ServiceModalSuggestions = new ObservableCollection<ClinicalService>(matches);
        SelectedServiceModalItem = ServiceModalSuggestions.FirstOrDefault();
        IsServiceModalDropdownOpen = !string.IsNullOrWhiteSpace(query) && ServiceModalSuggestions.Count > 0;
    }

    // ==========================================
    // OPERACIONES DE MODAL DE SERVICIOS CLÍNICOS
    // ==========================================

    [RelayCommand]
    public async Task OpenServicesModalAsync()
    {
        OpenServicesModal();
        if (_catalog.Services.Count == 0 && _inventoryService != null)
        {
            await _workflow.LoadServicesAsync();
            ApplyServiceModalSuggestions(string.Empty);
            IsServiceModalDropdownOpen = false;
        }
    }

    public void OpenServicesModal()
    {
        if (_gate.BlockIfConsultationReadOnly()) return;
        CloseSupplyEditor();
        if (_session.SelectedPatient == null)
        {
            _info.InfoModalMessage = "Seleccione o agregue un paciente primero para gestionar los servicios y procedimientos clínicos.";
            _info.IsInfoModalVisible = true;
            return;
        }

        ServiceModalSearchText = string.Empty;
        ServiceModalNotes = string.Empty;
        ServiceModalQuantity = 1;
        ApplyServiceModalSuggestions(string.Empty);
        IsServiceModalDropdownOpen = false;
        MarkRowsFromEarlierConsultations();
        IsServicesModalOpen = true;
    }

    [RelayCommand]
    public async Task OpenServiceCatalogAsync()
    {
        if (_gate.BlockIfConsultationReadOnly()) return;
        if (_catalog.Services.Count == 0 && _inventoryService != null)
        {
            await _workflow.LoadServicesAsync();
        }
        _catalogBrowser.CatalogBrowserMode = "Services";
        _catalogBrowser.CatalogBrowserTitle = "Catálogo de Servicios y Procedimientos Clínicos";
        _catalogBrowser.CatalogBrowserSubtitle = "Explore el catálogo oficial de servicios clínicos y agregue el procedimiento realizado al expediente.";
        _catalogBrowser.CatalogBrowserSearchText = string.Empty;
        _catalogBrowser.ApplyCatalogBrowserFilter();
        _catalogBrowser.IsCatalogBrowserModalOpen = true;
    }

    [RelayCommand]
    public void CloseServicesModal()
    {
        CloseSupplyEditor();
        IsServicesModalOpen = false;
    }

    /// <summary>La hoja se cierra por cualquier camino (Esc, Cerrar, el cobro, la consulta enviada): los insumos abiertos también.</summary>
    partial void OnIsServicesModalOpenChanged(bool value)
    {
        if (!value) CloseSupplyEditor();
    }

    [RelayCommand]
    public async Task SelectAndAddServiceModalSuggestionAsync(ClinicalService? service)
    {
        if (_session.SelectedPatient == null || _gate.BlockIfConsultationReadOnly()) return;
        CloseSupplyEditor();

        string name;
        decimal price;
        string category;
        string code;
        int serviceId;
        Guid? serviceSyncId;

        if (service != null)
        {
            name = service.Name;
            price = service.Price;
            category = service.Category;
            code = service.Code;
            serviceId = service.Id;
            serviceSyncId = service.SyncId;
        }
        else if (!string.IsNullOrWhiteSpace(ServiceModalSearchText))
        {
            name = ServiceModalSearchText.Trim();
            var serviceObj = _catalog.Services.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            price = serviceObj?.Price ?? 200.00m;
            category = serviceObj?.Category ?? "Consulta";
            code = serviceObj?.Code ?? "SERV-001";
            serviceId = serviceObj?.Id ?? 0;
            serviceSyncId = serviceObj?.SyncId;
        }
        else
        {
            return;
        }

        var notes = string.IsNullOrWhiteSpace(ServiceModalNotes)
            ? "Procedimiento clínico en consulta"
            : ServiceModalNotes.Trim();

        var qty = ServiceModalQuantity > 0 ? ServiceModalQuantity : 1;

        // Paso 8.18: el SyncId del servicio vale en cualquier equipo; el Id local sigue para las PC anteriores
        var newItem = new PatientServiceItem
        {
            ServiceId = serviceId,
            ServiceSyncId = serviceSyncId is { } syncId && syncId != Guid.Empty ? syncId : null,
            ServiceCode = code,
            ServiceName = name,
            Category = category,
            Notes = notes,
            Price = price,
            Quantity = qty
        };

        var list = new List<PatientServiceItem>(_session.SelectedPatient.ActiveServices) { newItem };
        _session.SelectedPatient.ActiveServices = list;
        _session.SelectedPatient.SyncServicesJson();

        // Durante la consulta, el servicio realizado se carga de inmediato a la cuenta; el renglón
        // recuerda qué consulta lo cargó y con qué insumos, para que la X descuente solo eso
        if (_active.HasActiveConsultation && _account.ChargeService(newItem))
        {
            newItem.ChargedConsultationFolio = _active.ActiveConsultation?.Folio;
            newItem.ChargedUnits = qty;
            _session.SelectedPatient.SyncServicesJson();
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

        ServiceModalSearchText = string.Empty;
        ServiceModalNotes = string.Empty;
        ServiceModalQuantity = 1;
        SelectedServiceModalItem = null;
        ApplyServiceModalSuggestions(string.Empty);

        _session.NotifySelectedPatientMutated();
    }

    [RelayCommand]
    public async Task RemoveServiceItemFromPatientAsync(PatientServiceItem? item)
    {
        if (_session.SelectedPatient == null || item == null || _gate.BlockIfConsultationReadOnly()) return;
        CloseSupplyEditor();

        // Lo que esta consulta cargó a la cuenta por el renglón deja de cobrarse: el servicio por sus unidades y su lista
        // de insumos tal como quedó (sin lista, como antes); también con 0 unidades, por si su lista cargó algo. Lo de una
        // consulta anterior ya se cobró con ella
        if (IsFromThisConsultation(item))
        {
            _account.UnchargeServiceRow(item, Math.Max(0, item.ChargedUnits));
        }

        var list = new List<PatientServiceItem>(_session.SelectedPatient.ActiveServices);
        list.Remove(item);
        _session.SelectedPatient.ActiveServices = list;
        _session.SelectedPatient.SyncServicesJson();

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

    /// <summary>El renglón lo cargó la consulta en curso del paciente (los demás ya se cobraron o se descartaron).</summary>
    private bool IsFromThisConsultation(PatientServiceItem item) =>
        IsConsultationForSelectedPatient && ServiceRowSupplies.IsFromConsultation(item, _active.ActiveConsultation?.Folio);

    /// <summary>
    /// La hoja conserva lo hecho en consultas anteriores: esos renglones se ven y se pueden quitar, pero
    /// su X no descuenta nada de la cuenta de hoy (ya se cobraron con su consulta).
    /// </summary>
    private void MarkRowsFromEarlierConsultations()
    {
        if (_session.SelectedPatient == null) return;
        foreach (var row in _session.SelectedPatient.ActiveServices)
        {
            row.IsFromEarlierConsultation = !IsFromThisConsultation(row);
        }
    }

    [RelayCommand]
    public async Task SaveServicesModalAsync()
    {
        if (_gate.BlockIfConsultationReadOnly()) return;
        CloseSupplyEditor();
        if (_session.SelectedPatient != null)
        {
            _session.SelectedPatient.SyncServicesJson();
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

        IsServicesModalOpen = false;
        _feedback.ShowSuccess($"¡Servicios clínicos guardados correctamente en el expediente de {_session.SelectedPatient?.FullName}!");
    }

    // ==========================================
    // INSUMOS DE UN SERVICIO, SOLO PARA ESTA CONSULTA (2026-10-14)
    // ==========================================

    /// <summary>
    /// El renglón con sus insumos abiertos (uno a la vez). Solo un renglón cargado por la consulta en curso; los de una
    /// consulta anterior solo muestran su lista.
    /// </summary>
    [ObservableProperty]
    private PatientServiceItem? _supplyEditorRow;

    public bool IsSupplyEditorOpen => SupplyEditorRow != null;

    partial void OnSupplyEditorRowChanged(PatientServiceItem? oldValue, PatientServiceItem? newValue)
    {
        if (oldValue != null)
        {
            oldValue.IsEditingSupplies = false;
            oldValue.PropertyChanged -= EditorRow_PropertyChanged;
        }
        if (newValue != null)
        {
            newValue.IsEditingSupplies = true;
            newValue.PropertyChanged += EditorRow_PropertyChanged;
        }
        OnPropertyChanged(nameof(IsSupplyEditorOpen));
        RaiseSupplyEditorEntries();
    }

    private void EditorRow_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PatientServiceItem.Supplies)) RaiseSupplyEditorEntries();
    }

    /// <summary>
    /// Un renglón sin lista (de una versión anterior) muestra al abrirse lo que descontaría hoy quitarlo, pero esa lista vive
    /// aquí y no en el renglón: pasa al renglón con el primer cambio de verdad (y se guarda con él). Así abrir los insumos, o
    /// guardar el expediente por otra cosa mientras están abiertos, no cambia el renglón.
    /// </summary>
    private List<PatientServiceSupply>? _legacyPreview;

    /// <summary>Los insumos que muestra el editor abierto: la lista del renglón o, en uno sin lista, la de vista previa.</summary>
    public IReadOnlyList<PatientServiceSupply> SupplyEditorEntries =>
        (IReadOnlyList<PatientServiceSupply>?)SupplyEditorRow?.Supplies ?? _legacyPreview ?? (IReadOnlyList<PatientServiceSupply>)Array.Empty<PatientServiceSupply>();

    public bool HasSupplyEditorEntries => SupplyEditorEntries.Count > 0;

    private void RaiseSupplyEditorEntries()
    {
        OnPropertyChanged(nameof(SupplyEditorEntries));
        OnPropertyChanged(nameof(HasSupplyEditorEntries));
    }

    /// <summary>La lista que se cambia en el renglón abierto: la suya, o la de vista previa (que todavía no es suya).</summary>
    private List<PatientServiceSupply>? EditorList(PatientServiceItem row) =>
        row.Supplies ?? (ReferenceEquals(row, SupplyEditorRow) ? _legacyPreview : null);

    /// <summary>
    /// El renglón queda con estos insumos (los que tienen piezas) y la vista previa deja de serlo. Si cambió cuáles son, en
    /// otra instancia de la lista: WPF no vuelve a dibujar una lista que se le asigna otra vez; si solo cambiaron piezas, la
    /// misma (cada insumo avisa de sus piezas y el foco se queda donde estaba).
    /// </summary>
    private void AdoptList(PatientServiceItem row, IEnumerable<PatientServiceSupply> entries)
    {
        var kept = entries.Where(e => e != null && e.Quantity > 0).ToList();
        if (ReferenceEquals(row, SupplyEditorRow)) _legacyPreview = null;
        if (row.Supplies != null && row.Supplies.SequenceEqual(kept))
        {
            row.NotifySuppliesChanged();
            return;
        }
        row.Supplies = kept;
    }

    [ObservableProperty]
    private string _serviceSupplySearchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<Product> _serviceSupplySuggestions = new();

    [ObservableProperty]
    private Product? _selectedServiceSupplySuggestion;

    [ObservableProperty]
    private bool _isServiceSupplyDropdownOpen;

    partial void OnServiceSupplySearchTextChanged(string value) => ApplyServiceSupplySuggestions(value);

    /// <summary>
    /// La búsqueda de la consulta y del Punto de venta (<see cref="TextSearch.RankProducts"/>): solo insumos del inventario,
    /// con existencias primero dentro de cada grupo.
    /// </summary>
    public void ApplyServiceSupplySuggestions(string query)
    {
        var supplies = _catalog.AvailableProducts.Where(p => p.IsActive && CatalogStore.IsProductSupply(p));
        ServiceSupplySuggestions = new ObservableCollection<Product>(TextSearch.RankProducts(supplies, query));
        SelectedServiceSupplySuggestion = ServiceSupplySuggestions.FirstOrDefault();
        IsServiceSupplyDropdownOpen = ServiceSupplySuggestions.Count > 0;
    }

    /// <summary>↑ ↓ en la búsqueda de insumos: la sugerencia anterior o la siguiente (da la vuelta).</summary>
    public void MoveServiceSupplySuggestion(int step)
    {
        int count = ServiceSupplySuggestions.Count;
        if (count == 0) return;
        int index = SelectedServiceSupplySuggestion == null ? -1 : ServiceSupplySuggestions.IndexOf(SelectedServiceSupplySuggestion);
        index = ((index + step) % count + count) % count;
        SelectedServiceSupplySuggestion = ServiceSupplySuggestions[index];
    }

    /// <summary>El renglón es de la consulta en curso y se puede cambiar aquí (no está en otro equipo).</summary>
    private bool CanAdjust(PatientServiceItem row) =>
        _session.SelectedPatient != null && _session.SelectedPatient.ActiveServices.Contains(row) && IsFromThisConsultation(row);

    /// <summary>Abre los insumos de un renglón de esta consulta, o los cierra si ya estaban abiertos.</summary>
    [RelayCommand]
    public void ToggleServiceSupplies(PatientServiceItem? row)
    {
        if (row == null) return;
        if (ReferenceEquals(SupplyEditorRow, row))
        {
            CloseSupplyEditor();
            return;
        }
        if (_gate.BlockIfConsultationReadOnly() || !CanAdjust(row)) return;

        CloseSupplyEditor();
        _legacyPreview = row.Supplies == null ? _account.LegacyServiceSupplies(row) : null;
        ServiceSupplySearchText = string.Empty;
        IsServiceSupplyDropdownOpen = false;
        SupplyEditorRow = row;
    }

    /// <summary>Cierra los insumos abiertos (Esc los cierra antes que la hoja).</summary>
    [RelayCommand]
    public void CloseSupplyEditor()
    {
        _legacyPreview = null;
        if (ServiceSupplySearchText.Length > 0) ServiceSupplySearchText = string.Empty;
        IsServiceSupplyDropdownOpen = false;
        SupplyEditorRow = null;
    }

    [RelayCommand]
    public Task IncreaseServiceSupplyAsync(PatientServiceSupply? entry) =>
        entry == null ? Task.CompletedTask : SetServiceSupplyQuantityAsync(entry, entry.Quantity + 1);

    [RelayCommand]
    public Task DecreaseServiceSupplyAsync(PatientServiceSupply? entry) =>
        entry == null ? Task.CompletedTask : SetServiceSupplyQuantityAsync(entry, entry.Quantity - 1);

    [RelayCommand]
    public Task RemoveServiceSupplyAsync(PatientServiceSupply? entry) =>
        entry == null ? Task.CompletedTask : SetServiceSupplyQuantityAsync(entry, 0);

    /// <summary>
    /// Cambia las piezas de un insumo del renglón abierto (+, − o la X en 0), solo para esta consulta: lo que se suma se carga
    /// a la cuenta y lo que se resta se descuenta, del mismo renglón de la cuenta con que se cargó. Como en la hoja de insumos
    /// (v1.18.7), un insumo del inventario no pasa de sus existencias (cuenta todo lo del producto que ya está en la cuenta) y
    /// se avisa. La configuración del servicio no cambia.
    /// </summary>
    public async Task SetServiceSupplyQuantityAsync(PatientServiceSupply entry, int quantity)
    {
        var row = SupplyEditorRow;
        var list = row == null ? null : EditorList(row);
        if (row == null || list == null || _gate.BlockIfConsultationReadOnly() || !CanAdjust(row) || !list.Contains(entry)) return;

        int difference = Math.Max(0, quantity) - entry.Quantity;
        if (difference > 0)
        {
            var line = _account.ServiceSupplyLine(entry);
            var product = line.LineId > 0 ? _catalog.AvailableProducts.FirstOrDefault(p => p.Id == line.LineId) : null;
            if (line.LineId > 0 && product == null)
            {
                _feedback.ShowWarning($"'{entry.SupplyName}' ya no está en el inventario: no se pueden cargar más piezas.");
                return;
            }
            if (product != null && !FitsInStock(product, ref difference, entry.SupplyName)) return;
            if (!_account.ChargeServiceSupply(entry, product, line, difference)) return;
        }
        else if (difference < 0)
        {
            _account.UnchargeServiceSupply(entry, -difference);
        }
        else
        {
            return;
        }

        // Un insumo en 0 sale de la lista; la de vista previa pasa al renglón con este primer cambio
        AdoptList(row, list);
        await SaveServiceRowAsync(row);
    }

    /// <summary>
    /// Agrega un insumo del inventario al renglón abierto (Enter o clic en la búsqueda): una pieza, cargada a la cuenta; si el
    /// renglón ya lo tiene, le suma una. Con el mismo tope de existencias que + .
    /// </summary>
    [RelayCommand]
    public async Task AddServiceSupplyAsync(Product? product)
    {
        var row = SupplyEditorRow;
        product ??= SelectedServiceSupplySuggestion ?? ServiceSupplySuggestions.FirstOrDefault();
        if (row == null || product == null || _gate.BlockIfConsultationReadOnly() || !CanAdjust(row)) return;
        var list = EditorList(row) ?? new List<PatientServiceSupply>();

        var existing = ServiceRowSupplies.Find(list, product);
        if (existing != null)
        {
            ClearServiceSupplySearch();
            await SetServiceSupplyQuantityAsync(existing, existing.Quantity + 1);
            return;
        }

        int one = 1;
        if (!FitsInStock(product, ref one, product.Name)) return;
        var entry = ServiceRowSupplies.NewEntry(product, 0);
        if (!_account.ChargeServiceSupply(entry, product, PatientSheetRows.SupplyLine.ForProduct(product), 1)) return;
        AdoptList(row, list.Append(entry));
        ClearServiceSupplySearch();
        await SaveServiceRowAsync(row);
    }

    private void ClearServiceSupplySearch()
    {
        ServiceSupplySearchText = string.Empty;
        IsServiceSupplyDropdownOpen = false;
    }

    /// <summary>
    /// El tope del Punto de venta: lo que ya está en la cuenta de ese producto (otros renglones, servicios, receta) más lo que
    /// se carga no pasa de sus existencias. Si no cabe todo, se avisa y se carga lo que quepa; false si no cabe nada.
    /// </summary>
    private bool FitsInStock(Product product, ref int pieces, string name)
    {
        int fit = _account.PiecesThatFit(product, pieces);
        if (fit < pieces) _feedback.ShowWarning($"No hay más existencias de '{name}' ({product.CurrentStock}).");
        pieces = fit;
        return pieces > 0;
    }

    [RelayCommand]
    public Task IncreaseServiceUnitsAsync(PatientServiceItem? row) =>
        row == null ? Task.CompletedTask : SetServiceUnitsAsync(row, row.Quantity + 1);

    [RelayCommand]
    public Task DecreaseServiceUnitsAsync(PatientServiceItem? row) =>
        row == null ? Task.CompletedTask : SetServiceUnitsAsync(row, row.Quantity - 1);

    /// <summary>
    /// Cambia las unidades de un servicio de esta consulta: una más carga el servicio y los insumos por defecto de una unidad
    /// (se suman a su lista); una menos descuenta lo mismo, cada insumo hasta lo que tenga la lista. Lo ajustado a mano se
    /// queda (<see cref="ServiceRowSupplies"/>). En 0 se quita como la X.
    /// </summary>
    public async Task SetServiceUnitsAsync(PatientServiceItem row, int units)
    {
        if (_session.SelectedPatient == null || _gate.BlockIfConsultationReadOnly() || !CanAdjust(row)) return;
        if (units <= 0)
        {
            await RemoveServiceItemFromPatientAsync(row);
            return;
        }

        int delta = units - row.Quantity;
        if (delta == 0) return;
        // Un renglón sin lista con sus insumos abiertos: la vista previa es lo que cargó, y con el cambio pasa a ser suya
        if (row.Supplies == null && EditorList(row) is { } preview)
        {
            row.Supplies = preview;
            if (!_account.ChangeServiceUnits(row, delta))
            {
                row.Supplies = null;
                return;
            }
            _legacyPreview = null;
        }
        else if (!_account.ChangeServiceUnits(row, delta))
        {
            return;
        }
        row.Quantity = units;
        row.ChargedUnits = Math.Max(0, row.ChargedUnits + delta);
        await SaveServiceRowAsync(row);
    }

    /// <summary>Guarda la hoja después de un cambio en un renglón.</summary>
    private async Task SaveServiceRowAsync(PatientServiceItem row)
    {
        if (_session.SelectedPatient == null || !_session.SelectedPatient.ActiveServices.Contains(row)) return;
        _session.SelectedPatient.SyncServicesJson();
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
}
