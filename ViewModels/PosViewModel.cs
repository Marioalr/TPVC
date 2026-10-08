using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

/// <summary>
/// Punto de venta (venta de mostrador). El buscador es el centro: se escribe, Enter agrega el
/// primero ("3*para" agrega 3) y la cuenta se cobra con el mismo componente que "Terminar
/// consulta" (<see cref="CheckoutViewModel"/>). Con el POS solo se manda a la consulta, nunca al
/// revés: "Abrir consulta" y "Agregar a la consulta" cuando hay una en curso.
/// Corte de caja, historial y servicios viven en archivos parciales.
/// </summary>
public partial class PosViewModel : ObservableObject, IDisposable
{
    private static readonly Regex QuantityPrefix = new(@"^\s*(\d{1,3})\s*\*\s*(.*)$", RegexOptions.Compiled);

    private readonly InventoryService _inventoryService;
    private readonly ConsultationService _consultationService;
    private readonly ClinicProfileService? _clinicProfileService;
    private readonly PrintingService? _printingService;
    private readonly NotificationService? _notifications;
    private readonly Action? _onProfileChangedHandler;
    private readonly Action? _onSalesChangedHandler;

    public Action? OnBackToHubRequested { get; set; }

    /// <summary>El POS manda a la consulta (nunca al revés): la cuenta se cobra al terminarla.</summary>
    public Action? OnOpenConsultationRequested { get; set; }

    /// <summary>Pide el foco en el buscador (al cerrar una hoja, tras cobrar...).</summary>
    public event Action? FocusSearchRequested;

    [RelayCommand]
    public void BackToHub() => OnBackToHubRequested?.Invoke();

    [RelayCommand]
    public void OpenActiveConsultation() => OnOpenConsultationRequested?.Invoke();

    public PosViewModel(
        InventoryService inventoryService,
        ConsultationService? consultationService = null,
        ClinicProfileService? clinicProfileService = null,
        PrintingService? printingService = null,
        NotificationService? notificationService = null)
    {
        _inventoryService = inventoryService;
        _consultationService = consultationService ?? new ConsultationService();
        _clinicProfileService = clinicProfileService;
        _printingService = printingService;
        _notifications = notificationService;

        Checkout = new CheckoutViewModel(inventoryService, () => string.IsNullOrWhiteSpace(DoctorNameWithTitle) ? "Dr. Titular" : DoctorNameWithTitle)
        {
            ShowsClientField = true,
            ChargeHint = "F5 o Ctrl + Enter",
            Notify = (message, ok) => ShowAlert(message, ok),
            Completed = OnSaleCompletedAsync,
            Failed = () => _ = LoadCatalogAsync()
        };
        Checkout.PropertyChanged += (_, e) =>
        {
            // Los nombres de siempre del POS siguen avisando sus cambios
            switch (e.PropertyName)
            {
                case nameof(CheckoutViewModel.TotalAmount): OnPropertyChanged(nameof(TotalCartAmount)); break;
                case nameof(CheckoutViewModel.HasItems): OnPropertyChanged(nameof(HasCartItems)); OnPropertyChanged(nameof(CanAddToActiveConsultation)); break;
            }
        };

        _inventoryService.OnInventoryChanged += OnInventoryChangedHandler;
        _consultationService.OnActiveConsultationChanged += SyncActiveConsultation;

        if (_clinicProfileService != null)
        {
            _onProfileChangedHandler = RefreshProfileData;
            _clinicProfileService.ProfileChanged += _onProfileChangedHandler;
            RefreshProfileData();
        }

        _onSalesChangedHandler = () => RunOnUi(async () =>
        {
            if (IsTicketHistoryModalOpen) await LoadSalesHistoryAsync();
            if (IsCashCutModalOpen) await LoadCashCutForDateAsync();
        });
        _inventoryService.OnSalesChanged += _onSalesChangedHandler;

        SyncActiveConsultation();
        _ = LoadServicesAsync();
    }

