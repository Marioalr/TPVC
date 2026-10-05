using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Threading;
using TPVC.ViewModels;

namespace TPVC.Views;

public partial class MedicalRecordsView : UserControl
{
    public MedicalRecordsView()
    {
        InitializeComponent();

        Loaded += (s, e) =>
        {
            if (DataContext is MedicalRecordsViewModel vm && vm.SelectedPatient == null && !vm.IsSelectPatientModalOpen)
            {
                vm.OpenSelectPatientModal();
            }
        };

        DataContextChanged += (s, e) =>
        {
            if (e.OldValue is MedicalRecordsViewModel oldVm)
            {
                oldVm.PropertyChanged -= ViewModel_PropertyChanged;
            }
            if (e.NewValue is MedicalRecordsViewModel newVm)
            {
                newVm.PropertyChanged += ViewModel_PropertyChanged;
            }
        };
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MedicalRecordsViewModel.IsSelectPatientModalOpen))
        {
            if (sender is MedicalRecordsViewModel { IsSelectPatientModalOpen: true })
            {
                Dispatcher.InvokeAsync(() =>
                {
                    SelectPatientSearchTextBox?.Focus();
                    SelectPatientSearchTextBox?.SelectAll();
                }, DispatcherPriority.Input);
            }
        }
    }
}
