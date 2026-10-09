using TPVC.Services;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Avisos breves de Pacientes (fase 6, paso 4): con el servicio de avisos de la aplicación, una nota
/// que se va sola; sin él (pruebas), la ventana de aviso.
/// </summary>
public sealed class PatientsFeedback
{
    private readonly NotificationService? _notifications;
    private readonly InfoDialogState _info;

    public PatientsFeedback(NotificationService? notifications, InfoDialogState info)
    {
        _notifications = notifications;
        _info = info;
    }

    /// <summary>Problema que no impide seguir (p. ej. falta de existencias al cobrar).</summary>
    public void ShowWarning(string message)
    {
        if (_notifications != null)
        {
            _notifications.Warning(message);
            return;
        }
        _info.InfoModalMessage = message;
        _info.IsInfoModalVisible = true;
    }

    /// <summary>Confirmación breve que se va sola; sin servicio de avisos (pruebas) usa la ventana.</summary>
    public void ShowSuccess(string message)
    {
        if (_notifications != null)
        {
            var firstLine = message.Split('\n')[0].Trim().TrimStart('¡').TrimEnd('!');
            _notifications.Success(firstLine);
            return;
        }
        _info.InfoModalMessage = message;
        _info.IsInfoModalVisible = true;
    }

    /// <summary>Nota informativa (solo con el servicio de avisos; sin él no se muestra nada).</summary>
    public void Info(string message) => _notifications?.Info(message);
}
