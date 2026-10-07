using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using TPVC.Services;

namespace TPVC.Helpers;

/// <summary>
/// Convierte un ViewModel en su vista (TPVC.ViewModels.XViewModel → TPVC.Views.XView) y reutiliza
/// la misma instancia en cada navegación. Con DataTemplates, WPF reconstruía vistas enormes
/// (Expedientes: ~6,900 líneas de XAML) cada vez que se entraba a ellas, lo que hacía lenta la
/// navegación y acumulaba instancias en memoria.
/// </summary>
public sealed class CachedViewConverter : IValueConverter
{
    private static readonly ConditionalWeakTable<object, FrameworkElement> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || value is FrameworkElement) return value;
        return Cache.GetValue(value, CreateView);
    }

    private static FrameworkElement CreateView(object viewModel)
    {
        var vmType = viewModel.GetType();
        var viewTypeName = vmType.FullName!
            .Replace(".ViewModels.", ".Views.")
            .Replace("ViewModel", "View");
        var viewType = vmType.Assembly.GetType(viewTypeName);

        if (viewType == null || Activator.CreateInstance(viewType) is not FrameworkElement view)
        {
            AppLogger.Error($"No existe la vista {viewTypeName} para {vmType.Name}");
            return new System.Windows.Controls.TextBlock { Text = $"Vista no disponible: {vmType.Name}" };
        }

        view.DataContext = viewModel;
        return view;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
