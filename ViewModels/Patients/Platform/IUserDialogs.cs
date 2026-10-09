using System.Threading.Tasks;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Confirmaciones y avisos de error de Pacientes (fase 6, paso 2b). Confirm es síncrona (cancelar el
/// alta la sigue usando así); ConfirmAsync solo se usa desde métodos que ya son asíncronos. En WPF las
/// dos muestran la misma ventana y ConfirmAsync regresa una tarea ya terminada.
/// </summary>
public interface IUserDialogs
{
    bool Confirm(string message, string title);

    Task<bool> ConfirmAsync(string message, string title);

    void ShowError(string message);
}
