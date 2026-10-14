using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

/// <summary>Chip de filtro con su conteo ("Por vencer · 3").</summary>
public sealed record BatchFilterChip(string Key, string Text);

public partial class BatchesViewModel : ObservableObject
{
    private static readonly System.Globalization.CultureInfo Mexican = System.Globalization.CultureInfo.GetCultureInfo("es-MX");
    private static readonly string[] DateFormats = { "d/M/yyyy", "dd/MM/yyyy", "d-M-yyyy", "dd-MM-yyyy", "yyyy-MM-dd", "d/M/yy", "dd/MM/yy", "MM/yyyy", "M/yyyy" };

    [ObservableProperty]
    private ObservableCollection<BatchFilterChip> _filterChips = new();

    [ObservableProperty]
    private BatchFilterChip? _selectedFilterChip;

    partial void OnSelectedFilterChipChanged(BatchFilterChip? value)
    {
        if (value != null && value.Key != SelectedFilter) SelectedFilter = value.Key;
    }

    private void RebuildFilterChips()
    {
        FilterChips = new ObservableCollection<BatchFilterChip>(new[]
        {
            new BatchFilterChip("Todos", $"Todos · {CountTodos}"),
            new BatchFilterChip("Proximos", $"Por vencer · {CountProximos}"),
            new BatchFilterChip("Vencidos", $"Vencidos · {CountVencidos}"),
            new BatchFilterChip("Vigentes", $"Vigentes · {CountVigentes}")
        });
        SelectedFilterChip = FilterChips.FirstOrDefault(c => c.Key == SelectedFilter);
    }

    /// <summary>Caducidad como se escribe (dd/mm/aaaa o mm/aaaa, que toma el último día del mes).</summary>
    [ObservableProperty]
    private string _newBatchExpirationText = string.Empty;

    private bool _syncingExpirationText;

    partial void OnNewBatchExpirationTextChanged(string value)
    {
        if (_syncingExpirationText) return;
        var parsed = ParseExpiration(value);
        if (parsed.HasValue)
        {
            _syncingExpirationText = true;
            NewBatchExpirationDate = parsed.Value;
            _syncingExpirationText = false;
        }
        else
        {
            IsExpirationWarningVisible = !string.IsNullOrWhiteSpace(value);
            ExpirationWarningMessage = "Escriba la fecha como dd/mm/aaaa (o mm/aaaa).";
        }
    }

