using System;
using System.Windows;
using System.Windows.Input;

namespace TPVC.Installer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        View1_Welcome.Visibility = Visibility.Collapsed;
        View2_Progress.Visibility = Visibility.Visible;
        View3_Finish.Visibility = Visibility.Collapsed;

        var progress = new Progress<InstallProgressReport>(report =>
        {
            ProgressBarInstall.Value = report.Percentage;
            TxtPercentage.Text = $"{report.Percentage}%";
            TxtStatus.Text = report.Status;
        });

        try
        {
            await InstallerEngine.InstallAsync(progress);

            View2_Progress.Visibility = Visibility.Collapsed;
            View3_Finish.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Ocurrió un inconveniente durante la instalación:\n\n{ex.Message}", 
                "Error en la Instalación", 
                MessageBoxButton.OK, 
                MessageBoxImage.Error);

            View2_Progress.Visibility = Visibility.Collapsed;
            View1_Welcome.Visibility = Visibility.Visible;
        }
    }

    private void FinishButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChkLaunchApp.IsChecked == true)
        {
            InstallerEngine.LaunchInstalledApp();
        }

        Close();
    }
}
