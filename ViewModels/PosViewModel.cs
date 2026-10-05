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
    public bool IsService { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtotal))]
    private int _quantity = 1;

    public decimal Subtotal => Quantity * UnitPrice;
}

public partial class PosViewModel : ObservableObject, IDisposable
{
    private readonly InventoryService _inventoryService;
    private readonly ConsultationService _consultationService;
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
    // CATÁLOGO DE SERVICIOS CLÍNICOS E INSUMOS (POS)
    // ==========================================
    [ObservableProperty]
    private ObservableCollection<ClinicalService> _services = new();

    [ObservableProperty]
    private ObservableCollection<ClinicalService> _filteredServices = new();

    [ObservableProperty]
    private ClinicalService? _selectedService;

    [ObservableProperty]
    private bool _isServicesModalOpen;

    [ObservableProperty]
    private string _serviceCategoryFilter = "Todos";

    [ObservableProperty]
    private string _serviceSearchText = string.Empty;

    public List<string> ServiceCategories { get; } = new()
    {
        "Todos",
        "Consulta",
        "Curaciones",
        "Procedimientos",
        "Diagnóstico",
        "Certificados"
    };

    // Modal de creación de servicio desde POS
    [ObservableProperty]
    private bool _isCreateServiceModalOpen;

    [ObservableProperty]
    private string _newServiceCode = string.Empty;

    [ObservableProperty]
    private string _newServiceName = string.Empty;

    [ObservableProperty]
    private string _newServiceDescription = string.Empty;

    [ObservableProperty]
    private string _newServiceCategory = "Consulta";

    [ObservableProperty]
    private decimal _newServicePrice = 300.00m;

    [ObservableProperty]
    private int _newServiceDuration = 20;

    [ObservableProperty]
    private ObservableCollection<Product> _availableProductsForService = new();

    [ObservableProperty]
    private Product? _selectedProductForService;

    [ObservableProperty]
    private int _newProductQuantity = 1;

    [ObservableProperty]
    private string _newProductInstructions = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ServiceDefaultProduct> _newServiceDefaultProducts = new();

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

    [ObservableProperty]
    private decimal _cashReceived = 0m;

    public decimal ChangeDue => Math.Max(0m, CashReceived - TotalCartAmount);

    public bool HasChange => CashReceived >= TotalCartAmount && TotalCartAmount > 0;

    partial void OnCashReceivedChanged(decimal value)
    {
        OnPropertyChanged(nameof(ChangeDue));
        OnPropertyChanged(nameof(HasChange));
    }

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
    // COMPROBANTE / TICKET DE VENTA SIMULADO
    // ==========================================
    [ObservableProperty]
    private SaleReceipt? _currentReceipt;

    [ObservableProperty]
    private bool _isTicketModalOpen;

    [ObservableProperty]
    private string? _ticketStatusMessage;

    [ObservableProperty]
    private bool _isTicketPrinted;

    // ==========================================
    // NOTIFICACIONES Y ALERTAS
    // ==========================================
    [ObservableProperty]
    private string? _alertMessage;

    [ObservableProperty]
    private bool _isAlertVisible;

    [ObservableProperty]
    private bool _isSuccessAlert;

    // ==========================================
    // ESTADO DE CONSULTA MÉDICA ACTIVA
    // ==========================================
    public bool HasActiveConsultation => _consultationService.HasActiveConsultation;
    public ActiveConsultation? ActiveConsultation => _consultationService.CurrentConsultation;
    public string ActiveConsultationFolio => _consultationService.CurrentConsultation?.Folio ?? string.Empty;
    public string ActiveConsultationPatientName => _consultationService.CurrentConsultation?.PatientName ?? string.Empty;
    public int ActiveConsultationItemsCount => _consultationService.CurrentConsultation?.TotalItems ?? 0;
    public decimal ActiveConsultationTotalAmount => _consultationService.CurrentConsultation?.TotalAmount ?? 0m;

    [ObservableProperty]
    private string _activeConsultationElapsedTime = "00:00";

    private System.Threading.Timer? _consultationTimer;

    [ObservableProperty]
    private bool _isSelectPatientModalOpen;

