using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TPVC.Models;

namespace TPVC.Converters;

using System.Collections;

public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = false;

        if (value is bool boolVal)
        {
            b = boolVal;
        }
        else if (value is int intVal)
        {
            b = intVal > 0;
        }
        else if (value is long longVal)
        {
            b = longVal > 0;
        }
        else if (value is double doubleVal)
        {
            b = doubleVal > 0;
        }
        else if (value is decimal decVal)
        {
            b = decVal > 0;
        }
        else if (value is string strVal)
        {
            b = !string.IsNullOrWhiteSpace(strVal);
        }
        else if (value is ICollection coll)
        {
            b = coll.Count > 0;
        }
        else if (value != null)
        {
            b = true;
        }

        if (Invert)
        {
            b = !b;
        }

        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b) return !b;
        if (value is int i) return i <= 0;
        return value == null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b) return !b;
        return false;
    }
}

public class ComparisonToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

public class ComparisonToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) 
            return Invert ? Visibility.Visible : Visibility.Collapsed;

        bool matches = string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
        if (Invert) matches = !matches;

        return matches ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

