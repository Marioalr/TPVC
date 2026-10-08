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

    /// <summary>Filtros como chips: todo, entradas, ventas, consumo en consulta, bajas y ajustes.</summary>
    public IReadOnlyList<string> TypeFilters { get; } = new[] { "Todos", "Entradas", "Ventas", "Consultas", "Bajas", "Ajustes" };

    [ObservableProperty]
    private string _typeFilter = "Todos";

    public string ResultsSummary => TotalMovementsCount == 1 ? "1 movimiento" : $"{TotalMovementsCount} movimientos";

    partial void OnTotalMovementsCountChanged(int value) => OnPropertyChanged(nameof(ResultsSummary));

    partial void OnTypeFilterChanged(string value) => ApplySearchFilter();

    public static bool MatchesTypeFilter(InventoryMovement movement, string? filter) => filter switch
    {
        "Entradas" => movement.MovementType is MovementType.EntradaCompra or MovementType.EntradaAjuste or MovementType.EntradaDevolucion,
        "Ventas" => movement.MovementType == MovementType.SalidaVenta,
        "Consultas" => movement.MovementType == MovementType.SalidaConsulta,
        "Bajas" => movement.MovementType is MovementType.SalidaCaducidad or MovementType.SalidaMerma,
        "Ajustes" => movement.MovementType == MovementType.AjusteInventario,
        _ => true
    };

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

    private List<InventoryMovement> _loadedMovements = new();

    [RelayCommand]
    public async Task LoadMovementsAsync()
    {
        IsLoading = true;
        try
        {
            _loadedMovements = await _inventoryService.GetRecentMovementsAsync(MovementsPageSize);
            ApplySearchFilter();
        }
        finally
        {
            IsLoading = false;
        }
    }

    public const int MovementsPageSize = 200;

    /// <summary>
    /// La búsqueda filtra en memoria los movimientos ya cargados (sin consultar la base por tecla).
    /// </summary>
    private void ApplySearchFilter()
    {
        IEnumerable<InventoryMovement> list = _loadedMovements.Where(m => MatchesTypeFilter(m, TypeFilter));
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = TextSearch.Normalize(SearchText);
            list = list.Where(m => TextSearch.Matches(q, m.Product?.Name, m.Product?.Code, m.Reason, m.Responsible, m.MovementTypeDisplay));
        }

        Movements = new ObservableCollection<InventoryMovement>(list);
        TotalMovementsCount = Movements.Count;
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplySearchFilter();
    }
}
