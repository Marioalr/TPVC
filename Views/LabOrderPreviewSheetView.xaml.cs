using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels;
using TPVC.ViewModels.Patients;

namespace TPVC.Views;

/// <summary>
/// Vista previa de la solicitud de laboratorio: cada página del documento que se imprime, a escala. No hay una
/// copia del acomodo en XAML: lo que se ve es lo que sale.
/// </summary>
public partial class LabOrderPreviewSheetView : UserControl
{
    private LabOrderViewModel? _labs;

    public LabOrderPreviewSheetView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach((e.NewValue as MedicalRecordsViewModel)?.Labs);
    }

    private void Attach(LabOrderViewModel? labs)
    {
        if (_labs != null) _labs.PropertyChanged -= OnLabsChanged;
        _labs = labs;
        if (_labs != null) _labs.PropertyChanged += OnLabsChanged;
        Render(_labs?.PreviewSheet);
    }

    private void OnLabsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LabOrderViewModel.PreviewSheet)) Render(_labs?.PreviewSheet);
    }

    /// <summary>Las páginas de la solicitud (una, o más si no cabe ni en carta) sobre papel blanco.</summary>
    private void Render(LabOrderSheet? sheet)
    {
        Pages.Children.Clear();
        if (sheet == null) return;

        double width = PrescriptionSheet.PageWidth;
        double height = sheet.PaperHeight;
        var doc = PrintingService.BuildLabOrderDocument(sheet, width, height);
        var paginator = ((IDocumentPaginatorSource)doc).DocumentPaginator;
        paginator.PageSize = new Size(width, height);
        paginator.ComputePageCount();

        for (int i = 0; i < paginator.PageCount; i++)
        {
            var page = new Border
            {
                Width = width,
                Height = height,
                Background = Brushes.White,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, i == 0 ? 0 : 12, 0, 0),
                Child = new DocumentPageView { DocumentPaginator = paginator, PageNumber = i }
            };
            page.SetResourceReference(Border.BorderBrushProperty, "ThemeBorder");
            Pages.Children.Add(page);
        }
    }
}
