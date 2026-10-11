using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

/// <summary>Una opción de «Producto en TPVC»: un producto del catálogo o crear uno nuevo con el renglón.</summary>
public sealed record SupplierProductChoice(int? ProductId, string Label)
{
    public static readonly SupplierProductChoice CreateNew = new(null, "➕ Producto nuevo");
    public override string ToString() => Label;
}

/// <summary>Un renglón del pedido en revisión: lo que dice el correo y lo que se va a ingresar (todo editable).</summary>
public partial class SupplierOrderLineViewModel : ObservableObject
{
    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");

    public SupplierOrderLineViewModel(SupplierLineSuggestion suggestion, SupplierProductChoice choice)
    {
        Description = suggestion.Line.Description;
        Sku = suggestion.Line.Sku;
        EmailLineTotal = suggestion.Line.LineTotal;
        Match = suggestion.Match;
        _quantity = suggestion.Line.Quantity;
        _unitCost = suggestion.Line.UnitCost;
        _selectedProduct = choice;
    }

    public string Description { get; }
    public string Sku { get; }
    public decimal EmailLineTotal { get; }
    public SupplierLineMatch Match { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineTotal), nameof(LineTotalText), nameof(Error))]
    private int _quantity;

    /// <summary>Precargado con el total del renglón entre la cantidad; se puede corregir a mano.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineTotal), nameof(LineTotalText), nameof(Error))]
    private decimal _unitCost;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    private string _batchNumber = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    private string _expirationText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewProduct), nameof(MatchText), nameof(NeedsReview))]
    private SupplierProductChoice _selectedProduct;

    public bool IsNewProduct => SelectedProduct.ProductId == null;

    /// <summary>Un producto parecido elegido solo: conviene revisarlo antes de confirmar.</summary>
    public bool NeedsReview => Match == SupplierLineMatch.Similar && !_productChangedByUser;

    private bool _productChangedByUser;

    partial void OnSelectedProductChanged(SupplierProductChoice value) => _productChangedByUser = true;

    /// <summary>Por qué se eligió ese producto, en palabras.</summary>
    public string MatchText => IsNewProduct
        ? "Se crea al ingresar"
        : _productChangedByUser
            ? "Elegido a mano"
            : Match switch
            {
                SupplierLineMatch.SupplierCode => "Llegó en un pedido anterior",
                SupplierLineMatch.ProductCode => "Mismo código",
                SupplierLineMatch.SameName => "Mismo nombre",
                SupplierLineMatch.Similar => "Parecido: revise",
                _ => string.Empty
            };

    public decimal LineTotal => Quantity * UnitCost;
    public string LineTotalText => LineTotal.ToString("C2", Mexican);

    public DateTime? Expiration => BatchesViewModel.ParseExpiration(ExpirationText);

    /// <summary>Lo que impide ingresar el renglón; vacío si está bien.</summary>
    public string Error
    {
        get
        {
            if (Quantity <= 0) return "La cantidad debe ser mayor a cero.";
            if (UnitCost < 0) return "El costo no puede ser negativo.";
            bool hasBatch = !string.IsNullOrWhiteSpace(BatchNumber), hasExpiration = !string.IsNullOrWhiteSpace(ExpirationText);
            if (hasBatch && !hasExpiration) return "Falta la caducidad del lote.";
            if (hasExpiration && !hasBatch) return "Falta el número de lote.";
            if (hasExpiration && Expiration is not { } date) return "Escriba la caducidad como dd/mm/aaaa o mm/aaaa.";
            if (hasExpiration && Expiration < DateTime.Today) return "La caducidad ya pasó.";
            return string.Empty;
        }
    }

    public SupplierReceiptLine ToReceiptLine() => new()
    {
        ProductId = SelectedProduct.ProductId,
        Sku = Sku,
        Description = Description,
        Quantity = Quantity,
        UnitCost = UnitCost,
        BatchNumber = string.IsNullOrWhiteSpace(BatchNumber) ? null : BatchNumber.Trim(),
        ExpirationDate = string.IsNullOrWhiteSpace(BatchNumber) ? null : Expiration
    };
}

/// <summary>
/// «Ingresar pedido»: se pega el correo del proveedor, se revisa cada renglón (producto, cantidad, costo, lote y
/// caducidad) y se ingresa todo junto. Primera versión: el formato del correo de LUIN, un proveedor a la vez.
/// </summary>
public partial class SupplierOrderViewModel : ObservableObject
{
    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");