    private static void RunOnUi(Func<Task> action)
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.InvokeAsync(action);
                return;
            }
        }
        catch
        {
            // Sin WPF (pruebas en Linux)
        }
        _ = action();
    }

    public void Dispose()
    {
        _inventoryService.OnInventoryChanged -= OnInventoryChangedHandler;
        if (_onSalesChangedHandler != null) _inventoryService.OnSalesChanged -= _onSalesChangedHandler;
        if (_clinicProfileService != null && _onProfileChangedHandler != null) _clinicProfileService.ProfileChanged -= _onProfileChangedHandler;
        _consultationService.OnActiveConsultationChanged -= SyncActiveConsultation;
        StopConsultationTimer();
        GC.SuppressFinalize(this);
    }

    private void OnInventoryChangedHandler() => RunOnUi(async () =>
    {
        await LoadCatalogAsync();
        await LoadServicesAsync();
    });

    public async Task InitializeAsync()
    {
        await LoadCatalogAsync();
        await LoadServicesAsync();
    }

    // ================================================================== perfil (ticket)

    [ObservableProperty]
    private string _clinicProfileName = "Consultorio Médico TPVC";

    [ObservableProperty]
    private string _doctorNameWithTitle = "Dr. Titular";

    [ObservableProperty]
    private string _doctorSpecialtyAndLicense = "Médico General";

    [ObservableProperty]
    private string _clinicAddressAndPhone = "";

    public void RefreshProfileData()
    {
        var p = _clinicProfileService?.CurrentProfile;
        if (p == null) return;

        ClinicProfileName = string.IsNullOrWhiteSpace(p.ClinicName) ? "Consultorio Médico TPVC" : p.ClinicName;
        DoctorNameWithTitle = p.DoctorNameWithTitle;

        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.DoctorSpecialty)) details.Add(p.DoctorSpecialty);
        if (!string.IsNullOrWhiteSpace(p.DoctorLicense)) details.Add($"Céd. Prof. {p.DoctorLicense}");
        DoctorSpecialtyAndLicense = details.Count > 0 ? string.Join(" • ", details) : "Médico General";

        var contact = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.ClinicAddress)) contact.Add(p.ClinicAddress);
        if (!string.IsNullOrWhiteSpace(p.ClinicPhone)) contact.Add($"Tel: {p.ClinicPhone}");
        ClinicAddressAndPhone = contact.Count > 0 ? string.Join(" • ", contact) : "";
    }

    // ================================================================== catálogo y búsqueda

    [ObservableProperty]
    private ObservableCollection<Category> _categories = new();

    [ObservableProperty]
    private ObservableCollection<Product> _products = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<Product> _searchSuggestions = new();

    [ObservableProperty]
    private int _selectedSuggestionIndex = -1;

    [ObservableProperty]
    private Product? _selectedSuggestion;

    /// <summary>Cantidad escrita antes del asterisco ("3*para" → 3).</summary>
    [ObservableProperty]
    private int _pendingQuantity = 1;

    public bool HasPendingQuantity => PendingQuantity > 1;
    public string PendingQuantityText => $"Enter agrega {PendingQuantity}";
    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);
    public bool HasNoSearchResults => HasSearchText && SearchSuggestions.Count == 0;

    partial void OnPendingQuantityChanged(int value)
    {
        OnPropertyChanged(nameof(HasPendingQuantity));
        OnPropertyChanged(nameof(PendingQuantityText));
    }

    partial void OnSelectedSuggestionIndexChanged(int value)
    {
        SelectedSuggestion = value >= 0 && value < SearchSuggestions.Count ? SearchSuggestions[value] : null;
    }

    partial void OnSelectedSuggestionChanged(Product? value)
    {
        if (value == null) return;
        var idx = SearchSuggestions.IndexOf(value);
        if (idx >= 0 && idx != SelectedSuggestionIndex) SelectedSuggestionIndex = idx;
    }

    public async Task LoadCatalogAsync()
    {
        var cats = await _inventoryService.GetCategoriesAsync();
        var allCat = new Category { Id = 0, Name = "Todas" };
        Categories = new ObservableCollection<Category>(new[] { allCat }.Concat(cats));
        if (CatalogSelectedCategory == null || !Categories.Any(c => c.Id == CatalogSelectedCategory.Id))
        {
            CatalogSelectedCategory = allCat;
        }
        else
        {
            CatalogSelectedCategory = Categories.First(c => c.Id == CatalogSelectedCategory.Id);
        }

        Products = new ObservableCollection<Product>(await _inventoryService.GetProductsAsync());
        ApplyCatalogFilters();
        ApplySearchSuggestions(SearchText);
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplySearchSuggestions(value);
        OnPropertyChanged(nameof(HasSearchText));
    }

    /// <summary>"3*para" → (3, "para"); sin asterisco la cantidad es 1.</summary>
    public static (int Quantity, string Query) ParseQuantityPrefix(string? text)
    {
        var match = QuantityPrefix.Match(text ?? string.Empty);
        if (!match.Success) return (1, text ?? string.Empty);
        int quantity = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return (Math.Max(1, quantity), match.Groups[2].Value);
    }

    /// <summary>
    /// Productos que coinciden (sin acentos): el código exacto primero (lector de código de barras),
    /// luego el nombre que empieza así, el genérico que empieza así, una palabra que empieza así y
    /// al final lo que lo contiene en nombre, genérico, código o ubicación. Dentro de cada grupo,
    /// los que tienen existencias primero.
    /// </summary>
    public static List<Product> RankProducts(IEnumerable<Product> products, string? query, int limit = 8)
    {
        var q = TextSearch.Normalize(query);
        if (q.Length == 0) return new List<Product>();

        return products
            .Select(p =>
            {
                var name = TextSearch.Normalize(p.Name);
                var generic = TextSearch.Normalize(p.GenericName);
                var code = TextSearch.Normalize(p.Code);
                int rank;
                if (code == q) rank = 0;
                else if (name.StartsWith(q, StringComparison.Ordinal)) rank = 1;
                else if (generic.StartsWith(q, StringComparison.Ordinal)) rank = 2;
                else if ((" " + name).Contains(" " + q, StringComparison.Ordinal) || (" " + generic).Contains(" " + q, StringComparison.Ordinal)) rank = 3;
                else if (TextSearch.Matches(q, p.Name, p.GenericName, p.Code, p.Location)) rank = 4;
                else rank = -1;
                return (Product: p, Rank: rank);
            })
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Product.CurrentStock <= 0)
            .ThenBy(x => x.Product.Name, StringComparer.CurrentCulture)
            .Take(limit)
            .Select(x => x.Product)
            .ToList();
    }

    private void ApplySearchSuggestions(string? text)
    {
        var (quantity, query) = ParseQuantityPrefix(text);
        PendingQuantity = quantity;

        SearchSuggestions = new ObservableCollection<Product>(RankProducts(Products, query));
        SelectedSuggestionIndex = SearchSuggestions.Count > 0 ? 0 : -1;
        SelectedSuggestion = SearchSuggestions.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoSearchResults));
    }

    public void MoveSuggestionDown()
    {
        if (SearchSuggestions.Count == 0) return;
        SelectedSuggestionIndex = SelectedSuggestionIndex < 0 || SelectedSuggestionIndex >= SearchSuggestions.Count - 1 ? 0 : SelectedSuggestionIndex + 1;
    }

    public void MoveSuggestionUp()
    {
        if (SearchSuggestions.Count == 0) return;
        SelectedSuggestionIndex = SelectedSuggestionIndex <= 0 || SelectedSuggestionIndex >= SearchSuggestions.Count ? SearchSuggestions.Count - 1 : SelectedSuggestionIndex - 1;
    }

    /// <summary>Enter en el buscador: agrega la sugerencia marcada con la cantidad escrita.</summary>
    public void ConfirmSelectedSuggestion()
    {
        if (SearchSuggestions.Count == 0) return;
        var target = SelectedSuggestion
            ?? (SelectedSuggestionIndex >= 0 && SelectedSuggestionIndex < SearchSuggestions.Count ? SearchSuggestions[SelectedSuggestionIndex] : null)
            ?? SearchSuggestions[0];
        SelectSuggestion(target);
    }

    public void SelectSuggestion(Product? product)
    {
        if (product == null) return;
        AddToCartWithQuantity(product, PendingQuantity);
        SearchText = string.Empty;
        FocusSearchRequested?.Invoke();
    }

    /// <summary>Esc en el buscador: primero cierra la lista, luego borra lo escrito.</summary>
    public void ClearSearch()
    {
        SearchText = string.Empty;
        FocusSearchRequested?.Invoke();
    }

    // ================================================================== catálogo (hoja)

    [ObservableProperty]
    private bool _isCatalogModalOpen;

    [ObservableProperty]
    private string _catalogSearchText = string.Empty;

    [ObservableProperty]
    private Category? _catalogSelectedCategory;

    [ObservableProperty]
    private ObservableCollection<Product> _catalogFilteredProducts = new();

    public string CatalogSummary => CatalogFilteredProducts.Count == 1 ? "1 producto" : $"{CatalogFilteredProducts.Count} productos";

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
        FocusSearchRequested?.Invoke();
    }

    /// <summary>Enter en el buscador del catálogo: agrega el primero de la lista.</summary>
    [RelayCommand]
    public void AddFirstCatalogProduct()
    {
        var first = CatalogFilteredProducts.FirstOrDefault(p => p.CurrentStock > 0);
        if (first != null) AddToCart(first);
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

        CatalogFilteredProducts = new ObservableCollection<Product>(string.IsNullOrWhiteSpace(CatalogSearchText)
            ? list.OrderBy(p => p.Name, StringComparer.CurrentCulture)
            : RankProducts(list, CatalogSearchText, limit: int.MaxValue));
        OnPropertyChanged(nameof(CatalogSummary));
    }

    // ================================================================== cuenta (venta de mostrador)

    /// <summary>La cuenta de la venta: el mismo componente que el cobro de la consulta.</summary>
    public CheckoutViewModel Checkout { get; }

    public ObservableCollection<CartItem> Cart => Checkout.Cart;
    public decimal TotalCartAmount => Checkout.TotalAmount;
    public bool HasCartItems => Checkout.HasItems;

    [RelayCommand]
    public void AddToCart(Product? product)
    {
        if (product != null) AddToCartWithQuantity(product, 1);
    }

    public void AddToCartWithQuantity(Product product, int quantity) => Checkout.AddProduct(product, quantity);

    [RelayCommand]
    public void IncreaseQuantity(CartItem? item) => Checkout.IncreaseQuantity(item);

    [RelayCommand]
    public void DecreaseQuantity(CartItem? item) => Checkout.DecreaseQuantity(item);

    [RelayCommand]
    public void RemoveFromCart(CartItem? item) => Checkout.RemoveFromCart(item);

    [RelayCommand]
    public void ClearCart() => Checkout.ClearCart();

    [RelayCommand]
    public void UndoClearCart() => Checkout.UndoClear();

    /// <summary>Cobra la venta (F5 / Ctrl+Enter / botón): mismo cobro que "Terminar consulta".</summary>
    public Task ProcessSaleAsync() => Checkout.ChargeAsync();

    private Task OnSaleCompletedAsync(SaleReceipt? receipt)
    {
        if (receipt == null) return Task.CompletedTask;
        CurrentReceipt = receipt;
        IsFreshSaleTicket = true;
        IsTicketModalOpen = true;
        TicketStatusMessage = null;
        ShowAlert($"Venta cobrada · {receipt.Folio}", true, toast: true);
        return Task.CompletedTask;
    }

    // ================================================================== ticket

    [ObservableProperty]
    private SaleReceipt? _currentReceipt;

    [ObservableProperty]
    private bool _isTicketModalOpen;

    [ObservableProperty]
    private string? _ticketStatusMessage;

    /// <summary>Ticket de la venta recién cobrada (no una reimpresión desde el historial).</summary>
    [ObservableProperty]
    private bool _isFreshSaleTicket;

    [RelayCommand]
    public void CloseTicketModal()
    {
        IsTicketModalOpen = false;
        CurrentReceipt = null;
        TicketStatusMessage = null;
        FocusSearchRequested?.Invoke();
    }

    [RelayCommand]
    public void PrintTicket()
    {
        if (CurrentReceipt == null) return;
        var profile = _clinicProfileService?.CurrentProfile ?? new ClinicProfile
        {
            ClinicName = ClinicProfileName,
            DoctorName = DoctorNameWithTitle
        };

        if (_printingService != null)
        {
            bool printed = _printingService.PrintTicket(CurrentReceipt, profile);
            TicketStatusMessage = printed ? "Ticket enviado a la impresora." : "Impresión cancelada o sin impresora seleccionada.";
        }
        else
        {
            TicketStatusMessage = "Ticket emitido.";
        }
    }

    // ================================================================== consulta en curso

    public bool HasActiveConsultation => _consultationService.HasActiveConsultation;
    public string ActiveConsultationPatientName => _consultationService.CurrentConsultation?.PatientName ?? string.Empty;
    /// <summary>Hay consulta en curso que se cobra en este equipo (no la que se envió a otro).</summary>
    public bool CanChargeToActiveConsultation => HasActiveConsultation && _consultationService.CurrentConsultation?.IsHandedOff != true;

    public bool CanAddToActiveConsultation => CanChargeToActiveConsultation && HasCartItems;

    public string ActiveConsultationBannerText => !HasActiveConsultation
        ? string.Empty
        : _consultationService.CurrentConsultation?.IsHandedOff == true
            ? $"Consulta de {ActiveConsultationPatientName} enviada a otro equipo: se cobra allá"
            : $"Consulta en curso: {ActiveConsultationPatientName} · {ActiveConsultationElapsedTime}";

    [ObservableProperty]
    private string _activeConsultationElapsedTime = "00:00";

    partial void OnActiveConsultationElapsedTimeChanged(string value) => OnPropertyChanged(nameof(ActiveConsultationBannerText));

    private System.Threading.Timer? _consultationTimer;

    public void SyncActiveConsultation()
    {
        OnPropertyChanged(nameof(HasActiveConsultation));
        OnPropertyChanged(nameof(ActiveConsultationPatientName));
        OnPropertyChanged(nameof(CanAddToActiveConsultation));
        OnPropertyChanged(nameof(CanChargeToActiveConsultation));
        OnPropertyChanged(nameof(ActiveConsultationBannerText));

        if (HasActiveConsultation) StartConsultationTimer();
        else StopConsultationTimer();
    }

    private void StartConsultationTimer()
    {
        _consultationTimer?.Dispose();
        UpdateActiveConsultationTimer();
        _consultationTimer = new System.Threading.Timer(_ => RunOnUi(() =>
        {
            UpdateActiveConsultationTimer();
            return Task.CompletedTask;
        }), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void StopConsultationTimer()
    {
        _consultationTimer?.Dispose();
        _consultationTimer = null;
        ActiveConsultationElapsedTime = "00:00";
    }

    public void UpdateActiveConsultationTimer(DateTime? asOf = null)
    {
        ActiveConsultationElapsedTime = _consultationService.CurrentConsultation?.GetFormattedElapsedTime(asOf) ?? "00:00";
    }

    /// <summary>
    /// Pasa lo que hay en la venta a la cuenta de la consulta en curso (se cobra al terminarla).
    /// Las consultas se inician en Pacientes: sin consulta en curso no hay a dónde mandarlo.
    /// </summary>
    [RelayCommand]
    public void AddToActiveConsultation()
    {
        if (Cart.Count == 0)
        {
            ShowAlert("La venta no tiene productos para agregar a la consulta.", false);
            return;
        }
        if (!_consultationService.HasActiveConsultation)
        {
            ShowAlert("No hay una consulta en curso. Las consultas se inician en Pacientes.", false);
            return;
        }
        if (!CanChargeToActiveConsultation)
        {
            ShowAlert("La consulta en curso se envió a otro equipo: se cobra allá.", false);
            return;
        }

        int totalAdded = 0;
        foreach (var item in Cart)
        {
            if (!_consultationService.AddCartItemToActiveConsultation(item.ProductId, item.Code, item.Name, item.GenericName, item.Presentation, item.Unit, item.UnitPrice, item.Quantity))
            {
                // No se vacía la venta si la consulta dejó de aceptar cargos
                ShowAlert("No se pudo agregar a la consulta en curso. La venta sigue aquí.", false);
                SyncActiveConsultation();
                return;
            }
            totalAdded += item.Quantity;
        }

        var patientName = _consultationService.CurrentConsultation?.PatientName;
        Checkout.Reset();
        SyncActiveConsultation();
        ShowAlert($"{totalAdded} artículo(s) agregados a la cuenta de la consulta de {patientName}", true, toast: true);
    }

    // ================================================================== avisos

    [ObservableProperty]
    private string? _alertMessage;

    [ObservableProperty]
    private bool _isAlertVisible;

    [ObservableProperty]
    private bool _isSuccessAlert;

    /// <summary>
    /// Último aviso (lo leen las pruebas) y, con servicio de avisos, un aviso breve: los problemas
    /// siempre; las confirmaciones solo si importan (la cuenta ya muestra lo agregado).
    /// </summary>
    private void ShowAlert(string message, bool isSuccess, bool toast = false)
    {
        AlertMessage = message;
        IsSuccessAlert = isSuccess;
        IsAlertVisible = true;

        if (_notifications == null) return;
        if (!isSuccess) _notifications.Warning(message);
        else if (toast) _notifications.Success(message);
    }
}
