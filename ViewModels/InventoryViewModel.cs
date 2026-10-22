using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

public partial class InventoryViewModel : ObservableObject
{
    private readonly InventoryService _inventoryService;
    private readonly Debouncer _searchDebouncer = new();

    [ObservableProperty]
    private ObservableCollection<Product> _products = new();

    [ObservableProperty]
    private ObservableCollection<Category> _categories = new();

    [ObservableProperty]
    private Category? _selectedCategory;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _filterLowStock;

    [ObservableProperty]
    private bool _filterExpiring;

    [ObservableProperty]
    private Product? _selectedProduct;

    [ObservableProperty]
    private int _totalResultsCount;

    // ================================================================== edición en panel lateral

    /// <summary>Alta o edición de un producto en el panel lateral (antes una ventana aparte).</summary>
    [ObservableProperty]
    private ProductDialogViewModel? _editor;

    [ObservableProperty]
    private bool _isEditorOpen;

    [RelayCommand]
    public Task NewProductAsync() => OpenEditorAsync(null);

    [RelayCommand]
    public Task EditProductAsync(Product? product) => product == null ? Task.CompletedTask : OpenEditorAsync(product);

    private async Task OpenEditorAsync(Product? product)
    {
        IsQuickActionOpen = false;
        var editor = new ProductDialogViewModel(_inventoryService, CloseEditor);
        editor.Saved += () => _ = FilterProductsAsync();
        await editor.InitializeAsync(product);
        Editor = editor;
        IsEditorOpen = true;
    }

    public void CloseEditor()
    {
        IsEditorOpen = false;
    }

    // ================================================================== ingresar pedido del proveedor

    /// <summary>«Ingresar pedido»: la hoja con el correo pegado y sus renglones.</summary>
    [ObservableProperty]
    private SupplierOrderViewModel? _orderEntry;

    [ObservableProperty]
    private bool _isOrderEntryOpen;

    [RelayCommand]
    public void OpenOrderEntry()
    {
        IsEditorOpen = false;
        IsQuickActionOpen = false;
        var entry = new SupplierOrderViewModel(_inventoryService, _preferences);
        entry.Closed += () => IsOrderEntryOpen = false;
        entry.Received += message =>
        {
            _notifications?.Success(message);
            _ = FilterProductsAsync();
        };
        OrderEntry = entry;
        IsOrderEntryOpen = true;
    }

    /// <summary>Lo que costó lo que hay en existencia de los productos de la lista (con su filtro), al costo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultsSummary))]
    private decimal _listInventoryValue;

    private static readonly System.Globalization.CultureInfo Mexican = System.Globalization.CultureInfo.GetCultureInfo("es-MX");

    /// <summary>"72 productos · $18,430 al costo en existencia" (sin la parte del valor si nada tiene costo).</summary>
    public string ResultsSummary =>
        (TotalResultsCount == 1 ? "1 producto" : $"{TotalResultsCount} productos") +
        (ListInventoryValue > 0 ? $" · {ListInventoryValue.ToString("C0", Mexican)} al costo en existencia" : string.Empty);

    partial void OnTotalResultsCountChanged(int value) => OnPropertyChanged(nameof(ResultsSummary));

    // Quick Action Form state
    [ObservableProperty]
    private bool _isQuickActionOpen;

    [ObservableProperty]
    private bool _isQuickConsumptionMode = true; // True = Salida consulta, False = Entrada

    [ObservableProperty]
    private int _quickActionQuantity = 1;

    [ObservableProperty]
    private string _quickActionNotes = string.Empty;

    [ObservableProperty]
    private string _quickEntryUnitsPerPackageText = string.Empty;

    private readonly NotificationService? _notifications;
    private readonly LocalPreferencesStore? _preferences;

    public InventoryViewModel(InventoryService inventoryService, NotificationService? notifications = null, LocalPreferencesStore? preferences = null)
    {
        _inventoryService = inventoryService;
        _notifications = notifications;
        _preferences = preferences;

        _inventoryService.OnInventoryChanged += () =>
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
                {
                    app.Dispatcher.InvokeAsync(async () => await FilterProductsAsync());
                    return;
                }
            }
            catch
            {
                // Fallback para entornos de pruebas sin runtime de WPF Application
            }