    [ObservableProperty]
    private ObservableCollection<PatientRecord> _availablePatientsForConsultation = new();

    [ObservableProperty]
    private PatientRecord? _selectedPatientForConsultation;

    [ObservableProperty]
    private string _newConsultationChiefComplaint = "Consulta Médica General";

    // ==========================================
    // MODO MODAL EMBEBIDO (DESDE EXPEDIENTES CLÍNICOS)
    // ==========================================
    [ObservableProperty]
    private bool _isEmbeddedInModal;

    [ObservableProperty]
    private bool _isConsultationCheckout;

    public Action? OnCloseModalRequested { get; set; }
    public Action? OnSaleCompletedFromConsultation { get; set; }

    [RelayCommand]
    public void CloseModal()
    {
        OnCloseModalRequested?.Invoke();
    }

    public PosViewModel(InventoryService inventoryService, ConsultationService? consultationService = null)
    {
        _inventoryService = inventoryService;
        _consultationService = consultationService ?? new ConsultationService();
        _inventoryService.OnInventoryChanged += OnInventoryChangedHandler;
        _consultationService.OnActiveConsultationChanged += SyncActiveConsultation;
        _cart.CollectionChanged += (s, e) => RecalculateTotals();

        AvailablePatientsForConsultation = new ObservableCollection<PatientRecord>(_consultationService.Patients);
        SelectedPatientForConsultation = AvailablePatientsForConsultation.FirstOrDefault();
        SyncActiveConsultation();

        _ = LoadServicesAsync();
    }

    public void SyncActiveConsultation()
    {
        OnPropertyChanged(nameof(HasActiveConsultation));
        OnPropertyChanged(nameof(ActiveConsultation));
        OnPropertyChanged(nameof(ActiveConsultationFolio));
        OnPropertyChanged(nameof(ActiveConsultationPatientName));
        OnPropertyChanged(nameof(ActiveConsultationItemsCount));
        OnPropertyChanged(nameof(ActiveConsultationTotalAmount));

        if (HasActiveConsultation)
        {
            StartConsultationTimer();
        }
        else
        {
            StopConsultationTimer();
        }
    }

