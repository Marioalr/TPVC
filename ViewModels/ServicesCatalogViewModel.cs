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

namespace TPVC.ViewModels;

/// <summary>
/// Configuración → Servicios: los servicios que se cobran (consulta, curación, inyección…), su precio
/// y los insumos que se agregan solos al cargarlos. Se editan en un panel lateral; desactivar un
/// servicio lo quita del POS y de la consulta sin borrar los tickets donde aparece.
/// </summary>
public partial class ServicesCatalogViewModel : ObservableObject
{
    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");
    private readonly InventoryService _inventoryService;
    private readonly NotificationService? _notifications;
    private List<ClinicalService> _all = new();

    public ServicesCatalogViewModel(InventoryService inventoryService, NotificationService? notifications = null)
    {
        _inventoryService = inventoryService;
        _notifications = notifications;
    }

    public static IReadOnlyList<string> DefaultCategories { get; } = new[] { "Consulta", "Curaciones", "Procedimientos", "Diagnóstico", "Certificados" };

    [ObservableProperty]
    private ObservableCollection<ClinicalService> _filteredServices = new();

    [ObservableProperty]
    private ObservableCollection<string> _categoryFilters = new(new[] { "Todos" }.Concat(DefaultCategories));

    [ObservableProperty]
    private string _categoryFilter = "Todos";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _showInactive;

    public string Summary
    {
        get
        {
            int active = _all.Count(s => s.IsActive);
            return active == 1 ? "1 servicio activo" : $"{active} servicios activos";
        }
    }

    /// <summary>Ya se leyó la lista al menos una vez (el resumen de Configuración la necesita).</summary>
    public bool IsLoaded { get; private set; }

