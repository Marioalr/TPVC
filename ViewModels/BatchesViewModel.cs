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

    public BatchesViewModel(InventoryService inventoryService)
    {
        _inventoryService = inventoryService;

        _inventoryService.OnInventoryChanged += () =>
        {
            System.Windows.Application.Current?.Dispatcher?.Invoke(() => _ = LoadBatchesAsync());
        };
    }

    [RelayCommand]
    public async Task LoadBatchesAsync()
    {
        IsLoading = true;
        try
        {
            var list = await _inventoryService.GetBatchesAsync();

            if (SelectedFilter == "Proximos")
            {
                list = list.Where(b => !b.IsDepleted && b.Status == ExpirationStatus.ProximoAVencer).ToList();
            }
            else if (SelectedFilter == "Vencidos")
            {
                list = list.Where(b => !b.IsDepleted && b.Status == ExpirationStatus.Vencido).ToList();
            }
            else if (SelectedFilter == "Vigentes")
            {
                list = list.Where(b => !b.IsDepleted && b.Status == ExpirationStatus.Vigente).ToList();
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
        NewBatchNumber = $"LOT-{DateTime.Now:yyyyMMdd}-{new Random().Next(10, 99)}";
        NewBatchExpirationDate = DateTime.Today.AddMonths(12);
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

        await _inventoryService.SaveBatchAsync(batch);

        // Sumar stock al producto automáticamente
        await _inventoryService.RegisterMovementAsync(new InventoryMovement
        {
            ProductId = NewBatchSelectedProduct.Id,
            BatchId = batch.Id,
            MovementType = MovementType.EntradaCompra,
            Quantity = NewBatchQuantity,
            Reason = $"Entrada de lote {batch.BatchNumber}",
            Responsible = "Dr. Consulta"
        });

        IsNewBatchModalOpen = false;
        await LoadBatchesAsync();
    }

    [RelayCommand]
    public async Task DeleteBatchAsync(ProductBatch? batch)
    {
        if (batch == null) return;
        await _inventoryService.DeleteBatchAsync(batch.Id);
        await LoadBatchesAsync();
    }
}
