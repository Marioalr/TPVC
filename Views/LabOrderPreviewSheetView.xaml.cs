using System.ComponentModel;
using System.Windows.Controls;
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
        if (sheet == null)
        {
            Pages.Children.Clear();
            return;
        }

        double width = PrescriptionSheet.PageWidth;
        double height = sheet.PaperHeight;
        Helpers.PaperPages.Render(Pages, PrintingService.BuildLabOrderDocument(sheet, width, height), width, height);
    }
}