            _ = FilterProductsAsync();
        };

        // Catálogo repetido con lo que llegó de otro equipo (la tablet edita el catálogo desde el 2026-10-22)
        _inventoryService.OnSyncNoticesChanged += () => _ = LoadDuplicateCatalogNoticeAsync();
    }

    // ================================================================== catálogo repetido con lo que llegó de otro equipo

    private IReadOnlyList<DuplicateCatalogPair> _duplicateCatalogPairs = Array.Empty<DuplicateCatalogPair>();

    /// <summary>El aviso de productos, servicios o categorías que pueden estar dados de alta dos veces (se queda hasta «Entendido»).</summary>
    [ObservableProperty]
    private string _duplicateCatalogNoticeText = string.Empty;

    public bool HasDuplicateCatalogNotice => DuplicateCatalogNoticeText.Length > 0;

    partial void OnDuplicateCatalogNoticeTextChanged(string value) => OnPropertyChanged(nameof(HasDuplicateCatalogNotice));

    /// <summary>Los pares que se muestran, el más reciente primero.</summary>
    public IReadOnlyList<DuplicateCatalogPair> DuplicateCatalogPairs => _duplicateCatalogPairs;

    /// <summary>Lee el aviso de la base (depurado: lo que ya no aplica se quita) y lo muestra.</summary>
    public async Task LoadDuplicateCatalogNoticeAsync()
    {
        try
        {
            ShowDuplicateCatalogNotice((await _inventoryService.GetSyncNoticesAsync()).DuplicateCatalog);
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo leer el aviso del catálogo repetido", ex);
        }
    }

    public void ShowDuplicateCatalogNotice(IReadOnlyList<DuplicateCatalogPair> pairs)
    {
        _duplicateCatalogPairs = pairs;
        DuplicateCatalogNoticeText = new SyncArrivals(Array.Empty<AppointmentOverlap>(), Array.Empty<DuplicatePatientPair>(), pairs).DuplicateCatalogNotice;
        OnPropertyChanged(nameof(DuplicateCatalogPairs));
    }

    /// <summary>«Entendido»: esos pares no vuelven a avisar en este equipo.</summary>
    [RelayCommand]
    public async Task DismissDuplicateCatalogNoticeAsync()
    {
        await _inventoryService.DismissSyncNoticesAsync(SyncNoticeKind.DuplicateCatalog);
        ShowDuplicateCatalogNotice(Array.Empty<DuplicateCatalogPair>());
    }

    public async Task LoadDataAsync()
    {
        var categories = await _inventoryService.GetCategoriesAsync();
        var allCat = new Category { Id = 0, Name = "Todas" };
        var catList = new ObservableCollection<Category> { allCat };
        foreach (var c in categories) catList.Add(c);
        Categories = catList;
        // Conserva la categoría elegida (las instancias cambian al recargar)
        SelectedCategory = Categories.FirstOrDefault(c => c.Id == (SelectedCategory?.Id ?? 0)) ?? allCat;

        await FilterProductsAsync();
        await LoadDuplicateCatalogNoticeAsync();
    }

    public async Task FilterProductsAsync()
    {
        int? catId = (SelectedCategory != null && SelectedCategory.Id > 0) ? SelectedCategory.Id : null;
        var list = await _inventoryService.GetProductsAsync(
            searchText: SearchText,
            categoryId: catId,
            onlyLowStock: FilterLowStock,
            onlyExpiring: FilterExpiring
        );

        Products = new ObservableCollection<Product>(list);
        ListInventoryValue = list.Sum(p => p.InventoryValue);
        TotalResultsCount = Products.Count;
    }

    partial void OnSearchTextChanged(string value)
    {
        // Evita una consulta a la base por cada tecla
        _ = _searchDebouncer.RunAsync(FilterProductsAsync);
    }

    partial void OnSelectedCategoryChanged(Category? value)
    {
        _ = FilterProductsAsync();
    }

    partial void OnFilterLowStockChanged(bool value)
    {
        _ = FilterProductsAsync();
    }

    partial void OnFilterExpiringChanged(bool value)
    {
        _ = FilterProductsAsync();
    }

    [RelayCommand]
    public void OpenQuickConsumption(Product? product)
    {
        var target = product ?? SelectedProduct;
        if (target == null) return;

        SelectedProduct = target;
        IsEditorOpen = false;
        IsQuickConsumptionMode = true;
        QuickActionQuantity = 1;
        QuickActionNotes = "Uso en consulta médica";
        IsQuickActionOpen = true;
    }

    [RelayCommand]
    public void OpenQuickEntry(Product? product)
    {
        var target = product ?? SelectedProduct;
        if (target == null) return;

        SelectedProduct = target;
        IsEditorOpen = false;
        IsQuickConsumptionMode = false;
        QuickActionQuantity = 5;
        QuickActionNotes = "Adquisición de insumos";
        QuickEntryUnitsPerPackageText = target.UnitsPerPackage > 1 ? target.UnitsPerPackage.ToString() : string.Empty;
        IsQuickActionOpen = true;
    }

    [RelayCommand]
    public void CloseQuickAction()
    {
        IsQuickActionOpen = false;
    }

    [RelayCommand]
    public async Task ConfirmQuickActionAsync()
    {
        if (SelectedProduct == null || QuickActionQuantity <= 0) return;

        try
        {
            if (IsQuickConsumptionMode)
            {
                await _inventoryService.RegisterQuickConsultationUsageAsync(
                    SelectedProduct.Id,
                    QuickActionQuantity,
                    QuickActionNotes
                );
            }
            else
            {
                int unitsPerPkg = int.TryParse(QuickEntryUnitsPerPackageText?.Trim(), out int parsedUnits) && parsedUnits > 1
                    ? parsedUnits
                    : 1;
                if (SelectedProduct.UnitsPerPackage != unitsPerPkg)
                {
                    SelectedProduct.UnitsPerPackage = unitsPerPkg;
                    await _inventoryService.SaveProductAsync(SelectedProduct, SelectedProduct.CurrentStock);
                }

                await _inventoryService.RegisterMovementAsync(new InventoryMovement
                {
                    ProductId = SelectedProduct.Id,
                    MovementType = MovementType.EntradaCompra,
                    Quantity = QuickActionQuantity,
                    Reason = QuickActionNotes,
                    Responsible = "Dr. Consulta"
                });
            }

            IsQuickActionOpen = false;
            await FilterProductsAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error en movimiento rápido de inventario", ex);
            DialogService.ShowError($"No se pudo registrar el movimiento:\n{ex.Message}");
        }
    }

    [RelayCommand]
    public async Task DeleteProductAsync(Product? product)
    {
        var target = product ?? SelectedProduct;
        if (target == null) return;

        if (!DialogService.Confirm(
                $"¿Dar de baja '{target.Name}' ({target.Code}) del catálogo?\n\nEl historial de movimientos se conserva.",
                "Dar de baja producto"))
        {
            return;
        }

        await _inventoryService.DeleteProductAsync(target.Id);
        await FilterProductsAsync();
    }
}
