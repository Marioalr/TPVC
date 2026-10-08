using System;
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
    private bool _isLoading;

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

    [RelayCommand]
    public void CloseEditor()
    {
        IsEditorOpen = false;
    }

    public string ResultsSummary => TotalResultsCount == 1 ? "1 producto" : $"{TotalResultsCount} productos";

    partial void OnTotalResultsCountChanged(int value) => OnPropertyChanged(nameof(ResultsSummary));

    // Quick Action Form state
    [ObservableProperty]
    private bool _isQuickActionOpen;

    [ObservableProperty]
    private string _quickActionTitle = "Consumo Rápido en Consulta";

    [ObservableProperty]
    private bool _isQuickConsumptionMode = true; // True = Salida consulta, False = Entrada

    [ObservableProperty]
    private int _quickActionQuantity = 1;

    [ObservableProperty]
    private string _quickActionNotes = string.Empty;

    public InventoryViewModel(InventoryService inventoryService)
    {
        _inventoryService = inventoryService;

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
    }

    [RelayCommand]
    public async Task LoadDataAsync()
    {
        IsLoading = true;
        try
        {
            var categories = await _inventoryService.GetCategoriesAsync();
            var allCat = new Category { Id = 0, Name = "Todas" };
            var catList = new ObservableCollection<Category> { allCat };
            foreach (var c in categories) catList.Add(c);
            Categories = catList;
            // Conserva la categoría elegida (las instancias cambian al recargar)
            SelectedCategory = Categories.FirstOrDefault(c => c.Id == (SelectedCategory?.Id ?? 0)) ?? allCat;

            await FilterProductsAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task FilterProductsAsync()
    {
        IsLoading = true;
        try
        {
            int? catId = (SelectedCategory != null && SelectedCategory.Id > 0) ? SelectedCategory.Id : null;
            var list = await _inventoryService.GetProductsAsync(
                searchText: SearchText,
                categoryId: catId,
                onlyLowStock: FilterLowStock,
                onlyExpiring: FilterExpiring
            );

            Products = new ObservableCollection<Product>(list);
            TotalResultsCount = Products.Count;
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        // Evita una consulta a la base por cada tecla
        _ = _searchDebouncer.RunAsync(FilterProductsAsync);
    }

    [RelayCommand]
    public void ClearSearch()
    {
        SearchText = string.Empty;
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
        QuickActionTitle = $"Consumo en Consulta: {target.Name}";
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
        QuickActionTitle = $"Entrada de Stock: {target.Name}";
        QuickActionQuantity = 5;
        QuickActionNotes = "Adquisición de insumos";
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

        IsLoading = true;
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
        finally
        {
            IsLoading = false;
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
