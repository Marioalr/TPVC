using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Rules;

namespace TPVC.ViewModels;

/// <summary>Renglón de una cuenta (producto o servicio).</summary>
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
    public bool IsIndividualUnits { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtotal))]
    [NotifyPropertyChangedFor(nameof(StockText))]
    [NotifyPropertyChangedFor(nameof(IsShortOfStock))]
    private int _quantity = 1;

    public decimal Subtotal => Quantity * UnitPrice;

    /// <summary>
    /// Revisión 20: se cobran más de las que hay (un producto; las existencias pueden estar en negativo). No impide cobrar:
    /// el renglón y el cobro lo avisan (<see cref="CheckoutRules.CartShortages"/>).
    /// </summary>
    public bool IsShortOfStock => !IsService && Quantity > AvailableStock;

    /// <summary>
    /// "Existencias: 12", "Piezas disp.: 12" o "Servicio"; si se cobran más de las que hay, con " · revisar inventario"
    /// (los negativos como "−3").
    /// </summary>
    public string StockText => CheckoutRules.StockText(IsService, IsIndividualUnits, AvailableStock, Quantity);

    /// <summary>La cantidad escrita a mano: mínimo 1. Revisión 20: las existencias ya no la topan.</summary>
    partial void OnQuantityChanged(int value)
    {
        // Paso 8.26: CheckoutRules.ClampQuantity
        var allowed = CheckoutRules.ClampQuantity(value, IsService, AvailableStock);
        if (allowed != value) Quantity = allowed;
    }
}

/// <summary>
/// Cobro: cuenta, forma de pago, recibido y cambio. Lo usan el Punto de Venta (venta de mostrador)
/// y "Terminar consulta" con la misma pantalla y las mismas reglas, cada uno con su propia cuenta
/// (antes compartían el carrito y terminar una consulta vaciaba la venta a medias del mostrador).
/// </summary>
public partial class CheckoutViewModel : ObservableObject
{
    public const string DefaultClient = "Público General";
    public const string Cash = "Efectivo";

    private readonly InventoryService? _inventoryService;
    private readonly Func<string> _cashierName;
    private List<CartItem>? _lastClearedCart;
    private bool _restoringCart;
    private bool _isCharging;

    public CheckoutViewModel(InventoryService? inventoryService, Func<string>? cashierName = null)
    {
        _inventoryService = inventoryService;
        _cashierName = cashierName ?? (() => "Dr. Titular");
        Cart.CollectionChanged += OnCartCollectionChanged;
    }

    /// <summary>Avisos para quien usa el cobro (texto, éxito).</summary>
    public Action<string, bool>? Notify { get; set; }

    /// <summary>Se cobró: recibo de la venta, o null si se terminó sin cargos (solo cuando se permite).</summary>
    public Func<SaleReceipt?, Task>? Completed { get; set; }

    /// <summary>Falló el registro de la venta (p. ej. existencias): quien usa el cobro puede refrescar datos.</summary>
    public Action? Failed { get; set; }

    /// <summary>Texto del botón cuando la cuenta está vacía; si es null, una cuenta vacía no se cobra.</summary>
    public string? EmptyChargeText { get; init; }

    /// <summary>Complemento del botón: "Cobrar $300.00 y terminar".</summary>
    public string ChargeSuffix { get; init; } = string.Empty;

    /// <summary>Campo "Cliente" (venta de mostrador); en la consulta el paciente ya se sabe.</summary>
    public bool ShowsClientField { get; init; }

    /// <summary>Atajo de teclado que se muestra bajo el botón de cobrar.</summary>
    public string ChargeHint { get; init; } = string.Empty;

    public bool HasChargeHint => ChargeHint.Length > 0;

    public ObservableCollection<CartItem> Cart { get; } = new();

    [ObservableProperty]
    private CartItem? _selectedItem;

    [ObservableProperty]
    private string _patientName = DefaultClient;

    public IReadOnlyList<string> PaymentMethods { get; } = new[] { Cash, "Tarjeta Débito/Crédito", "Transferencia" };

    [ObservableProperty]
    private string _selectedPaymentMethod = Cash;

    [ObservableProperty]
    private decimal _cashReceived;

    [ObservableProperty]
    private bool _canUndoClear;

