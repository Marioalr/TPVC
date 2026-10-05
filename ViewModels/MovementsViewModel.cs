using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

public partial class MovementsViewModel : ObservableObject
{
    private readonly InventoryService _inventoryService;

    [ObservableProperty]
    private ObservableCollection<InventoryMovement> _movements = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private int _totalMovementsCount;

    public MovementsViewModel(InventoryService inventoryService)
    {
        _inventoryService = inventoryService;

        _inventoryService.OnInventoryChanged += () =>
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
                {
                    app.Dispatcher.InvokeAsync(async () => await LoadMovementsAsync());
                    return;
                }
            }
            catch
            {
                // Fallback para entornos de pruebas sin runtime de WPF Application
            }

            _ = LoadMovementsAsync();
        };
    }

    [RelayCommand]
    public async Task LoadMovementsAsync()
    {
        IsLoading = true;
        try
        {
            var list = await _inventoryService.GetRecentMovementsAsync(100);

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var s = SearchText.Trim().ToLower();
                list = list.Where(m => 
                    (m.Product != null && m.Product.Name.ToLower().Contains(s)) ||
                    (m.Reason != null && m.Reason.ToLower().Contains(s)) ||
                    (m.Responsible != null && m.Responsible.ToLower().Contains(s))
                ).ToList();
            }

            Movements = new ObservableCollection<InventoryMovement>(list);
            TotalMovementsCount = Movements.Count;
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        _ = LoadMovementsAsync();
    }
}
