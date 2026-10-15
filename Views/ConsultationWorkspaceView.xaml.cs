using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TPVC.Models;
using TPVC.ViewModels;
using TPVC.ViewModels.Patients;

namespace TPVC.Views;

public partial class ConsultationWorkspaceView : UserControl
{
    public ConsultationWorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => AttachToViewModel(e.NewValue as MedicalRecordsViewModel);
    }

    private void ConsultationWorkspace_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || DataContext is not MedicalRecordsViewModel vm) return;

        ICommand? command = e.Key switch
        {
            Key.Down => vm.Consultation.NextConsultationSectionCommand,
            Key.Up => vm.Consultation.PreviousConsultationSectionCommand,
            Key.Enter => vm.Consultation.FinalizeConsultationCommand,
            Key.S => vm.Autosave.SaveConsultationDraftNowCommand,
            _ => null
        };
        if (command == null) return;

        e.Handled = true;
        if (command.CanExecute(null)) command.Execute(null);
    }

    private void FocusActiveConsultationSection()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (FindFirstVisible<TextBox>(ConsultationSectionsPanel) is { } box)
            {
                box.Focus();
                box.CaretIndex = box.Text.Length;
                box.BringIntoView();
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// "Estudios de laboratorio" (sección 5): el comando abre la sección 6 y aquí se trae a la vista desde su encabezado
    /// (después el cursor queda en la búsqueda de estudios, como al cambiar de sección).
    /// </summary>
    private void LabStudiesButton_Click(object sender, RoutedEventArgs e)
    {
        Dispatcher.InvokeAsync(() => LabSection?.BringIntoView(), DispatcherPriority.Loaded);
    }

    private static T? FindFirstVisible<T>(DependencyObject? root) where T : UIElement
    {
        if (root == null) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { IsVisible: false }) continue;
            if (child is T match && match.IsEnabled) return match;
            if (FindFirstVisible<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void DiagnosisTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MedicalRecordsViewModel vm) return;

        if (e.Key == Key.Down)
        {
            if (ActiveCie10SuggestionsListBox?.Items.Count > 0)
            {
                var cur = ActiveCie10SuggestionsListBox.SelectedIndex;
                var nextIndex = cur + 1;
                if (nextIndex >= ActiveCie10SuggestionsListBox.Items.Count || nextIndex < 0)
                    nextIndex = 0;
                ActiveCie10SuggestionsListBox.SelectedIndex = nextIndex;
                var item = ActiveCie10SuggestionsListBox.Items[nextIndex];
                ActiveCie10SuggestionsListBox.ScrollIntoView(item);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            if (ActiveCie10SuggestionsListBox?.Items.Count > 0)
            {
                var cur = ActiveCie10SuggestionsListBox.SelectedIndex;
                var prevIndex = cur - 1;
                if (prevIndex < 0 || prevIndex >= ActiveCie10SuggestionsListBox.Items.Count)
                    prevIndex = ActiveCie10SuggestionsListBox.Items.Count - 1;
                ActiveCie10SuggestionsListBox.SelectedIndex = prevIndex;
                var item = ActiveCie10SuggestionsListBox.Items[prevIndex];
                ActiveCie10SuggestionsListBox.ScrollIntoView(item);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (ActiveCie10SuggestionsListBox?.SelectedItem is Cie10Diagnosis item)
            {
                vm.Consultation.SelectCie10Suggestion(item);
                e.Handled = true;
            }
        }
    }

    /// <summary>Laboratorio: ↑ ↓ cambian la sugerencia, Enter agrega (la marcada o lo escrito), Esc borra la búsqueda.</summary>
    private void LabStudySearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MedicalRecordsViewModel vm || Keyboard.Modifiers != ModifierKeys.None) return;
        var labs = vm.Labs;
        switch (e.Key)
        {
            case Key.Down when labs.HasSuggestions:
                labs.NextSuggestion();
                break;
            case Key.Up when labs.HasSuggestions:
                labs.PreviousSuggestion();
                break;
            case Key.Enter when !string.IsNullOrWhiteSpace(labs.StudySearchText):
                labs.AcceptStudy();
                break;
            case Key.Escape when !string.IsNullOrEmpty(labs.StudySearchText):
                labs.StudySearchText = string.Empty;
                break;
            default:
                return;
        }
        e.Handled = true;
        if (labs.SelectedSuggestionIndex >= 0 && labs.SelectedSuggestionIndex < LabSuggestionsListBox.Items.Count)
        {
            LabSuggestionsListBox.ScrollIntoView(LabSuggestionsListBox.Items[labs.SelectedSuggestionIndex]);
        }
    }

    private void LabSuggestionsListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not ListBoxItem && dep != sender)
        {
            dep = VisualTreeHelper.GetParent(dep);
        }
        if (dep is ListBoxItem { DataContext: TPVC.Services.LabCatalogStudy study } && DataContext is MedicalRecordsViewModel vm)
        {
            vm.Labs.AddSuggestion(study);
            LabStudySearchTextBox?.Focus();
            e.Handled = true;
        }
    }

    private void Cie10SuggestionsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MedicalRecordsViewModel vm) return;

        if (e.Key == Key.Enter)
        {
            if (ActiveCie10SuggestionsListBox?.SelectedItem is Cie10Diagnosis item)
            {
                vm.Consultation.SelectCie10Suggestion(item);
                ActiveConsultationDiagnosisTextBox?.Focus();
                e.Handled = true;
            }
        }
    }

    private void Cie10SuggestionsListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not ListBoxItem && dep != sender)
        {
            dep = VisualTreeHelper.GetParent(dep);
        }

        if (dep is ListBoxItem item && item.DataContext is Cie10Diagnosis diag)
        {
            if (DataContext is MedicalRecordsViewModel vm)
            {
                vm.Consultation.SelectCie10Suggestion(diag);
                ActiveConsultationDiagnosisTextBox?.Focus();
                e.Handled = true;
            }
        }
    }

    private MedicalRecordsViewModel? _subscribed;

    private void AttachToViewModel(MedicalRecordsViewModel? viewModel)
    {
        if (_subscribed != null) _subscribed.Consultation.PropertyChanged -= ViewModel_PropertyChanged;
        _subscribed = viewModel;
        if (_subscribed != null) _subscribed.Consultation.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConsultationViewModel.ConsultationSection)) FocusActiveConsultationSection();
    }
}