    public int TotalItems => Cart.Sum(c => c.Quantity);
    public decimal TotalAmount => Cart.Sum(c => c.Subtotal);
    public bool HasItems => Cart.Count > 0;
    // Paso 8.26: las cuentas viven en CheckoutRules (vectores checkout.json)
    public bool IsCash => CheckoutRules.IsCash(SelectedPaymentMethod);

    public decimal ChangeDue => CheckoutRules.ChangeDue(CashReceived, TotalAmount);
    public bool HasChange => CheckoutRules.HasChange(IsCash, CashReceived, TotalAmount);

    /// <summary>Lo que falta cuando se escribió un efectivo menor al total (vacío = paga exacto).</summary>
    public decimal MissingAmount => CheckoutRules.MissingAmount(IsCash, CashReceived, TotalAmount);
    public bool IsCashShort => MissingAmount > 0;

    public bool CanCharge => CheckoutRules.CanCharge(HasItems, EmptyChargeText != null, MissingAmount);

    public string ChargeText => CheckoutRules.ChargeText(HasItems, TotalAmount, ChargeSuffix, EmptyChargeText);

    /// <summary>
    /// Revisión 20: el aviso de existencias al cobrar, sin bloquear: "Existencias insuficientes: Paracetamol (quedan 2, se
    /// cobran 5). Revise el inventario." (<see cref="CheckoutRules.CartShortages"/>); null si todo alcanza.
    /// </summary>
    public string? StockWarning => StockReviewRules.WarningText(CheckoutRules.CartShortages(
        Cart.Select(c => new CheckoutRules.CartLine(c.ProductId, c.Name, c.IsService, c.IsIndividualUnits, c.AvailableStock, c.Quantity))));

    public bool HasStockWarning => StockWarning != null;

    private string _cashReceivedText = string.Empty;
    private bool _typingCashReceived;

    /// <summary>
    /// Recibido como se escribe ("500", "$1,000", "250.5"); vacío = paga exacto. Lo escrito no se
    /// reformatea mientras se teclea (si no, "250." perdería el punto).
    /// </summary>
    public string CashReceivedText
    {
        get => _cashReceivedText;
        set
        {
            if (!SetProperty(ref _cashReceivedText, value ?? string.Empty)) return;
            _typingCashReceived = true;
            try { CashReceived = ParseAmount(value); }
            finally { _typingCashReceived = false; }
        }
    }

    /// <summary>Billetes probables por encima del total para escribir el recibido de un clic.</summary>
    public IReadOnlyList<decimal> QuickCashAmounts => SuggestCashAmounts(TotalAmount);

    public static IReadOnlyList<decimal> SuggestCashAmounts(decimal total) => CheckoutRules.SuggestCashAmounts(total);

    public static decimal ParseAmount(string? text) => CheckoutRules.ParseAmount(text);

    partial void OnSelectedPaymentMethodChanged(string value) => NotifyAmounts();
    partial void OnCashReceivedChanged(decimal value)
    {
        if (!_typingCashReceived)
        {
            _cashReceivedText = CheckoutRules.AmountText(value);
            OnPropertyChanged(nameof(CashReceivedText));
        }
        NotifyAmounts();
    }

