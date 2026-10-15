using System.ComponentModel;
using System.Windows.Controls;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels;

namespace TPVC.Views;

/// <summary>
/// Médico y consultorio. La vista previa de la receta de ejemplo (2026-10-14) dibuja las páginas del mismo documento que
/// se imprime, en el papel que se eligió (<see cref="ClinicProfileViewModel.PreviewSheet"/>).
/// </summary>
public partial class ClinicProfileView : UserControl
{
    private ClinicProfileViewModel? _subscribed;

    public ClinicProfileView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as ClinicProfileViewModel);
    }

    private void Attach(ClinicProfileViewModel? viewModel)
    {
        if (_subscribed != null) _subscribed.PropertyChanged -= ViewModel_PropertyChanged;
        _subscribed = viewModel;
        if (_subscribed != null) _subscribed.PropertyChanged += ViewModel_PropertyChanged;
        Render(_subscribed?.PreviewSheet);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClinicProfileViewModel.PreviewSheet)) Render(_subscribed?.PreviewSheet);
    }

    /// <summary>Media carta: 816 × 528; carta: 816 × 1056 (como al imprimir en ese papel).</summary>
    private void Render(PrescriptionSheet? sheet)
    {
        if (sheet == null)
        {
            PreviewPages.Children.Clear();
            return;
        }

        double width = PrescriptionSheet.PageWidth;
        double height = sheet.PaperHeight;
        Helpers.PaperPages.Render(PreviewPages, PrintingService.BuildPrescriptionDocument(sheet, width, height), width, height);
    }
}