    private readonly InventoryService _inventory;
    private readonly LocalPreferencesStore? _preferences;
    private SupplierOrder? _order;

    public SupplierOrderViewModel(InventoryService inventory, LocalPreferencesStore? preferences = null)
    {
        _inventory = inventory;
        _preferences = preferences;
        _supplier = preferences?.Current.LastSupplierName ?? "LUIN";
    }

    [ObservableProperty]
    private string _pastedText = string.Empty;

    [ObservableProperty]
    private string _supplier;

    /// <summary>false: pegar el correo; true: revisar los renglones.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPasting))]
    private bool _isReviewing;

    public bool IsPasting => !IsReviewing;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _title = "Ingresar pedido";

    /// <summary>"23 productos · 18 del catálogo · 5 nuevos".</summary>
    [ObservableProperty]
    private string _summaryText = string.Empty;

    /// <summary>La suma de los renglones contra el subtotal del correo.</summary>
    [ObservableProperty]
    private string _totalCheckText = string.Empty;

    [ObservableProperty]
    private bool _isTotalCheckOk;

    /// <summary>"Este pedido ya se ingresó el …"; vacío si no.</summary>
    [ObservableProperty]
    private string _duplicateWarning = string.Empty;

    [ObservableProperty]
    private ObservableCollection<Category> _categories = new();

    [ObservableProperty]
    private Category? _categoryForNew;

    [ObservableProperty]
    private bool _hasNewProducts;

    public ObservableCollection<SupplierOrderLineViewModel> Lines { get; } = new();

    /// <summary>"Producto nuevo" y los productos del catálogo, por nombre (la misma lista para cada renglón).</summary>
    [ObservableProperty]
    private IReadOnlyList<SupplierProductChoice> _productChoices = Array.Empty<SupplierProductChoice>();

    public string LinesTotalText => Lines.Sum(l => l.LineTotal).ToString("C2", Mexican);

    public bool CanConfirm => IsReviewing && !IsBusy && Lines.Count > 0 && Lines.All(l => l.Error.Length == 0);

    /// <summary>Se cerró la hoja (con o sin ingresar).</summary>
    public event Action? Closed;

