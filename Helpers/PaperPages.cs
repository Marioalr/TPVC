using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace TPVC.Helpers;

/// <summary>
/// Las páginas de un documento impreso (receta o solicitud de laboratorio) a escala, cada una sobre papel blanco con
/// cualquier tema: lo que se ve es el mismo documento que sale de la impresora, sin una copia del acomodo en XAML. Lo
/// usan la vista previa de la solicitud de laboratorio (v1.25.0) y la de la receta de ejemplo en "Médico y consultorio"
/// (2026-10-14).
/// </summary>
public static class PaperPages
{
    public static void Render(Panel pages, FlowDocument? document, double width, double height)
    {
        pages.Children.Clear();
        if (document == null) return;

        var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
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
            pages.Children.Add(page);
        }
    }
}
