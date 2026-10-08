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
    private decimal _price = 0.0m;

    [ObservableProperty]
    private int _currentStock = 0;

    [ObservableProperty]
    private int _minStock = 5;

    [ObservableProperty]
    private int _optimalStock = 20;

    [ObservableProperty]
    private string _location = string.Empty;

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

    [ObservableProperty]
    private bool _isSaving;

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

    public bool DialogResult { get; private set; }

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
            CurrentStock = existing.CurrentStock;
            MinStock = existing.MinStock;
            OptimalStock = existing.OptimalStock;
            Location = existing.Location ?? string.Empty;
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
        CurrentStock = 0;
        MinStock = 5;
        OptimalStock = 20;
        Location = string.Empty;
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

        string trimmedCode = Code.Trim();

        // 1. Validar que no se repita el código en el inventario
        bool isTaken = await _inventoryService.IsProductCodeTakenAsync(trimmedCode, Id);
        if (isTaken)
        {
            ErrorMessage = $"El código '{trimmedCode}' ya está registrado en el inventario. Debe ingresar un código diferente.";
            return;
        }

        IsSaving = true;
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
                CurrentStock = CurrentStock,
                MinStock = MinStock,
                OptimalStock = OptimalStock,
                Location = string.IsNullOrWhiteSpace(Location) ? null : Location.Trim(),
                RequiresRefrigeration = RequiresRefrigeration,
                Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim()
            };

            await _inventoryService.SaveProductAsync(product);
            DialogResult = true;
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
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    public void Cancel()
    {
        RequestClose?.Invoke();
        _onClose?.Invoke();
    }
}
