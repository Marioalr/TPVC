using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using TPVC.Data;

namespace TPVC.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void OpenDbFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.GetDirectoryName(AppDbContext.DbPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // Silencioso si falla explorador de archivos
        }
    }
}
