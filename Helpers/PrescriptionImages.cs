using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TPVC.Models;

namespace TPVC.Helpers;

/// <summary>
/// Imágenes de la receta para WPF (impresión y vista previa): los iconos fijos del pie y las del
/// membrete que eligió el médico. Se cargan completas en memoria (el archivo no queda abierto, así se
/// puede reemplazar o borrar) y congeladas (se pueden usar en el hilo de impresión).
/// </summary>
public static class PrescriptionImages
{
    private static readonly Lazy<ImageSource?> WhatsAppIcon = new(() => FromBytes(PrescriptionSheet.WhatsAppIconPng));
    private static readonly Lazy<ImageSource?> LocationIcon = new(() => FromBytes(PrescriptionSheet.LocationIconPng));

    /// <summary>Icono de WhatsApp (pie de la receta, junto al teléfono).</summary>
    public static ImageSource? WhatsApp => WhatsAppIcon.Value;

    /// <summary>Icono de ubicación (pie de la receta, junto al domicilio).</summary>
    public static ImageSource? Location => LocationIcon.Value;

    /// <summary>Imagen de un archivo del membrete, o null si no hay o no se puede leer.</summary>
    public static ImageSource? FromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            return FromBytes(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ImageSource? FromBytes(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes, writable: false);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Ruta de una imagen del membrete → imagen (null sin imagen, sin errores de binding).</summary>
public sealed class ImageFileConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        PrescriptionImages.FromFile(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Texto de signos vitales del expediente → el signo de la receta con sus unidades (ConverterParameter:
/// TA, Temp, Peso, Talla o SpO2), igual que en la impresión. Vacío si no se registró.
/// </summary>
public sealed class PrescriptionVitalConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        PrescriptionSheet.VitalSign(value as string, parameter as string ?? string.Empty);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