    public async Task LoadAsync()
    {
        try
        {
            _all = await _inventoryService.GetClinicalServicesAsync(includeInactive: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudieron cargar los servicios", ex);
            _all = new List<ClinicalService>();
        }

        var categories = DefaultCategories.Concat(_all.Select(s => s.Category))
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        CategoryFilters = new ObservableCollection<string>(new[] { "Todos" }.Concat(categories));
        if (!CategoryFilters.Contains(CategoryFilter)) CategoryFilter = "Todos";
        ApplyFilter();
        IsLoaded = true;
        OnPropertyChanged(nameof(Summary));
    }

    partial void OnCategoryFilterChanged(string value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnShowInactiveChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        var list = _all.Where(s => ShowInactive || s.IsActive);
        if (!string.IsNullOrWhiteSpace(CategoryFilter) && CategoryFilter != "Todos")
        {
            list = list.Where(s => string.Equals(s.Category, CategoryFilter, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = TextSearch.Normalize(SearchText);
            list = list.Where(s => TextSearch.Matches(q, s.Name, s.Code, s.Category, s.Description));
        }
        FilteredServices = new ObservableCollection<ClinicalService>(list.OrderBy(s => s.Category).ThenBy(s => s.Name));
    }

    // ================================================================== editor (panel lateral)

    [ObservableProperty]
    private bool _isEditorOpen;

    [ObservableProperty]
    private int _editId;

    [ObservableProperty]
    private string _editCode = string.Empty;

    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    private string _editCategory = "Consulta";

    [ObservableProperty]
    private string _editPriceText = string.Empty;

    [ObservableProperty]
    private int _editDuration = 20;

    [ObservableProperty]
    private string _editDescription = string.Empty;

    [ObservableProperty]
    private bool _editIsActive = true;

    [ObservableProperty]
    private ObservableCollection<ServiceDefaultProduct> _editSupplies = new();

    [ObservableProperty]
    private ObservableCollection<Product> _availableProducts = new();

    [ObservableProperty]
    private Product? _selectedProductToAdd;

    [ObservableProperty]
    private int _newSupplyQuantity = 1;

    [ObservableProperty]
    private string? _editorError;

    public bool IsNewService => EditId == 0;
    public bool IsEditingExisting => EditId > 0;
    public string EditorTitle => IsNewService ? "Nuevo servicio" : "Editar servicio";

    partial void OnEditIdChanged(int value)
    {
        OnPropertyChanged(nameof(IsNewService));
        OnPropertyChanged(nameof(IsEditingExisting));
        OnPropertyChanged(nameof(EditorTitle));
    }

    private async Task LoadProductsAsync()
    {
        if (AvailableProducts.Count > 0) return;
        try
        {
            AvailableProducts = new ObservableCollection<Product>((await _inventoryService.GetProductsAsync()).OrderBy(p => p.Name));
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudieron cargar los productos para el servicio", ex);
        }
    }

    [RelayCommand]
    public async Task NewServiceAsync()
    {
        await LoadProductsAsync();
        EditId = 0;
        EditCode = NextCode();
        EditName = string.Empty;
        EditCategory = CategoryFilter != "Todos" ? CategoryFilter : "Consulta";
        EditPriceText = string.Empty;
        EditDuration = 20;
        EditDescription = string.Empty;
        EditIsActive = true;
        EditSupplies = new ObservableCollection<ServiceDefaultProduct>();
        NewSupplyQuantity = 1;
        SelectedProductToAdd = null;
        EditorError = null;
        IsEditorOpen = true;
    }

    [RelayCommand]
    public async Task EditServiceAsync(ClinicalService? service)
    {
        if (service == null) return;
        await LoadProductsAsync();
        EditId = service.Id;
        EditCode = service.Code;
        EditName = service.Name;
        EditCategory = service.Category;
        EditPriceText = service.Price.ToString("0.##", CultureInfo.InvariantCulture);
        EditDuration = service.EstimatedDurationMinutes;
        EditDescription = service.Description;
        EditIsActive = service.IsActive;
        EditSupplies = new ObservableCollection<ServiceDefaultProduct>(service.DefaultProducts.Select(dp => new ServiceDefaultProduct
        {
            ProductId = dp.ProductId,
            Product = AvailableProducts.FirstOrDefault(p => p.Id == dp.ProductId) ?? dp.Product,
            Quantity = dp.Quantity,
            Instructions = dp.Instructions
        }));
        NewSupplyQuantity = 1;
        SelectedProductToAdd = null;
        EditorError = null;
        IsEditorOpen = true;
    }

    private string NextCode()
    {
        int max = _all.Select(s => s.Code)
            .Select(c => c.StartsWith("SERV-", StringComparison.OrdinalIgnoreCase) && int.TryParse(c[5..], out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();
        return $"SERV-{max + 1:D3}";
    }

    [RelayCommand]
    public void CloseEditor()
    {
        IsEditorOpen = false;
        EditorError = null;
    }

    [RelayCommand]
    public void AddSupply()
    {
        if (SelectedProductToAdd == null) return;
        var existing = EditSupplies.FirstOrDefault(s => s.ProductId == SelectedProductToAdd.Id);
        if (existing != null)
        {
            existing.Quantity += Math.Max(1, NewSupplyQuantity);
            EditSupplies = new ObservableCollection<ServiceDefaultProduct>(EditSupplies);
        }
        else
        {
            EditSupplies.Add(new ServiceDefaultProduct
            {
                ProductId = SelectedProductToAdd.Id,
                Product = SelectedProductToAdd,
                Quantity = Math.Max(1, NewSupplyQuantity)
            });
        }
        NewSupplyQuantity = 1;
        SelectedProductToAdd = null;
    }

    [RelayCommand]
    public void RemoveSupply(ServiceDefaultProduct? supply)
    {
        if (supply != null) EditSupplies.Remove(supply);
    }

    public static decimal? ParsePrice(string? text)
    {
        var clean = (text ?? string.Empty).Replace("$", string.Empty).Replace(",", string.Empty).Trim();
        return decimal.TryParse(clean, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) && price >= 0 ? Math.Round(price, 2) : null;
    }

    [RelayCommand]
    public async Task SaveServiceAsync()
    {
        if (string.IsNullOrWhiteSpace(EditName))
        {
            EditorError = "Escriba el nombre del servicio.";
            return;
        }
        var price = ParsePrice(EditPriceText);
        if (price == null)
        {
            EditorError = "Escriba el precio como número (por ejemplo 250 o 250.50).";
            return;
        }
        var code = string.IsNullOrWhiteSpace(EditCode) ? NextCode() : EditCode.Trim().ToUpperInvariant();
        // Solo un alta o un código que cambió, contra los servicios activos (regla «Catálogo con varios equipos», revisión 19):
        // un servicio que quedó con el mismo código que otro de la tablet se sigue pudiendo editar sin cambiarle el código
        var stored = _all.FirstOrDefault(s => s.Id == EditId && EditId != 0);
        bool codeChanged = stored == null || !string.Equals(stored.Code?.Trim(), code, StringComparison.OrdinalIgnoreCase);
        if (codeChanged && _all.Any(s => s.Id != EditId && s.IsActive && string.Equals(s.Code?.Trim(), code, StringComparison.OrdinalIgnoreCase)))
        {
            EditorError = $"El código {code} ya lo usa otro servicio.";
            return;
        }

        var service = new ClinicalService
        {
            Id = EditId,
            Code = code,
            Name = EditName.Trim(),
            Category = string.IsNullOrWhiteSpace(EditCategory) ? "Consulta" : EditCategory.Trim(),
            Price = price.Value,
            EstimatedDurationMinutes = Math.Max(0, EditDuration),
            Description = EditDescription?.Trim() ?? string.Empty,
            IsActive = EditIsActive
        };

        try
        {
            var saved = await _inventoryService.SaveClinicalServiceAsync(service, EditSupplies.Select(s => (s.ProductId, s.Quantity, s.Instructions)).ToList());
            IsEditorOpen = false;
            EditorError = null;
            await LoadAsync();
            _notifications?.Success($"Servicio guardado: {saved.Name} · {saved.Price.ToString("C2", Mexican)}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al guardar servicio", ex);
            EditorError = $"No se pudo guardar: {ex.Message}";
        }
    }

    /// <summary>Desactivar quita el servicio del POS y de la consulta; los tickets que lo tienen no cambian.</summary>
    [RelayCommand]
    public async Task ToggleActiveAsync(ClinicalService? service)
    {
        if (service == null) return;
        var updated = new ClinicalService
        {
            Id = service.Id,
            Code = service.Code,
            Name = service.Name,
            Category = service.Category,
            Price = service.Price,
            EstimatedDurationMinutes = service.EstimatedDurationMinutes,
            Description = service.Description,
            IsActive = !service.IsActive
        };
        try
        {
            await _inventoryService.SaveClinicalServiceAsync(updated, service.DefaultProducts.Select(dp => (dp.ProductId, dp.Quantity, dp.Instructions)).ToList());
            await LoadAsync();
            _notifications?.Info(updated.IsActive ? $"{service.Name} vuelve a estar disponible" : $"{service.Name} ya no se ofrece al cobrar");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al activar/desactivar servicio", ex);
            _notifications?.Warning($"No se pudo cambiar el servicio: {ex.Message}");
        }
    }

    /// <summary>Elimina definitivamente un servicio del catálogo.</summary>
    [RelayCommand]
    public async Task DeleteServiceAsync(ClinicalService? service)
    {
        if (service == null) return;
        if (!DialogService.Confirm(
                $"¿Eliminar definitivamente el servicio «{service.Name}» ({service.Code})?\n\nLos tickets ya cobrados se conservan.",
                "Eliminar servicio"))
        {
            return;
        }

        try
        {
            await _inventoryService.DeleteClinicalServiceAsync(service.Id);
            if (IsEditorOpen && EditId == service.Id)
            {
                IsEditorOpen = false;
                EditorError = null;
            }
            await LoadAsync();
            _notifications?.Info($"Servicio eliminado: {service.Name}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al eliminar servicio", ex);
            _notifications?.Warning($"No se pudo eliminar el servicio: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task DeleteEditingServiceAsync()
    {
        if (EditId <= 0) return;
        var current = _all.FirstOrDefault(s => s.Id == EditId) ?? new ClinicalService { Id = EditId, Code = EditCode, Name = EditName };
        await DeleteServiceAsync(current);
    }
}
