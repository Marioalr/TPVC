using System;
using System.Threading.Tasks;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Hilo de la interfaz para Pacientes (fase 6, paso 2a). Cada miembro reproduce uno de los usos que
/// el ViewModel hacía directamente del hilo de WPF o de su contexto de sincronización; quien lo llama
/// conserva su propio try/catch y si espera o no el resultado. En WPF lo implementa WpfUiThread; sin
/// interfaz (pruebas en Linux, más adelante MAUI), InlineUiThread.
/// </summary>
public interface IUiThread
{
    /// <summary>
    /// true cuando hay hilo de interfaz y la llamada viene de otro hilo (hay que pasarle el trabajo).
    /// Puede lanzar excepción, igual que la consulta que reemplaza: quien la usa decide qué hacer.
    /// </summary>
    bool IsOffUiThread { get; }

    /// <summary>Pone la acción en el hilo de la interfaz; la tarea termina cuando la acción terminó.</summary>
    Task InvokeAsync(Action action);

    /// <summary>
    /// Pone el trabajo asíncrono en el hilo de la interfaz. La tarea termina cuando el trabajo regresa
    /// su tarea (no cuando esa tarea termina); quienes lo llaman no la esperan.
    /// </summary>
    Task InvokeAsync(Func<Task> action);

    /// <summary>
    /// Deja la acción en la cola del hilo de la interfaz con prioridad baja (después de lo que ya está
    /// en curso). false si no hay hilo de interfaz: quien llama la ejecuta en ese momento.
    /// </summary>
    bool TryPostBackground(Action action);

    /// <summary>
    /// Ejecuta en el contexto tomado al crear el ViewModel, sin esperar; sin contexto, aquí mismo
    /// (antes PostToUi del guardado automático).
    /// </summary>
    void Post(Action action);

    /// <summary>
    /// Ejecuta en el contexto tomado al crear el ViewModel y espera a que termine; sin contexto, o si ya
    /// se está en él, aquí mismo (antes el cuerpo de OnUiAsync del envío de consultas).
    /// </summary>
    Task RunAsync(Func<Task> action);
}