    private void StartConsultationTimer()
    {
        _consultationTimer?.Dispose();
        UpdateActiveConsultationTimer();
        _consultationTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
                {
                    app.Dispatcher.InvokeAsync(() => UpdateActiveConsultationTimer());
                }
                else
                {
                    UpdateActiveConsultationTimer();
                }
            }
            catch
            {
                // Ignored
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void StopConsultationTimer()
    {
        _consultationTimer?.Dispose();
        _consultationTimer = null;
        ActiveConsultationElapsedTime = "00:00";
    }

    public void UpdateActiveConsultationTimer(DateTime? asOf = null)
    {
        if (_consultationService?.CurrentConsultation != null)
        {
            ActiveConsultationElapsedTime = _consultationService.CurrentConsultation.GetFormattedElapsedTime(asOf);
        }
        else
        {
            ActiveConsultationElapsedTime = "00:00";
        }
    }

    public void Dispose()
    {
        _inventoryService.OnInventoryChanged -= OnInventoryChangedHandler;
        _consultationService.OnActiveConsultationChanged -= SyncActiveConsultation;
        StopConsultationTimer();
        GC.SuppressFinalize(this);
    }

    private void OnInventoryChangedHandler()
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.InvokeAsync(async () =>
                {
                    await LoadCatalogAsync();
                    await LoadServicesAsync();
                });
                return;
            }
        }
        catch
        {
            // Fallback para entornos donde WPF Application/Dispatcher no está disponible (ej. Linux tests)
        }

        _ = LoadCatalogAsync();
        _ = LoadServicesAsync();
    }

    public async Task InitializeAsync()
    {
        await LoadCatalogAsync();
        await LoadServicesAsync();
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

        var q = TextSearch.Normalize(query);
        var matches = Products
            .Where(p => TextSearch.Matches(q, p.Name, p.GenericName, p.Code, p.Location))
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
            var q = TextSearch.Normalize(CatalogSearchText);
            list = list.Where(p => TextSearch.Matches(q, p.Name, p.GenericName, p.Code, p.Location));
        }

        CatalogFilteredProducts = new ObservableCollection<Product>(list.OrderBy(p => p.Name));
    }

    // ==========================================
    // OPERACIONES DE LA VENTA / CARRITO
    // ==========================================
    [RelayCommand]
    public void AddToCart(Product product)
    {
        AddToCartWithQuantity(product, 1);
    }

    public void AddToCartWithQuantity(Product product, int quantity)
    {
        if (product.CurrentStock <= 0)
        {
            ShowAlert($"El producto '{product.Name}' no tiene existencias disponibles.", false);
            return;
        }

        int requestedQty = Math.Max(1, quantity);
        var existing = Cart.FirstOrDefault(c => c.ProductId == product.Id);
        if (existing != null)
        {
            int allowed = Math.Min(existing.Quantity + requestedQty, product.CurrentStock);
            if (allowed > existing.Quantity)
            {
                int added = allowed - existing.Quantity;
                existing.Quantity = allowed;
                RecalculateTotals();
                ShowAlert($"Se aumentó la cantidad de '{product.Name}' en la venta (+{added}, total: {allowed}).", true);
            }
            else
            {
                ShowAlert($"No hay más existencias disponibles de '{product.Name}' (Stock actual: {product.CurrentStock}).", false);
            }
        }
        else
        {
            int initialQty = Math.Min(requestedQty, product.CurrentStock);
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
                Quantity = initialQty
            });
            RecalculateTotals();
            ShowAlert($"'{product.Name}' ({initialQty} {product.Unit}) agregado a la venta.", true);
        }
    }

    [RelayCommand]
    public void IncreaseQuantity(CartItem item)
    {
        if (item.IsService || item.Quantity < item.AvailableStock)
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

    private List<CartItem>? _lastClearedCart;

    [ObservableProperty]
    private bool _canUndoClearCart;

    [RelayCommand]
    public void ClearCart()
    {
        if (Cart.Count == 0) return;
        _lastClearedCart = Cart.ToList();
        CanUndoClearCart = true;
        Cart.Clear();
        RecalculateTotals();
        ShowAlert("Se vació la venta. Puede presionar 'Deshacer' para restaurarla.", true);
    }

    [RelayCommand]
    public void UndoClearCart()
    {
        if (_lastClearedCart == null || _lastClearedCart.Count == 0) return;
        foreach (var item in _lastClearedCart)
        {
            Cart.Add(item);
        }
        _lastClearedCart = null;
        CanUndoClearCart = false;
        RecalculateTotals();
        ShowAlert("Se restauraron los productos de la venta.", true);
    }

    public void RecalculateTotals()
    {
        TotalCartItems = Cart.Sum(c => c.Quantity);
        TotalCartAmount = Cart.Sum(c => c.Subtotal);
        HasCartItems = Cart.Count > 0;
        OnPropertyChanged(nameof(ChangeDue));
        OnPropertyChanged(nameof(HasChange));
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
            var saleLines = Cart
                .Select(c => new SaleLineRequest(c.ProductId, c.Quantity, c.UnitPrice, c.Code, c.Name))
                .ToList();
            var patient = string.IsNullOrWhiteSpace(PatientName) ? "Público General" : PatientName.Trim();

            // Se registra primero: si falta stock no se genera ticket ni se vacía el carrito
            var sale = await _inventoryService.RegisterSaleAsync(saleLines, patient, SelectedPaymentMethod, "Dr. Titular / Consultorio TPVC");

            // Construir el recibo / ticket con los datos exactos cobrados y el folio persistido
            var receipt = new SaleReceipt
            {
                Folio = sale?.Folio ?? $"TKT-{DateTime.Now:yyyyMMdd-HHmmss}",
                Date = sale?.Date ?? DateTime.Now,
                PatientName = patient,
                PaymentMethod = SelectedPaymentMethod,
                Cashier = "Dr. Titular / Consultorio TPVC",
                Items = Cart.Select(c => new SaleReceiptItem
                {
                    ProductId = c.ProductId,
                    Name = c.Name,
                    GenericName = c.GenericName,
                    Presentation = c.Presentation,
                    Quantity = c.Quantity,
                    UnitPrice = c.UnitPrice
                }).ToList()
            };

            bool isFromConsultation = IsEmbeddedInModal || IsConsultationCheckout;

            if (isFromConsultation)
            {
                IsConsultationCheckout = false;
                IsEmbeddedInModal = false;
                IsTicketModalOpen = false;
                CurrentReceipt = receipt;
                Cart.Clear();
                RecalculateTotals();
                PatientName = "Público General";

                OnSaleCompletedFromConsultation?.Invoke();
            }
            else
            {
                // Presentar el ticket simulado en pantalla para ventas directas de farmacia
                CurrentReceipt = receipt;
                IsTicketModalOpen = true;
                IsTicketPrinted = false;
                TicketStatusMessage = null;

                Cart.Clear();
                RecalculateTotals();
                PatientName = "Público General";

                ShowAlert($"¡Venta procesada con éxito! Comprobante generado: {receipt.Folio}", true);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al procesar la venta", ex);
            ShowAlert($"Error al procesar la venta: {ex.Message}", false);
            // Refrescar existencias para que el carrito muestre el stock real
            _ = LoadCatalogAsync();
        }
    }

    [RelayCommand]
    public void CloseTicketModal()
    {
        IsTicketModalOpen = false;
        CurrentReceipt = null;
        TicketStatusMessage = null;
        IsTicketPrinted = false;
    }

    [RelayCommand]
    public void PrintTicket()
    {
        IsTicketPrinted = true;
        TicketStatusMessage = "¡Ticket enviado a la impresora térmica correctamente!";
    }

    public void AddPrescriptionItem(string medicineName, int qty = 1)
    {
        if (string.IsNullOrWhiteSpace(medicineName)) return;
        var cleanMed = medicineName.Trim();

        var product = Products.FirstOrDefault(p => 
            p.Name.Contains(cleanMed, StringComparison.OrdinalIgnoreCase) || 
            cleanMed.Contains(p.Name, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(p.GenericName) && (p.GenericName.Contains(cleanMed, StringComparison.OrdinalIgnoreCase) || cleanMed.Contains(p.GenericName, StringComparison.OrdinalIgnoreCase))));

        if (product != null)
        {
            AddToCartWithQuantity(product, Math.Max(1, qty));
        }
    }

    // ==========================================
    // INTEGRACIÓN CON CONSULTA MÉDICA ACTIVA
    // ==========================================
    [RelayCommand]
    public void AddToActiveConsultation()
    {
        if (Cart.Count == 0)
        {
            ShowAlert("La venta no contiene ningún producto para agregar a la consulta activa.", false);
            return;
        }

        if (!_consultationService.HasActiveConsultation)
        {
            if (IsEmbeddedInModal)
            {
                ShowAlert("Solo se puede agregar insumos si existe una consulta médica activa.", false);
                return;
            }

            AvailablePatientsForConsultation = new ObservableCollection<PatientRecord>(_consultationService.Patients);
            if (SelectedPatientForConsultation == null || !AvailablePatientsForConsultation.Contains(SelectedPatientForConsultation))
            {
                SelectedPatientForConsultation = AvailablePatientsForConsultation.FirstOrDefault();
            }
            NewConsultationChiefComplaint = "Consulta Médica General";
            IsSelectPatientModalOpen = true;
            ShowAlert("Debe haber una consulta activa para agregar insumos. Seleccione el paciente para iniciarla.", false);
            return;
        }

        ExecuteAddCartToActiveConsultation();
    }

    private void ExecuteAddCartToActiveConsultation()
    {
        if (!_consultationService.HasActiveConsultation) return;

        int totalAdded = 0;
        foreach (var item in Cart)
        {
            _consultationService.AddCartItemToActiveConsultation(
                item.ProductId,
                item.Code,
                item.Name,
                item.GenericName,
                item.Presentation,
                item.Unit,
                item.UnitPrice,
                item.Quantity
            );
            totalAdded += item.Quantity;
        }

        var folio = _consultationService.CurrentConsultation?.Folio;
        var patientName = _consultationService.CurrentConsultation?.PatientName;

        Cart.Clear();
        RecalculateTotals();
        SyncActiveConsultation();

        ShowAlert($"¡Se agregaron {totalAdded} insumo(s) a la consulta médica de {patientName} ({folio})! Quedan guardados para liquidarse al terminar.", true);
    }

    [RelayCommand]
    public void OpenSelectPatientModal()
    {
        AvailablePatientsForConsultation = new ObservableCollection<PatientRecord>(_consultationService.Patients);
        SelectedPatientForConsultation = AvailablePatientsForConsultation.FirstOrDefault();
        IsSelectPatientModalOpen = true;
    }

    [RelayCommand]
    public void CloseSelectPatientModal()
    {
        IsSelectPatientModalOpen = false;
    }

    [RelayCommand]
    public void ConfirmStartConsultationAndAddItems()
    {
        if (SelectedPatientForConsultation == null)
        {
            ShowAlert("Seleccione un paciente de la lista para iniciar la consulta.", false);
            return;
        }

        _consultationService.StartConsultation(SelectedPatientForConsultation, NewConsultationChiefComplaint);
        IsSelectPatientModalOpen = false;

        ExecuteAddCartToActiveConsultation();
    }

    [RelayCommand]
    public void CheckoutActiveConsultation()
    {
        if (!_consultationService.HasActiveConsultation || _consultationService.CurrentConsultation == null)
        {
            ShowAlert("No hay ninguna consulta médica activa para cobrar.", false);
            return;
        }

        IsConsultationCheckout = true;
        var consult = _consultationService.CurrentConsultation;
        if (consult.Items.Count == 0)
        {
            ShowAlert($"La consulta activa de {consult.PatientName} no tiene insumos guardados.", false);
            return;
        }

        PatientName = consult.PatientName;
        foreach (var cItem in consult.Items)
        {
            var prod = Products.FirstOrDefault(p => p.Id == cItem.ProductId);
            var existing = Cart.FirstOrDefault(c => c.ProductId == cItem.ProductId);
            if (existing != null)
            {
                existing.Quantity += cItem.Quantity;
            }
            else
            {
                Cart.Add(new CartItem
                {
                    ProductId = cItem.ProductId,
                    Code = cItem.Code,
                    Name = cItem.Name,
                    GenericName = cItem.GenericName,
                    Presentation = cItem.Presentation,
                    Unit = cItem.Unit,
                    AvailableStock = prod?.CurrentStock ?? 999,
                    UnitPrice = cItem.UnitPrice,
                    Quantity = cItem.Quantity,
                    RequiresRefrigeration = prod?.RequiresRefrigeration ?? false,
                    IsService = cItem.ProductId <= 0
                });
            }
        }

        _consultationService.CloseActiveConsultation();
        RecalculateTotals();
        SyncActiveConsultation();
        ShowAlert($"Insumos de la consulta de {consult.PatientName} transferidos al carrito. Listos para cobro y ticket.", true);
    }

    [RelayCommand]
    public void CancelActiveConsultation()
    {
        if (_consultationService.HasActiveConsultation)
        {
            var patient = _consultationService.CurrentConsultation?.PatientName;
            _consultationService.CloseActiveConsultation();
            SyncActiveConsultation();
            ShowAlert($"La consulta médica activa de {patient} ha sido finalizada/cerrada.", true);
        }
    }

    // ==========================================
    // OPERACIONES DE SERVICIOS CLÍNICOS E INSUMOS
    // ==========================================
    [RelayCommand]
    public async Task OpenServicesModalAsync()
    {
        await LoadServicesAsync();
        IsServicesModalOpen = true;
    }

    [RelayCommand]
    public void CloseServicesModal()
    {
        IsServicesModalOpen = false;
    }

    public async Task LoadServicesAsync()
    {
        try
        {
            var dbServices = await _inventoryService.GetClinicalServicesAsync();
            if (dbServices != null && dbServices.Count > 0)
            {
                Services = new ObservableCollection<ClinicalService>(dbServices);
            }
            else
            {
                LoadFallbackServices();
            }
        }
        catch
        {
            if (Services.Count == 0)
            {
                LoadFallbackServices();
            }
        }

        ApplyServicesFilter();
        if (SelectedService == null || !Services.Contains(SelectedService))
        {
            SelectedService = FilteredServices.FirstOrDefault() ?? Services.FirstOrDefault();
        }
    }

    private void LoadFallbackServices()
    {
        var services = new List<ClinicalService>
        {
            new()
            {
                Id = 1,
                Code = "SERV-001",
                Name = "Consulta Médica General",
                Description = "Evaluación clínica general, exploración física, diagnóstico y prescripción médica.",
                Category = "Consulta",
                Price = 300.00m,
                EstimatedDurationMinutes = 25,
                DefaultProducts = new List<ServiceDefaultProduct>()
            },
            new()
            {
                Id = 2,
                Code = "SERV-002",
                Name = "Curación Menor y Asepsia",
                Description = "Aseo quirúrgico superficial, desinfección, curación de herida o quemadura y vendaje de protección.",
                Category = "Curaciones",
                Price = 250.00m,
                EstimatedDurationMinutes = 20,
                DefaultProducts = new List<ServiceDefaultProduct>()
            },
            new()
            {
                Id = 3,
                Code = "SERV-003",
                Name = "Sutura de Herida Simple",
                Description = "Afrontamiento por planos de bordes de herida dérmica limpia con material estéril.",
                Category = "Procedimientos",
                Price = 450.00m,
                EstimatedDurationMinutes = 35,
                DefaultProducts = new List<ServiceDefaultProduct>()
            },
            new()
            {
                Id = 4,
                Code = "SERV-004",
                Name = "Monitoreo de Glucemia Capilar",
                Description = "Toma de muestra capilar y determinación cuantitativa inmediata de glucosa en sangre.",
                Category = "Diagnóstico",
                Price = 80.00m,
                EstimatedDurationMinutes = 10,
                DefaultProducts = new List<ServiceDefaultProduct>()
            },
            new()
            {
                Id = 5,
                Code = "SERV-005",
                Name = "Certificado Médico Oficial",
                Description = "Examen médico integral, somatometría y expedición de certificado de salud.",
                Category = "Certificados",
                Price = 200.00m,
                EstimatedDurationMinutes = 20,
                DefaultProducts = new List<ServiceDefaultProduct>()
            }
        };

        Services = new ObservableCollection<ClinicalService>(services);
    }

    partial void OnServiceCategoryFilterChanged(string value) => ApplyServicesFilter();
    partial void OnServiceSearchTextChanged(string value) => ApplyServicesFilter();

    [RelayCommand]
    public void FilterServiceCategory(string category)
    {
        ServiceCategoryFilter = category;
    }

    private void ApplyServicesFilter()
    {
        var query = Services.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(ServiceCategoryFilter) && ServiceCategoryFilter != "Todos")
        {
            query = query.Where(s => s.Category.Equals(ServiceCategoryFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(ServiceSearchText))
        {
            var search = TextSearch.Normalize(ServiceSearchText);
            query = query.Where(s => TextSearch.Matches(search, s.Name, s.Code, s.Category, s.Description));
        }

        FilteredServices = new ObservableCollection<ClinicalService>(query.OrderBy(s => s.Category).ThenBy(s => s.Name));
        if (SelectedService == null || !FilteredServices.Contains(SelectedService))
        {
            SelectedService = FilteredServices.FirstOrDefault();
        }
    }

    [RelayCommand]
    public void SelectService(ClinicalService? service)
    {
        SelectedService = service;
    }

    [RelayCommand]
    public void AddServiceToCart(ClinicalService? service)
    {
        AddServiceToCartInternal(service, includeDefaultSupplies: true);
    }

    [RelayCommand]
    public void AddServiceOnlyToCart(ClinicalService? service)
    {
        AddServiceToCartInternal(service, includeDefaultSupplies: false);
    }

    private void AddServiceToCartInternal(ClinicalService? service, bool includeDefaultSupplies)
    {
        service ??= SelectedService;
        if (service == null)
        {
            ShowAlert("Por favor seleccione un servicio clínico para agregar.", false);
            return;
        }

        var serviceId = -service.Id;
        var existingService = Cart.FirstOrDefault(c => c.ProductId == serviceId || (c.IsService && c.Code == service.Code));
        if (existingService != null)
        {
            existingService.Quantity++;
        }
        else
        {
            Cart.Add(new CartItem
            {
                ProductId = serviceId,
                Code = service.Code,
                Name = service.Name,
                GenericName = $"Servicio Clínico • {service.Category}",
                Presentation = $"Honorario • {service.EstimatedDurationMinutes} min",
                Unit = "Servicio",
                AvailableStock = 999,
                UnitPrice = service.Price,
                RequiresRefrigeration = false,
                Quantity = 1,
                IsService = true
            });
        }

        int addedSuppliesCount = 0;
        int failedSuppliesCount = 0;

        if (includeDefaultSupplies && service.DefaultProducts != null && service.DefaultProducts.Count > 0)
        {
            foreach (var dp in service.DefaultProducts)
            {
                var prod = Products.FirstOrDefault(p => p.Id == dp.ProductId) ?? dp.Product;
                if (prod != null)
                {
                    if (prod.CurrentStock > 0)
                    {
                        var existingProd = Cart.FirstOrDefault(c => c.ProductId == prod.Id);
                        int qtyToAdd = dp.Quantity;
                        if (existingProd != null)
                        {
                            int allowed = Math.Min(existingProd.Quantity + qtyToAdd, prod.CurrentStock);
                            existingProd.Quantity = allowed;
                        }
                        else
                        {
                            int allowed = Math.Min(qtyToAdd, prod.CurrentStock);
                            Cart.Add(new CartItem
                            {
                                ProductId = prod.Id,
                                Code = prod.Code,
                                Name = prod.Name,
                                GenericName = prod.GenericName,
                                Presentation = prod.Presentation,
                                Unit = prod.Unit,
                                AvailableStock = prod.CurrentStock,
                                UnitPrice = prod.Price,
                                RequiresRefrigeration = prod.RequiresRefrigeration,
                                Quantity = allowed,
                                IsService = false
                            });
                        }
                        addedSuppliesCount++;
                    }
                    else
                    {
                        failedSuppliesCount++;
                    }
                }
            }
        }

        RecalculateTotals();

        string msg = includeDefaultSupplies && addedSuppliesCount > 0
            ? $"¡Servicio '{service.Name}' y {addedSuppliesCount} insumos cargados a la venta!"
            : $"¡Servicio '{service.Name}' cargado a la venta!";

        if (failedSuppliesCount > 0)
        {
            msg += $" ({failedSuppliesCount} insumos sin stock fueron omitidos)";
        }

        ShowAlert(msg, true);
        IsServicesModalOpen = false;
    }

    [RelayCommand]
    public void AddServiceToActiveConsultation(ClinicalService? service)
    {
        service ??= SelectedService;
        if (service == null)
        {
            ShowAlert("Por favor seleccione un servicio clínico para agregar.", false);
            return;
        }

        if (!_consultationService.HasActiveConsultation)
        {
            AvailablePatientsForConsultation = new ObservableCollection<PatientRecord>(_consultationService.Patients);
            if (SelectedPatientForConsultation == null || !AvailablePatientsForConsultation.Contains(SelectedPatientForConsultation))
            {
                SelectedPatientForConsultation = AvailablePatientsForConsultation.FirstOrDefault();
            }
            NewConsultationChiefComplaint = $"Atención: {service.Name}";
            IsSelectPatientModalOpen = true;
            ShowAlert("Debe haber una consulta activa para transferir servicios. Seleccione el paciente para iniciarla.", false);
            return;
        }

        // Agregar honorario a la consulta activa
        _consultationService.AddCartItemToActiveConsultation(
            -service.Id,
            service.Code,
            service.Name,
            $"Servicio Clínico • {service.Category}",
            $"Honorario • {service.EstimatedDurationMinutes} min",
            "Servicio",
            service.Price,
            1
        );

        int addedSupplies = 0;
        if (service.DefaultProducts != null)
        {
            foreach (var dp in service.DefaultProducts)
            {
                var prod = Products.FirstOrDefault(p => p.Id == dp.ProductId) ?? dp.Product;
                if (prod != null)
                {
                    _consultationService.AddCartItemToActiveConsultation(
                        prod.Id,
                        prod.Code,
                        prod.Name,
                        prod.GenericName,
                        prod.Presentation,
                        prod.Unit,
                        prod.Price,
                        dp.Quantity
                    );
                    addedSupplies++;
                }
            }
        }

        ShowAlert($"¡Servicio '{service.Name}' ({addedSupplies} insumos) guardado en la consulta activa de {_consultationService.CurrentConsultation?.PatientName}!", true);
        IsServicesModalOpen = false;
    }

    [RelayCommand]
    public void OpenCreateServiceModal()
    {
        NewServiceCode = $"SERV-{Services.Count + 1:D3}";
        NewServiceName = string.Empty;
        NewServiceDescription = string.Empty;
        NewServiceCategory = "Consulta";
        NewServicePrice = 300.00m;
        NewServiceDuration = 20;
        NewProductQuantity = 1;
        NewProductInstructions = string.Empty;
        NewServiceDefaultProducts.Clear();

        AvailableProductsForService = new ObservableCollection<Product>(Products.OrderBy(p => p.Name));
        SelectedProductForService = AvailableProductsForService.FirstOrDefault();

        IsCreateServiceModalOpen = true;
    }

    [RelayCommand]
    public void CloseCreateServiceModal()
    {
        IsCreateServiceModalOpen = false;
    }

    [RelayCommand]
    public void AddProductToNewService()
    {
        if (SelectedProductForService == null) return;

        var existing = NewServiceDefaultProducts.FirstOrDefault(dp => dp.ProductId == SelectedProductForService.Id);
        if (existing != null)
        {
            existing.Quantity += Math.Max(1, NewProductQuantity);
            if (!string.IsNullOrWhiteSpace(NewProductInstructions))
            {
                existing.Instructions = NewProductInstructions.Trim();
            }
        }
        else
        {
            NewServiceDefaultProducts.Add(new ServiceDefaultProduct
            {
                ProductId = SelectedProductForService.Id,
                Product = SelectedProductForService,
                Quantity = Math.Max(1, NewProductQuantity),
                Instructions = string.IsNullOrWhiteSpace(NewProductInstructions) ? null : NewProductInstructions.Trim()
            });
        }

        NewProductQuantity = 1;
        NewProductInstructions = string.Empty;
    }

    [RelayCommand]
    public void RemoveProductFromNewService(ServiceDefaultProduct item)
    {
        NewServiceDefaultProducts.Remove(item);
    }

    [RelayCommand]
    public async Task SaveNewServiceAsync()
    {
        if (string.IsNullOrWhiteSpace(NewServiceName))
        {
            ShowAlert("El nombre del servicio clínico es obligatorio.", false);
            return;
        }

        if (string.IsNullOrWhiteSpace(NewServiceCode))
        {
            NewServiceCode = $"SERV-{Services.Count + 1:D3}";
        }

        var newService = new ClinicalService
        {
            Code = NewServiceCode.Trim().ToUpper(),
            Name = NewServiceName.Trim(),
            Description = NewServiceDescription.Trim(),
            Category = NewServiceCategory,
            Price = NewServicePrice,
            EstimatedDurationMinutes = NewServiceDuration,
            IsActive = true
        };

        var defaultItems = NewServiceDefaultProducts
            .Select(p => (p.ProductId, p.Quantity, p.Instructions))
            .ToList();

        try
        {
            var saved = await _inventoryService.SaveClinicalServiceAsync(newService, defaultItems);
            await LoadServicesAsync();
            SelectedService = Services.FirstOrDefault(s => s.Id == saved.Id) ?? saved;
            IsCreateServiceModalOpen = false;
            ShowAlert($"¡Servicio '{saved.Name}' guardado exitosamente!", true);
        }
        catch (Exception ex)
        {
            ShowAlert($"Error al guardar el servicio: {ex.Message}", false);
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
