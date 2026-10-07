using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace TPVC.Helpers;

/// <summary>
/// Icono de trazo (juego único de la interfaz). Dibuja una geometría pensada en una cuadrícula de
/// 24×24 escalada al tamaño del control, con el color del texto que lo rodea: dentro de un botón
/// toma su Foreground sin configurar nada. Es un solo elemento ligero (sin Viewbox ni Canvas).
/// Uso: &lt;helpers:Icon Data="{StaticResource IcoSearch}" Width="16" Height="16" /&gt;
/// </summary>
public sealed class Icon : FrameworkElement
{
    private const double GridSize = 24.0;

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(Icon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Icon),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Rellenar en vez de trazar (para figuras sólidas como un punto).</summary>
    public static readonly DependencyProperty IsFilledProperty = DependencyProperty.Register(
        nameof(IsFilled), typeof(bool), typeof(Icon),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public bool IsFilled
    {
        get => (bool)GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }

    static Icon()
    {
        WidthProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(16.0));
        HeightProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(16.0));
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(true));
        FocusableProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var geometry = Data;
        if (geometry == null || ActualWidth <= 0 || ActualHeight <= 0) return;

        var scale = Math.Min(ActualWidth, ActualHeight) / GridSize;
        drawingContext.PushTransform(new TranslateTransform((ActualWidth - GridSize * scale) / 2, (ActualHeight - GridSize * scale) / 2));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));

        if (IsFilled)
        {
            drawingContext.DrawGeometry(Foreground, null, geometry);
        }
        else
        {
            var pen = new Pen(Foreground, StrokeThickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();
            drawingContext.DrawGeometry(null, pen, geometry);
        }

        drawingContext.Pop();
        drawingContext.Pop();
    }
}
