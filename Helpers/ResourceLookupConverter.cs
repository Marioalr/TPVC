using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TPVC.Helpers;

/// <summary>Devuelve el recurso de la aplicación con la clave dada (muestras de colores e iconos).</summary>
public sealed class ResourceLookupConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? Application.Current?.TryFindResource(key) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