    private void OnCartCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (CartItem item in e.OldItems) item.PropertyChanged -= OnItemPropertyChanged;
        if (e.NewItems != null) foreach (CartItem item in e.NewItems) item.PropertyChanged += OnItemPropertyChanged;
        if (e.Action == NotifyCollectionChangedAction.Reset) foreach (var item in Cart) { item.PropertyChanged -= OnItemPropertyChanged; item.PropertyChanged += OnItemPropertyChanged; }
        if (SelectedItem != null && !Cart.Contains(SelectedItem)) SelectedItem = Cart.LastOrDefault();
        // "Deshacer" solo justo después de vaciar: si se empieza otra cuenta ya no aplica
        if (e.Action == NotifyCollectionChangedAction.Add && CanUndoClear && !_restoringCart)
        {
            CanUndoClear = false;
            _lastClearedCart = null;
        }
        Recalculate();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CartItem.Quantity)) Recalculate();
    }

    /// <summary>Recalcula totales, cambio y el texto del botón.</summary>
    public void Recalculate()
    {
        OnPropertyChanged(nameof(TotalItems));
        OnPropertyChanged(nameof(TotalAmount));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(QuickCashAmounts));
        NotifyAmounts();
    }

    private void NotifyAmounts()
    {
        OnPropertyChanged(nameof(IsCash));
        OnPropertyChanged(nameof(ChangeDue));
        OnPropertyChanged(nameof(HasChange));
        OnPropertyChanged(nameof(MissingAmount));
        OnPropertyChanged(nameof(IsCashShort));
        OnPropertyChanged(nameof(CanCharge));
        OnPropertyChanged(nameof(ChargeText));
        OnPropertyChanged(nameof(StockWarning));
        OnPropertyChanged(nameof(HasStockWarning));
    }

    // ------------------------------------------------------------------ renglones

    /// <summary>
    /// Agrega un producto; si ya está, suma la cantidad. Revisión 20 (un inventario mal contado nunca impide cobrar): sin
    /// existencias o con menos de las que se piden se agrega igual y se avisa sin bloquear (<see cref="StockReviewRules.WarningText"/>).
    /// </summary>
    public bool AddProduct(Product product, int quantity = 1)
    {
        int requested = Math.Max(1, quantity);
        var existing = Cart.FirstOrDefault(c => c.ProductId == product.Id && !c.IsIndividualUnits);
        if (existing != null)
        {
            existing.AvailableStock = product.CurrentStock;
            existing.Quantity += requested;
            SelectedItem = existing;
            Recalculate();
            NotifyAdded(existing, $"{product.Name}: {existing.Quantity} en la cuenta");
            return true;
        }

        var line = new CartItem
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
            Quantity = requested
        };
        Cart.Add(line);
        SelectedItem = line;
        NotifyAdded(line, $"{product.Name} agregado ({line.Quantity})");
        return true;
    }

    /// <summary>El aviso de un renglón agregado: el de éxito, o el de existencias (sin bloquear) si se cobran más de las que hay.</summary>
    private void NotifyAdded(CartItem line, string ok)
    {
        if (line.IsShortOfStock)
        {
            Notify?.Invoke(StockReviewRules.WarningText(new[] { new StockShortage(line.Name, line.AvailableStock, line.Quantity, line.IsIndividualUnits) })!, false);
        }
        else
        {
            Notify?.Invoke(ok, true);
        }
    }

    /// <summary>Agrega un renglón ya armado (servicio, cargo de la consulta); si ya existe, suma la cantidad.</summary>
    public void AddLine(CartItem line)
    {
        var existing = Cart.FirstOrDefault(c =>
            c.ProductId == line.ProductId &&
            c.Code == line.Code &&
            c.IsService == line.IsService &&
            c.IsIndividualUnits == line.IsIndividualUnits);
        if (existing != null)
        {
            existing.Quantity += Math.Max(1, line.Quantity);
            SelectedItem = existing;
            return;
        }
        Cart.Add(line);
        SelectedItem = line;
    }

    [RelayCommand]
    public void IncreaseQuantity(CartItem? item)
    {
        item ??= SelectedItem;
        if (item == null) return;
        SelectedItem = item;
        // Revisión 20: sin tope; pasar de las existencias se avisa sin bloquear
        item.Quantity++;
        if (item.IsShortOfStock)
        {
            Notify?.Invoke(StockReviewRules.WarningText(new[] { new StockShortage(item.Name, item.AvailableStock, item.Quantity, item.IsIndividualUnits) })!, false);
        }
    }

    [RelayCommand]
    public void DecreaseQuantity(CartItem? item)
    {
        item ??= SelectedItem;
        if (item == null) return;
        SelectedItem = item;
        if (item.Quantity > 1) item.Quantity--;
        else RemoveFromCart(item);
    }

    [RelayCommand]
    public void RemoveFromCart(CartItem? item)
    {
        item ??= SelectedItem;
        if (item == null) return;
        int index = Cart.IndexOf(item);
        Cart.Remove(item);
        if (Cart.Count > 0) SelectedItem = Cart[Math.Clamp(index, 0, Cart.Count - 1)];
        Notify?.Invoke($"{item.Name} quitado de la cuenta", true);
    }

    /// <summary>Con el teclado: ↑/↓ eligen el renglón al que se aplican +, − y Supr.</summary>
    public void SelectNextItem(int step)
    {
        if (Cart.Count == 0) return;
        int index = SelectedItem == null ? (step > 0 ? -1 : Cart.Count) : Cart.IndexOf(SelectedItem);
        SelectedItem = Cart[Math.Clamp(index + step, 0, Cart.Count - 1)];
    }

    public void ClearCart()
    {
        if (Cart.Count == 0) return;
        _lastClearedCart = Cart.ToList();
        Cart.Clear();
        CanUndoClear = true;
        Notify?.Invoke("Se vació la cuenta (puede deshacerlo)", true);
    }

    public void UndoClear()
    {
        if (_lastClearedCart == null || _lastClearedCart.Count == 0) return;
        _restoringCart = true;
        try
        {
            foreach (var item in _lastClearedCart) Cart.Add(item);
        }
        finally
        {
            _restoringCart = false;
        }
        _lastClearedCart = null;
        CanUndoClear = false;
        Notify?.Invoke("Se restauró la cuenta", true);
    }

    /// <summary>Deja la cuenta lista para la siguiente (sin opción de deshacer).</summary>
    public void Reset()
    {
        Cart.Clear();
        _lastClearedCart = null;
        CanUndoClear = false;
        CashReceived = 0;
        PatientName = DefaultClient;
    }

    // ------------------------------------------------------------------ pago

    [RelayCommand]
    public void SetCashReceived(decimal amount) => CashReceived = Math.Max(0m, amount);

    [RelayCommand]
    public void SetExactCash() => CashReceived = TotalAmount;

    /// <summary>
    /// Registra la venta (descuenta existencias por FEFO y genera el folio) y avisa a quien usa el cobro. Si el efectivo
    /// escrito no alcanza, no se cobra nada. Revisión 20: las existencias nunca lo impiden; si no alcanzaron, después de cobrar
    /// se avisa (<see cref="SaleReceipt.StockWarning"/>) y el producto queda en "revisar inventario".
    /// </summary>
    [RelayCommand]
    public async Task ChargeAsync()
    {
        if (_isCharging) return;
        _isCharging = true;
        try
        {
            if (Cart.Count == 0)
            {
                if (EmptyChargeText != null)
                {
                    if (Completed != null) await Completed(null);
                }
                else
                {
                    Notify?.Invoke("La cuenta no tiene ningún producto.", false);
                }
                return;
            }

            if (IsCashShort)
            {
                Notify?.Invoke(CheckoutRules.MissingText(MissingAmount), false);
                return;
            }

            if (_inventoryService == null)
            {
                Notify?.Invoke("No hay base de datos para registrar la venta.", false);
                return;
            }

            // Paso 8.23 (H11): con la nube activa, una sincronización rápida antes de revisar las existencias (3 s como máximo;
            // sin red no espera). Sin conexión se cobra igual: el folio sale con la letra del equipo (8.16)
            await _inventoryService.QuickSyncAsync();
            if (Cart.Count == 0) return; // se vació mientras tanto

            var patient = string.IsNullOrWhiteSpace(PatientName) ? DefaultClient : PatientName.Trim();
            var cashier = _cashierName();
            var lines = Cart.Select(c => new SaleLineRequest(c.ProductId, c.Quantity, c.UnitPrice, c.Code, c.Name, c.IsIndividualUnits)).ToList();

            // Se registra primero: si falla (p. ej. un producto que ya no existe) no se genera recibo ni se vacía la cuenta.
            // Las existencias que no alcanzan no lo impiden (revisión 20)
            var sale = await _inventoryService.RegisterSaleAsync(lines, patient, SelectedPaymentMethod, cashier);

            var total = TotalAmount;
            var (received, change) = CheckoutRules.ReceiptCash(IsCash, CashReceived, total);
            var receipt = new SaleReceipt
            {
                Folio = sale?.Folio ?? $"TKT-{DateTime.Now:yyyyMMdd-HHmmss}",
                Date = sale?.Date ?? DateTime.Now,
                PatientName = patient,
                PaymentMethod = SelectedPaymentMethod,
                Cashier = cashier,
                CashReceived = received,
                Change = change,
                StockWarning = StockReviewRules.WarningText(sale?.StockShortages ?? Array.Empty<StockShortage>()),
                Items = Cart.Select(c => new SaleReceiptItem
                {
                    Name = c.Name,
                    Presentation = c.Presentation,
                    Quantity = c.Quantity,
                    UnitPrice = c.UnitPrice
                }).ToList()
            };

            Reset();
            if (Completed != null) await Completed(receipt);
            // Después del aviso de la venta, para que no lo tape
            if (receipt.StockWarning != null) Notify?.Invoke(receipt.StockWarning, false);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al cobrar", ex);
            Notify?.Invoke($"No se pudo cobrar: {ex.Message}", false);
            Failed?.Invoke();
        }
        finally
        {
            _isCharging = false;
        }
    }
}