    /// <summary>"31/12/2027" → esa fecha; "12/2027" → último día de diciembre de 2027 (como en la caja).</summary>
    public static DateTime? ParseExpiration(string? text)
    {
        var clean = text?.Trim();
        if (string.IsNullOrEmpty(clean)) return null;
        if (!DateTime.TryParseExact(clean, DateFormats, Mexican, System.Globalization.DateTimeStyles.None, out var date)) return null;
        bool monthOnly = clean.Count(c => c == '/' || c == '-') == 1;
        return monthOnly ? new DateTime(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month)) : date.Date;
    }

    [RelayCommand]
    public void AddMonthsToExpiration(string? months)
    {
        if (int.TryParse(months, out var m)) NewBatchExpirationDate = DateTime.Today.AddMonths(m);
    }
    private readonly InventoryService _inventoryService;

    [ObservableProperty]
    private ObservableCollection<ProductBatch> _batches = new();

    [ObservableProperty]
    private ObservableCollection<Product> _availableProducts = new();

    [ObservableProperty]
    private string _selectedFilter = "Todos"; // Todos, Proximos, Vencidos, Vigentes

    [ObservableProperty]
    private int _countTodos;

    [ObservableProperty]
    private int _countProximos;

    [ObservableProperty]
    private int _countVencidos;

    [ObservableProperty]
    private int _countVigentes;

    // Nuevo Lote Modal State
    [ObservableProperty]
    private bool _isNewBatchModalOpen;

    [ObservableProperty]
    private Product? _newBatchSelectedProduct;

    [ObservableProperty]
    private string _newBatchNumber = string.Empty;

    [ObservableProperty]
    private DateTime _newBatchExpirationDate = DateTime.Today.AddMonths(12);

    [ObservableProperty]
    private int _newBatchQuantity = 10;

    [ObservableProperty]
    private string _newBatchNotes = string.Empty;

    [ObservableProperty]
    private bool _isExpirationWarningVisible;

    [ObservableProperty]
    private string? _newBatchError;

    [ObservableProperty]
    private string _expirationWarningMessage = string.Empty;

    public BatchesViewModel(InventoryService inventoryService)
    {
        _inventoryService = inventoryService;

        _inventoryService.OnInventoryChanged += () =>
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
                {
                    app.Dispatcher.InvokeAsync(async () => await LoadBatchesAsync());
                    return;
                }
            }
            catch
            {
                // Fallback para entornos de pruebas sin runtime de WPF Application
            }

            _ = LoadBatchesAsync();
        };
    }

    partial void OnNewBatchExpirationDateChanged(DateTime value)
    {
        ValidateExpirationDate(value);
        if (!_syncingExpirationText)
        {
            _syncingExpirationText = true;
            NewBatchExpirationText = value.ToString("dd/MM/yyyy", Mexican);
            _syncingExpirationText = false;
        }
    }

    private void ValidateExpirationDate(DateTime value)
    {
        if (value.Date < DateTime.Today)
        {
            IsExpirationWarningVisible = true;
            ExpirationWarningMessage = "Esa fecha ya pasó: el lote entraría vencido.";
        }
        else if ((value.Date - DateTime.Today).TotalDays <= 30)
        {
            int days = (int)(value.Date - DateTime.Today).TotalDays;
            IsExpirationWarningVisible = true;
            ExpirationWarningMessage = $"Este lote vence en {days} días ({value:dd/MM/yyyy}). Revise que la fecha sea correcta.";
        }
        else
        {
            IsExpirationWarningVisible = false;
            ExpirationWarningMessage = string.Empty;
        }
    }

    public async Task LoadBatchesAsync()
    {
        var allBatches = await _inventoryService.GetBatchesAsync();
        // Ni agotados ni cerrados (paso 8.19: «Eliminar lote» lo cierra)
        var active = allBatches.Where(b => b.IsAvailable).ToList();

        CountTodos = active.Count;
        CountProximos = active.Count(b => b.Status == ExpirationStatus.ProximoAVencer);
        CountVencidos = active.Count(b => b.Status == ExpirationStatus.Vencido);
        CountVigentes = active.Count(b => b.Status == ExpirationStatus.Vigente);

        var list = active;
        if (SelectedFilter == "Proximos")
        {
            list = active.Where(b => b.Status == ExpirationStatus.ProximoAVencer).ToList();
        }
        else if (SelectedFilter == "Vencidos")
        {
            list = active.Where(b => b.Status == ExpirationStatus.Vencido).ToList();
        }
        else if (SelectedFilter == "Vigentes")
        {
            list = active.Where(b => b.Status == ExpirationStatus.Vigente).ToList();
        }

        Batches = new ObservableCollection<ProductBatch>(list.OrderBy(b => b.ExpirationDate));
        RebuildFilterChips();

        var products = await _inventoryService.GetProductsAsync();
        AvailableProducts = new ObservableCollection<Product>(products);
        if (NewBatchSelectedProduct == null && AvailableProducts.Count > 0)
        {
            NewBatchSelectedProduct = AvailableProducts[0];
        }
    }

    partial void OnSelectedFilterChanged(string value)
    {
        _ = LoadBatchesAsync();
    }

    [RelayCommand]
    public void OpenNewBatchModal()
    {
        NewBatchNumber = $"LOT-{DateTime.Now:yyyyMMdd}-{Random.Shared.Next(10, 99)}";
        NewBatchExpirationDate = DateTime.Today.AddMonths(12);
        NewBatchExpirationText = NewBatchExpirationDate.ToString("dd/MM/yyyy", Mexican);
        ValidateExpirationDate(NewBatchExpirationDate);
        NewBatchQuantity = 10;
        NewBatchNotes = string.Empty;
        IsNewBatchModalOpen = true;
    }

    [RelayCommand]
    public void CloseNewBatchModal()
    {
        IsNewBatchModalOpen = false;
    }

    [RelayCommand]
    public async Task SaveNewBatchAsync()
    {
        if (NewBatchSelectedProduct == null || string.IsNullOrWhiteSpace(NewBatchNumber) || NewBatchQuantity <= 0)
        {
            NewBatchError = "Elija el producto y escriba el número de lote y la cantidad.";
            return;
        }
        if (ParseExpiration(NewBatchExpirationText) == null)
        {
            NewBatchError = "Escriba la caducidad como dd/mm/aaaa (o mm/aaaa).";
            return;
        }
        NewBatchError = null;

        var batch = new ProductBatch
        {
            ProductId = NewBatchSelectedProduct.Id,
            BatchNumber = NewBatchNumber.Trim(),
            ExpirationDate = NewBatchExpirationDate,
            Quantity = NewBatchQuantity,
            ReceivedDate = DateTime.Now,
            Notes = NewBatchNotes
        };

        try
        {
            // Lote + suma de existencias + Kardex en una sola transacción
            await _inventoryService.ReceiveBatchAsync(batch);
            IsNewBatchModalOpen = false;
            await LoadBatchesAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al registrar lote", ex);
            DialogService.ShowError($"No se pudo registrar el lote:\n{ex.Message}");
        }
    }

    [RelayCommand]
    public async Task DeleteBatchAsync(ProductBatch? batch)
    {
        if (batch == null) return;

        var remaining = batch.IsAvailable && batch.Quantity > 0
            ? $"\n\nSe descontarán {batch.Quantity} pieza(s) restantes de las existencias de '{batch.Product?.Name}'."
            : string.Empty;
        if (!DialogService.Confirm($"¿Eliminar el lote {batch.BatchNumber}?{remaining}\n\nEsta acción no se puede deshacer.", "Eliminar lote"))
        {
            return;
        }

        try
        {
            await _inventoryService.DeleteBatchAsync(batch.Id);
            await LoadBatchesAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al eliminar lote", ex);
            DialogService.ShowError($"No se pudo eliminar el lote:\n{ex.Message}");
        }
    }
}
