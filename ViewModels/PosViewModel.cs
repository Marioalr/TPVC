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
    public bool RequiresRefrigeration { get; set; }

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

    // ==========================================
    // BÚSQUEDA EN TIEMPO REAL CON DROPDOWN
    // ==========================================
    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<Product> _searchSuggestions = new();

    [ObservableProperty]
    private bool _isSearchDropdownOpen;

    [ObservableProperty]
    private int _selectedSuggestionIndex = -1;

    [ObservableProperty]
    private Product? _selectedSuggestion;

    partial void OnSelectedSuggestionIndexChanged(int value)
    {
        if (value >= 0 && value < SearchSuggestions.Count)
        {
            SelectedSuggestion = SearchSuggestions[value];
        }
        else
        {
            SelectedSuggestion = null;
        }
    }

    partial void OnSelectedSuggestionChanged(Product? value)
    {
        if (value != null)
        {
            var idx = SearchSuggestions.IndexOf(value);
            if (idx >= 0 && idx != SelectedSuggestionIndex)
            {
                SelectedSuggestionIndex = idx;
            }
        }
    }

    // ==========================================
    // CATÁLOGO GENERAL Y MODAL DE BÚSQUEDA
    // ==========================================
    [ObservableProperty]
    private ObservableCollection<Category> _categories = new();

    [ObservableProperty]
    private ObservableCollection<Product> _products = new();

    [ObservableProperty]
    private bool _isCatalogModalOpen;

    [ObservableProperty]
    private string _catalogSearchText = string.Empty;

    [ObservableProperty]
    private Category? _catalogSelectedCategory;

    [ObservableProperty]
    private ObservableCollection<Product> _catalogFilteredProducts = new();

    // ==========================================
    // VENTA ACTUAL / CARRITO PRINCIPAL
    // ==========================================
    [ObservableProperty]
    private ObservableCollection<CartItem> _cart = new();

    [ObservableProperty]
    private int _totalCartItems = 0;

    [ObservableProperty]
    private decimal _totalCartAmount = 0m;

    [ObservableProperty]
    private bool _hasCartItems;

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

    partial void OnCartChanged(ObservableCollection<CartItem> value)
    {
        value.CollectionChanged += (s, e) => RecalculateTotals();
        RecalculateTotals();
    }

    // ==========================================
    // NOTIFICACIONES Y ALERTAS
    // ==========================================
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
        _cart.CollectionChanged += (s, e) => RecalculateTotals();
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
        if (CatalogSelectedCategory == null || !Categories.Any(c => c.Id == CatalogSelectedCategory.Id))
        {
            CatalogSelectedCategory = allCat;
        }

        var prods = await _inventoryService.GetProductsAsync();
        Products = new ObservableCollection<Product>(prods);

        ApplyCatalogFilters();
        ApplySearchSuggestions(SearchText);
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplySearchSuggestions(value);
    }

    private void ApplySearchSuggestions(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            SearchSuggestions.Clear();
            SelectedSuggestionIndex = -1;
            SelectedSuggestion = null;
            IsSearchDropdownOpen = false;
            return;
        }

        var q = query.Trim().ToLowerInvariant();
        var matches = Products
            .Where(p => 
                p.Name.ToLowerInvariant().Contains(q) ||
                (p.GenericName != null && p.GenericName.ToLowerInvariant().Contains(q)) ||
                p.Code.ToLowerInvariant().Contains(q) ||
                (p.Location != null && p.Location.ToLowerInvariant().Contains(q))
            )
            .OrderBy(p => p.Name)
            .Take(8)
            .ToList();

        SearchSuggestions = new ObservableCollection<Product>(matches);
        IsSearchDropdownOpen = SearchSuggestions.Count > 0;
        SelectedSuggestionIndex = SearchSuggestions.Count > 0 ? 0 : -1;
        SelectedSuggestion = SearchSuggestions.FirstOrDefault();
    }

    [RelayCommand]
    public void MoveSuggestionDown()
    {
        if (SearchSuggestions.Count == 0)
        {
            SelectedSuggestionIndex = -1;
            SelectedSuggestion = null;
            return;
        }

        if (SelectedSuggestionIndex < 0 || SelectedSuggestionIndex >= SearchSuggestions.Count - 1)
        {
            SelectedSuggestionIndex = 0;
        }
        else
        {
            SelectedSuggestionIndex++;
        }

        SelectedSuggestion = SearchSuggestions[SelectedSuggestionIndex];
    }

    [RelayCommand]
    public void MoveSuggestionUp()
    {
        if (SearchSuggestions.Count == 0)
        {
            SelectedSuggestionIndex = -1;
            SelectedSuggestion = null;
            return;
        }

        if (SelectedSuggestionIndex <= 0 || SelectedSuggestionIndex >= SearchSuggestions.Count)
        {
            SelectedSuggestionIndex = SearchSuggestions.Count - 1;
        }
        else
        {
            SelectedSuggestionIndex--;
        }

        SelectedSuggestion = SearchSuggestions[SelectedSuggestionIndex];
    }

    [RelayCommand]
    public void ConfirmSelectedSuggestion()
    {
        if (!IsSearchDropdownOpen || SearchSuggestions.Count == 0)
            return;

        var target = SelectedSuggestion;
        if (target == null && SelectedSuggestionIndex >= 0 && SelectedSuggestionIndex < SearchSuggestions.Count)
        {
            target = SearchSuggestions[SelectedSuggestionIndex];
        }
        target ??= SearchSuggestions.FirstOrDefault();

        if (target != null)
        {
            SelectSuggestion(target);
        }
    }

    [RelayCommand]
    public void SelectSuggestion(Product product)
    {
        AddToCart(product);
        SearchText = string.Empty;
        SearchSuggestions.Clear();
        SelectedSuggestionIndex = -1;
        SelectedSuggestion = null;
        IsSearchDropdownOpen = false;
    }

    [RelayCommand]
    public void AddFirstSuggestion()
    {
        ConfirmSelectedSuggestion();
    }

    [RelayCommand]
    public void CloseSearchDropdown()
    {
        IsSearchDropdownOpen = false;
    }

    // ==========================================
    // MODAL DE CATÁLOGO COMPLETO
    // ==========================================
    [RelayCommand]
    public void OpenCatalogModal()
    {
        CatalogSearchText = string.Empty;
        CatalogSelectedCategory = Categories.FirstOrDefault();
        ApplyCatalogFilters();
        IsCatalogModalOpen = true;
    }

    [RelayCommand]
    public void CloseCatalogModal()
    {
        IsCatalogModalOpen = false;
    }

    partial void OnCatalogSearchTextChanged(string value) => ApplyCatalogFilters();
    partial void OnCatalogSelectedCategoryChanged(Category? value) => ApplyCatalogFilters();

    private void ApplyCatalogFilters()
    {
        var list = Products.AsEnumerable();

        if (CatalogSelectedCategory != null && CatalogSelectedCategory.Id > 0)
        {
            list = list.Where(p => p.CategoryId == CatalogSelectedCategory.Id);
        }

        if (!string.IsNullOrWhiteSpace(CatalogSearchText))
        {
            var q = CatalogSearchText.Trim().ToLowerInvariant();
            list = list.Where(p => 
                p.Name.ToLowerInvariant().Contains(q) ||
                (p.GenericName != null && p.GenericName.ToLowerInvariant().Contains(q)) ||
                p.Code.ToLowerInvariant().Contains(q) ||
                (p.Location != null && p.Location.ToLowerInvariant().Contains(q))
            );
        }

        CatalogFilteredProducts = new ObservableCollection<Product>(list.OrderBy(p => p.Name));
    }

    // ==========================================
    // OPERACIONES DE LA VENTA / CARRITO
    // ==========================================
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
                ShowAlert($"Se aumentó la cantidad de '{product.Name}' en la venta.", true);
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
                RequiresRefrigeration = product.RequiresRefrigeration,
                Quantity = 1
            });
            RecalculateTotals();
            ShowAlert($"'{product.Name}' agregado a la venta.", true);
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
        ShowAlert($"'{item.Name}' removido de la venta.", true);
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
        HasCartItems = Cart.Count > 0;
    }


    [RelayCommand]
    public async Task ProcessSaleAsync()
    {
        if (Cart.Count == 0)
        {
            ShowAlert("La venta no contiene ningún producto.", false);
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
