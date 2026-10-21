using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

public partial class ProductDialogViewModel : ObservableObject
{
    private readonly InventoryService _inventoryService;
    private readonly Action? _onClose;

    [ObservableProperty]
    private bool _isNew = true;

    [ObservableProperty]
    private int _id;

    [ObservableProperty]
    private string _code = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _genericName = string.Empty;

    [ObservableProperty]
    private string _presentation = string.Empty;

    [ObservableProperty]
    private string _unit = "Piezas";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfitText), nameof(IndividualUnitPriceSummary))]
    private decimal _price = 0.0m;

    /// <summary>Costo por unidad (lo que se paga al proveedor).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfitText))]
    private decimal _unitCost;

    /// <summary>El costo no incluye IVA: se le suma 16 %. Desmarcada por omisión (los medicamentos van sin IVA).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfitText))]
    private bool _costExcludesVat;

    [ObservableProperty]
    private int _currentStock = 0;

    /// <summary>
    /// Paso 8.19: la existencia que mostró el diálogo al abrirse. Si el campo no cambió, guardar no toca la existencia (una
    /// venta que bajó mientras tanto no se revierte); null en un producto nuevo.
    /// </summary>
    public int? OriginalStock { get; private set; }

    [ObservableProperty]
    private int _minStock = 5;

    [ObservableProperty]
    private int _optimalStock = 20;

    /// <summary>
    /// Cuántos individuales contiene el paquete (ampolletas, piezas, etc.). Si no se ingresa, se asume 1 sola pieza.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveUnitsPerPackage), nameof(IndividualUnitPriceSummary))]
    private string _unitsPerPackageText = string.Empty;

    public int EffectiveUnitsPerPackage =>
        int.TryParse(UnitsPerPackageText?.Trim(), out int parsed) && parsed > 1 ? parsed : 1;

    public string IndividualUnitPriceSummary =>
        EffectiveUnitsPerPackage > 1 && Price > 0
            ? $"Contiene {EffectiveUnitsPerPackage} piezas individuales · {Math.Round(Price / EffectiveUnitsPerPackage, 2, MidpointRounding.AwayFromZero).ToString("C2", Mexican)} c/u en servicios"
            : EffectiveUnitsPerPackage > 1
                ? $"Contiene {EffectiveUnitsPerPackage} piezas individuales por paquete"
                : "Si se deja vacío se asume 1 sola pieza individual";

    [ObservableProperty]
    private string _location = string.Empty;

    /// <summary>SKU del proveedor: «Ingresar pedido» lo guarda y con él empareja el siguiente pedido.</summary>
    [ObservableProperty]
    private string _supplierCode = string.Empty;

    [ObservableProperty]
    private bool _requiresRefrigeration;

    [ObservableProperty]
    private string _notes = string.Empty;

    [ObservableProperty]
    private ObservableCollection<Category> _categories = new();

    [ObservableProperty]
    private Category? _selectedCategory;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _successMessage = string.Empty;

    private static readonly System.Globalization.CultureInfo Mexican = System.Globalization.CultureInfo.GetCultureInfo("es-MX");

    /// <summary>
    /// "Ganancia: $5.50 por unidad (46 % del precio)" con el costo real (IVA sumado si no lo incluía). Vacío sin costo;
    /// un aviso si el costo es mayor que el precio.
    /// </summary>
    public string ProfitText
    {
        get
        {
            if (UnitCost <= 0) return string.Empty;
            var cost = Product.CostWithVat(UnitCost, CostExcludesVat);
            var costText = CostExcludesVat ? $"Costo con IVA: {cost.ToString("C2", Mexican)} · " : string.Empty;
            if (Price <= 0) return costText + "Sin precio de venta todavía.";
            var profit = Price - cost;
            if (profit < 0) return costText + $"Se vende {(-profit).ToString("C2", Mexican)} abajo del costo.";
            return costText + $"Ganancia: {profit.ToString("C2", Mexican)} por unidad ({Math.Round(profit / Price * 100):0} % del precio).";
        }
    }

    public event Action? RequestClose;

    /// <summary>Se guardó un producto (la lista lo refresca; el alta queda abierta para el siguiente).</summary>
    public event Action? Saved;

    public ObservableCollection<string> CommonUnits { get; } = new()
    {
        "Piezas",
        "Cajas",
        "Frascos",
        "Ampolletas",
        "Tabletas",
        "Sobres",
        "Paquetes",
        "Rollos",
        "Mililitros"
    };

    public ProductDialogViewModel(InventoryService inventoryService, Action? onClose = null)
    {
        _inventoryService = inventoryService;
        _onClose = onClose;
    }

    public async Task InitializeAsync(Product? existing = null)
    {
        var cats = await _inventoryService.GetCategoriesAsync();
        Categories = new ObservableCollection<Category>(cats);
        ErrorMessage = string.Empty;
        SuccessMessage = string.Empty;

        if (existing == null)
        {
            IsNew = true;
            ResetFields();
            SelectedCategory = Categories.FirstOrDefault();
        }
        else
        {
            IsNew = false;
            Id = existing.Id;
            Code = existing.Code;
            Name = existing.Name;
            GenericName = existing.GenericName ?? string.Empty;
            Presentation = existing.Presentation ?? string.Empty;
            Unit = existing.Unit;
            Price = existing.Price;
            UnitCost = existing.UnitCost;
            CostExcludesVat = existing.CostExcludesVat;
            CurrentStock = existing.CurrentStock;
            OriginalStock = existing.CurrentStock;
            MinStock = existing.MinStock;
            OptimalStock = existing.OptimalStock;
            UnitsPerPackageText = existing.UnitsPerPackage > 1 ? existing.UnitsPerPackage.ToString() : string.Empty;
            Location = existing.Location ?? string.Empty;
            SupplierCode = existing.SupplierCode ?? string.Empty;
            RequiresRefrigeration = existing.RequiresRefrigeration;
            Notes = existing.Notes ?? string.Empty;
            SelectedCategory = Categories.FirstOrDefault(c => c.Id == existing.CategoryId);
        }
    }

    public void ResetFields()
    {
        Id = 0;
        Code = string.Empty;
        Name = string.Empty;
        GenericName = string.Empty;
        Presentation = string.Empty;
        Unit = "Piezas";
        Price = 0.0m;
        UnitCost = 0m;
        CostExcludesVat = false;
        CurrentStock = 0;
        OriginalStock = null;
        MinStock = 5;
        OptimalStock = 20;
        UnitsPerPackageText = string.Empty;
        Location = string.Empty;
        SupplierCode = string.Empty;
        RequiresRefrigeration = false;
        Notes = string.Empty;
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        ErrorMessage = string.Empty;
        SuccessMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(Code))
        {
            ErrorMessage = "Debe ingresar un Código o SKU para el insumo.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            ErrorMessage = "El Nombre Comercial del insumo es obligatorio.";
            return;
        }

        if (SelectedCategory == null)
        {
            ErrorMessage = "Debe seleccionar una categoría para el insumo.";
            return;
        }

        if (UnitCost < 0)
        {
            ErrorMessage = "El costo no puede ser negativo.";
            return;
        }

        string trimmedCode = Code.Trim();

        // 1. Validar que no se repita el código en el inventario
        bool isTaken = await _inventoryService.IsProductCodeTakenAsync(trimmedCode, Id);
        if (isTaken)
        {
            ErrorMessage = $"El código '{trimmedCode}' ya está registrado en el inventario. Debe ingresar un código diferente.";
            return;
        }

        try
        {
            string savedName = Name.Trim();

            var product = new Product
            {
                Id = Id,
                Code = trimmedCode,
                Name = savedName,
                GenericName = string.IsNullOrWhiteSpace(GenericName) ? null : GenericName.Trim(),
                Presentation = string.IsNullOrWhiteSpace(Presentation) ? null : Presentation.Trim(),
                CategoryId = SelectedCategory.Id,
                Unit = Unit,
                Price = Price,
                UnitCost = UnitCost,
                CostExcludesVat = CostExcludesVat,
                CurrentStock = CurrentStock,
                MinStock = MinStock,
                OptimalStock = OptimalStock,
                UnitsPerPackage = EffectiveUnitsPerPackage,
                Location = string.IsNullOrWhiteSpace(Location) ? null : Location.Trim(),
                SupplierCode = string.IsNullOrWhiteSpace(SupplierCode) ? null : SupplierCode.Trim(),
                RequiresRefrigeration = RequiresRefrigeration,
                Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim()
            };

            await _inventoryService.SaveProductAsync(product, IsNew ? null : OriginalStock);
            Saved?.Invoke();

            if (IsNew)
            {
                // Limpiar lo agregado para permitir registrar el siguiente insumo sin duplicados
                ResetFields();
                SuccessMessage = $"\"{savedName}\" guardado. Puede registrar el siguiente.";
            }
            else
            {
                RequestClose?.Invoke();
                _onClose?.Invoke();
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error al guardar el insumo: {ex.Message}";
        }
    }

    [RelayCommand]
    public void Cancel()
    {
        RequestClose?.Invoke();
        _onClose?.Invoke();
    }
}
