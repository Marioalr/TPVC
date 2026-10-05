using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

public partial class BatchesViewModel : ObservableObject
{
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

    [ObservableProperty]
    private bool _isLoading;

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

    [RelayCommand]
    public void SetFilter(string filter)
    {
        SelectedFilter = filter;
    }

    partial void OnNewBatchExpirationDateChanged(DateTime value)
    {
        ValidateExpirationDate(value);
    }

    private void ValidateExpirationDate(DateTime value)
    {
        if (value.Date < DateTime.Today)
        {
            IsExpirationWarningVisible = true;
            ExpirationWarningMessage = "⚠️ Atención: La fecha de caducidad ingresada ya venció.";
        }
        else if ((value.Date - DateTime.Today).TotalDays <= 30)
        {
            int days = (int)(value.Date - DateTime.Today).TotalDays;
            IsExpirationWarningVisible = true;
            ExpirationWarningMessage = $"⚠️ Advertencia: Este lote vencerá en {days} días ({value:dd/MM/yyyy}). Confirme que la fecha sea correcta.";
        }
        else
        {
            IsExpirationWarningVisible = false;
            ExpirationWarningMessage = string.Empty;
        }
    }

    [RelayCommand]
    public async Task LoadBatchesAsync()
    {
        IsLoading = true;
        try
        {
            var allBatches = await _inventoryService.GetBatchesAsync();
            var active = allBatches.Where(b => !b.IsDepleted).ToList();

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

            Batches = new ObservableCollection<ProductBatch>(list);

            var products = await _inventoryService.GetProductsAsync();
            AvailableProducts = new ObservableCollection<Product>(products);
            if (NewBatchSelectedProduct == null && AvailableProducts.Count > 0)
            {
                NewBatchSelectedProduct = AvailableProducts[0];
            }
        }
        finally
        {
            IsLoading = false;
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
            return;
        }

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

        var remaining = !batch.IsDepleted && batch.Quantity > 0
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
