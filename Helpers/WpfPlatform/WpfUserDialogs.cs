using System.Threading.Tasks;
using TPVC.Services;
using TPVC.ViewModels.Patients;

namespace TPVC.Helpers.WpfPlatform;

/// <summary>
/// Confirmaciones y errores de Pacientes con DialogService (la app conecta ahí sus ventanas y las
/// pruebas sus respuestas). ConfirmAsync pregunta en ese momento y regresa la respuesta ya lista: quien
/// la espera sigue sin pausa, igual que con la confirmación síncrona.
/// </summary>
public sealed class WpfUserDialogs : IUserDialogs
{
    public bool Confirm(string message, string title) => DialogService.Confirm(message, title);

    public Task<bool> ConfirmAsync(string message, string title) => Task.FromResult(DialogService.Confirm(message, title));

    public void ShowError(string message) => DialogService.ShowError(message);
}
