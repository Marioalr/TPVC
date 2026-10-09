using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Aviso de Pacientes (fase 6, paso 4): la ventana "Notificación clínica" con su mensaje. Se cierra
/// sola a los 4 segundos, con Aceptar o con Esc.
/// </summary>
public partial class InfoDialogState : ObservableObject
{
    private readonly IUiThread _ui;

    public InfoDialogState(IUiThread ui)
    {
        _ui = ui;
    }

    [ObservableProperty]
    private string? _infoModalMessage;

    [ObservableProperty]
    private bool _isInfoModalVisible;

    private System.Threading.CancellationTokenSource? _infoModalCts;

    partial void OnIsInfoModalVisibleChanged(bool value)
    {
        if (value)
        {
            _infoModalCts?.Cancel();
            _infoModalCts?.Dispose();
            _infoModalCts = new System.Threading.CancellationTokenSource();
            var token = _infoModalCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(4000, token);
                    if (!token.IsCancellationRequested)
                    {
                        if (_ui.IsOffUiThread)
                        {
                            await _ui.InvokeAsync(() => DismissInfoModal());
                        }
                        else
                        {
                            DismissInfoModal();
                        }
                    }
                }
                catch (System.OperationCanceledException) { }
            }, token);
        }
        else
        {
            _infoModalCts?.Cancel();
        }
    }

    [RelayCommand]
    public void DismissInfoModal()
    {
        _infoModalCts?.Cancel();
        IsInfoModalVisible = false;
        InfoModalMessage = null;
    }
}