    /// <summary>Se ingresó el pedido: el aviso de lo que quedó.</summary>
    public event Action<string>? Received;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanConfirm));
    partial void OnIsReviewingChanged(bool value) => OnPropertyChanged(nameof(CanConfirm));

    [RelayCommand]
    public void PasteFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsText()) PastedText = Clipboard.GetText();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"No se pudo leer el portapapeles: {ex.Message}");
        }
    }

    /// <summary>Lee el correo pegado y empareja cada renglón con el catálogo.</summary>
    [RelayCommand]
    public async Task ReadOrderAsync()
    {
        ErrorMessage = string.Empty;
        var order = SupplierOrderParser.Parse(PastedText);
        if (order.Lines.Count == 0)
        {
            ErrorMessage = "No se encontraron productos en el texto. Copie del correo la tabla de «Detalles de la Compra» " +
                           "(Producto, SKU, Cantidad y Total) y vuelva a pegarla.";
            return;
        }

        IsBusy = true;
        try
        {
            _order = order;
            var suggestions = await _inventory.MatchSupplierOrderAsync(order.Lines);
            var products = (await _inventory.GetProductsAsync()).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var choices = new List<SupplierProductChoice> { SupplierProductChoice.CreateNew };
            choices.AddRange(products.Select(p => new SupplierProductChoice(p.Id, $"{p.Name} · {p.Code}")));
            // Un producto dado de baja que llegó en el pedido también se puede elegir
            foreach (var s in suggestions.Where(s => s.Product != null && choices.All(c => c.ProductId != s.Product.Id)))
            {
                choices.Add(new SupplierProductChoice(s.Product!.Id, $"{s.Product.Name} · {s.Product.Code} (dado de baja)"));
            }
            ProductChoices = choices;

            foreach (var line in Lines) line.PropertyChanged -= OnLineChanged;
            Lines.Clear();
            foreach (var s in suggestions)
            {
                var choice = s.Product == null ? SupplierProductChoice.CreateNew : choices.First(c => c.ProductId == s.Product.Id);
                var line = new SupplierOrderLineViewModel(s, choice);
                line.PropertyChanged += OnLineChanged;
                Lines.Add(line);
            }

            var categories = await _inventory.GetCategoriesAsync();
            Categories = new ObservableCollection<Category>(categories);
            CategoryForNew ??= categories.FirstOrDefault(c => c.Name.Contains("Medicamentos", StringComparison.OrdinalIgnoreCase)) ?? categories.FirstOrDefault();

            Title = string.IsNullOrWhiteSpace(order.OrderNumber) ? "Pedido" : $"Pedido #{order.OrderNumber}";
            if (!string.IsNullOrWhiteSpace(Supplier)) Title += $" · {Supplier.Trim()}";
            DuplicateWarning = await _inventory.WhenWasSupplierOrderReceivedAsync(Supplier, order.OrderNumber) is { } when
                ? $"Este pedido ya se ingresó el {when.ToString("dd/MM/yyyy 'a las' HH:mm", Mexican)}. Ingresarlo otra vez sumaría las existencias de nuevo."
                : string.Empty;

            RefreshTotals();
            IsReviewing = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnLineChanged(object? sender, PropertyChangedEventArgs e) => RefreshTotals();

    private void RefreshTotals()
    {
        int newCount = Lines.Count(l => l.IsNewProduct);
        HasNewProducts = newCount > 0;
        SummaryText = $"{Lines.Count} productos · {Lines.Count - newCount} del catálogo · {newCount} nuevos";

        // Lo que dice el correo contra lo que leímos (sin las correcciones a mano: eso compara que se leyó todo)
        if (_order?.MatchesEmailTotal is { } matches)
        {
            var expected = (_order.Subtotal ?? _order.Total)!.Value;
            IsTotalCheckOk = matches;
            TotalCheckText = matches
                ? $"Los {Lines.Count} renglones suman {expected.ToString("C2", Mexican)}, igual que el correo."
                : $"Los renglones leídos suman {_order.LinesTotal.ToString("C2", Mexican)} y el correo dice {expected.ToString("C2", Mexican)}: " +
                  "revise que estén todos los productos.";
        }
        else
        {
            IsTotalCheckOk = false;
            TotalCheckText = "El texto no trae el subtotal del correo: revise que estén todos los productos.";
        }

        OnPropertyChanged(nameof(LinesTotalText));
        OnPropertyChanged(nameof(CanConfirm));
    }

    [RelayCommand]
    public void BackToPaste()
    {
        IsReviewing = false;
        ErrorMessage = string.Empty;
    }

    /// <summary>Ingresa el pedido: productos nuevos, costos, existencias, lotes y kardex, todo junto.</summary>
    [RelayCommand]
    public async Task ConfirmAsync()
    {
        ErrorMessage = string.Empty;
        if (!CanConfirm)
        {
            ErrorMessage = Lines.FirstOrDefault(l => l.Error.Length > 0) is { } bad ? $"«{bad.Description}»: {bad.Error}" : string.Empty;
            return;
        }
        if (HasNewProducts && CategoryForNew == null)
        {
            ErrorMessage = "Elija la categoría de los productos nuevos.";
            return;
        }
        if (DuplicateWarning.Length > 0 && !DialogService.Confirm(DuplicateWarning + "\n\n¿Ingresarlo de todos modos?", "TPVC - Pedido ya ingresado"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _inventory.ReceiveSupplierOrderAsync(new SupplierReceipt
            {
                Supplier = Supplier.Trim(),
                OrderNumber = _order?.OrderNumber,
                CategoryIdForNewProducts = CategoryForNew?.Id ?? 0,
                Lines = Lines.Select(l => l.ToReceiptLine()).ToList()
            });
            if (_preferences != null && !string.IsNullOrWhiteSpace(Supplier))
            {
                _preferences.Current.LastSupplierName = Supplier.Trim();
                _preferences.Save();
            }

            var message = $"{Title}: {result.Received} productos ingresados";
            if (result.CreatedWithoutPrice.Count > 0)
                message += $"; {result.CreatedWithoutPrice.Count} nuevos sin precio de venta (complételos en Productos)";
            Received?.Invoke(message + ".");
            Close();
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo ingresar el pedido del proveedor", ex);
            ErrorMessage = $"No se ingresó nada: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void Close()
    {
        foreach (var line in Lines) line.PropertyChanged -= OnLineChanged;
        Closed?.Invoke();
    }
}
