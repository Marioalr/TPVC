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

public partial class CartItem : ObservableObject
{
    public int ProductId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? GenericName { get; set; }
    public string? Presentation { get; set; }
    public string Unit { get; set; } = "Piezas";
    public int AvailableStock { get; set; }
    public decimal UnitPrice { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtotal))]
    private int _quantity = 1;

    public decimal Subtotal => Quantity * UnitPrice;
}

public partial class PosViewModel : ObservableObject
{
    private readonly InventoryService _inventoryService;
    public Action? OnBackToHubRequested { get; set; }

    [RelayCommand]
    public void BackToHub() => OnBackToHubRequested?.Invoke();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private Category? _selectedCategory;

    [ObservableProperty]
    private ObservableCollection<Category> _categories = new();

    [ObservableProperty]
    private ObservableCollection<Product> _products = new();

    [ObservableProperty]
    private ObservableCollection<Product> _filteredProducts = new();

    [ObservableProperty]
    private ObservableCollection<CartItem> _cart = new();

    [ObservableProperty]
    private int _totalCartItems = 0;

    [ObservableProperty]
    private decimal _totalCartAmount = 0m;

    [ObservableProperty]
    private string _patientName = "Público General";

    [ObservableProperty]
    private string _selectedPaymentMethod = "Efectivo";

    public List<string> PaymentMethods { get; } = new()
    {
        "Efectivo",
        "Tarjeta Débito/Crédito",
        "Transferencia"
    };

    [ObservableProperty]
    private string? _alertMessage;

    [ObservableProperty]
    private bool _isAlertVisible;

    [ObservableProperty]
    private bool _isSuccessAlert;

    public PosViewModel(InventoryService inventoryService)
    {
        _inventoryService = inventoryService;
        _inventoryService.OnInventoryChanged += OnInventoryChangedHandler;
    }

    private void OnInventoryChangedHandler()
    {
        _ = LoadCatalogAsync();
    }

    public async Task InitializeAsync()
    {
        await LoadCatalogAsync();
    }

    public async Task LoadCatalogAsync()
    {
        var cats = await _inventoryService.GetCategoriesAsync();
        var allCat = new Category { Id = 0, Name = "Todas las categorías" };
        var catList = new List<Category> { allCat };
        catList.AddRange(cats);

        Categories = new ObservableCollection<Category>(catList);
        if (SelectedCategory == null || !Categories.Any(c => c.Id == SelectedCategory.Id))
        {
            SelectedCategory = allCat;
        }

        var prods = await _inventoryService.GetProductsAsync();
        Products = new ObservableCollection<Product>(prods);
        ApplyFilters();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilters();
    partial void OnSelectedCategoryChanged(Category? value) => ApplyFilters();

    private void ApplyFilters()
    {
        var list = Products.AsEnumerable();

        if (SelectedCategory != null && SelectedCategory.Id > 0)
        {
            list = list.Where(p => p.CategoryId == SelectedCategory.Id);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.Trim().ToLowerInvariant();
            list = list.Where(p => 
                p.Name.ToLowerInvariant().Contains(q) ||
                (p.GenericName != null && p.GenericName.ToLowerInvariant().Contains(q)) ||
                p.Code.ToLowerInvariant().Contains(q) ||
                (p.Location != null && p.Location.ToLowerInvariant().Contains(q))
            );
        }

        FilteredProducts = new ObservableCollection<Product>(list.OrderBy(p => p.Name));
    }

    [RelayCommand]
    public void AddToCart(Product product)
    {
        if (product.CurrentStock <= 0)
        {
            ShowAlert($"El producto '{product.Name}' no tiene existencias disponibles.", false);
            return;
        }

        var existing = Cart.FirstOrDefault(c => c.ProductId == product.Id);
        if (existing != null)
        {
            if (existing.Quantity < product.CurrentStock)
            {
                existing.Quantity++;
                RecalculateTotals();
                ShowAlert($"Se aumentó la cantidad de '{product.Name}' en el carrito.", true);
            }
            else
            {
                ShowAlert($"No hay más existencias disponibles de '{product.Name}' (Stock actual: {product.CurrentStock}).", false);
            }
        }
        else
        {
            Cart.Add(new CartItem
            {
                ProductId = product.Id,
                Code = product.Code,
                Name = product.Name,
                GenericName = product.GenericName,
                Presentation = product.Presentation,
                Unit = product.Unit,
                AvailableStock = product.CurrentStock,
                UnitPrice = product.Price,
                Quantity = 1
            });
            RecalculateTotals();
            ShowAlert($"'{product.Name}' agregado al carrito.", true);
        }
    }

    [RelayCommand]
    public void IncreaseQuantity(CartItem item)
    {
        if (item.Quantity < item.AvailableStock)
        {
            item.Quantity++;
            RecalculateTotals();
        }
        else
        {
            ShowAlert($"Stock máximo alcanzado para '{item.Name}' ({item.AvailableStock} disponibles).", false);
        }
    }

    [RelayCommand]
    public void DecreaseQuantity(CartItem item)
    {
        if (item.Quantity > 1)
        {
            item.Quantity--;
            RecalculateTotals();
        }
        else
        {
            RemoveFromCart(item);
        }
    }

    [RelayCommand]
    public void RemoveFromCart(CartItem item)
    {
        Cart.Remove(item);
        RecalculateTotals();
        ShowAlert($"'{item.Name}' removido del carrito.", true);
    }

    [RelayCommand]
    public void ClearCart()
    {
        Cart.Clear();
        RecalculateTotals();
    }

    private void RecalculateTotals()
    {
        TotalCartItems = Cart.Sum(c => c.Quantity);
        TotalCartAmount = Cart.Sum(c => c.Subtotal);
    }

    [RelayCommand]
    public async Task ProcessSaleAsync()
    {
        if (Cart.Count == 0)
        {
            ShowAlert("El carrito de venta está vacío.", false);
            return;
        }

        try
        {
            var saleItems = Cart.Select(c => (c.ProductId, c.Quantity, c.UnitPrice)).ToList();
            var total = TotalCartAmount;
            var patient = string.IsNullOrWhiteSpace(PatientName) ? "Público General" : PatientName.Trim();

            await _inventoryService.RegisterSaleAsync(saleItems, patient, SelectedPaymentMethod);

            Cart.Clear();
            RecalculateTotals();
            PatientName = "Público General";

            ShowAlert($"¡Venta procesada con éxito! Total cobrado: ${total:N2} ({SelectedPaymentMethod})", true);
        }
        catch (Exception ex)
        {
            ShowAlert($"Error al procesar la venta: {ex.Message}", false);
        }
    }

    public void AddPrescriptionItem(string medicineName, int qty = 1)
    {
        var product = Products.FirstOrDefault(p => 
            p.Name.Contains(medicineName, StringComparison.OrdinalIgnoreCase) || 
            (p.GenericName != null && p.GenericName.Contains(medicineName, StringComparison.OrdinalIgnoreCase)));

        if (product != null)
        {
            AddToCart(product);
        }
    }

    private void ShowAlert(string message, bool isSuccess)
    {
        AlertMessage = message;
        IsSuccessAlert = isSuccess;
        IsAlertVisible = true;
    }

    [RelayCommand]
    public void DismissAlert()
    {
        IsAlertVisible = false;
        AlertMessage = null;
    }
}
