using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TPVC.Helpers.WpfPlatform;
using TPVC.Services;
using TPVC.ViewModels;
using TPVC.ViewModels.Patients;

namespace TPVC.Tests.Fase6;

/// <summary>
/// Fase 6 (paso 2a): el hilo de la interfaz de Pacientes (IUiThread). Sin aplicación de WPF todo corre
/// aquí mismo, como antes; con un contexto de sincronización, Post lo deja en ese contexto y RunAsync
/// corre en él (aquí mismo si ya se está en él) y espera a que termine.
/// </summary>
[TestClass]
public class UiThreadSeamTests
{
    /// <summary>Contexto que guarda lo que se le manda y lo corre solo cuando la prueba lo pide.</summary>
    private sealed class RecordingContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public int Posted { get; private set; }

        public int Pending
        {
            get { lock (_queue) return _queue.Count; }
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_queue)
            {
                _queue.Enqueue((d, state));
                Posted++;
            }
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException("Pacientes no usa Send");

        /// <summary>Corre lo pendiente con este contexto como actual (lo que haría el hilo de la interfaz).</summary>
        public void RunPending()
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                while (true)
                {
                    (SendOrPostCallback Callback, object? State) next;
                    lock (_queue)
                    {
                        if (_queue.Count == 0) return;
                        next = _queue.Dequeue();
                    }
                    next.Callback(next.State);
                }
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }

    /// <summary>Corre <paramref name="action"/> con <paramref name="context"/> como contexto actual del hilo.</summary>
    private static T With<T>(SynchronizationContext? context, Func<T> action)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            return action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    // ================================================================== sin aplicación

    /// <summary>
    /// null: WPF no puede ni iniciarse (Linux: Application.Current lanza excepción). false: no hay
    /// aplicación. true: hay una (en la CI de Windows otra prueba la deja creada, con su despachador ya
    /// terminado).
    /// </summary>
    private static bool? WpfApplicationExists()
    {
        try
        {
            return System.Windows.Application.Current != null;
        }
        catch (TypeInitializationException)
        {
            return null;
        }
    }

    [TestMethod]
    public void WithoutApplication_WpfUiThreadRunsEverythingHere()
    {
        var application = WpfApplicationExists();
        if (application == true) return;

        var ui = With(null, () => new WpfUiThread());
        if (application == null)
        {
            // Igual que la consulta de Application.Current que reemplaza: lanza, y cada lugar que la usa
            // conserva su propio try/catch (p. ej. el reloj de la consulta no se actualiza desde su hilo)
            Assert.ThrowsExactly<TypeInitializationException>(() => ui.IsOffUiThread);
            Assert.ThrowsExactly<TypeInitializationException>(() => ui.TryPostBackground(() => { }));
        }
        else
        {
            Assert.IsFalse(ui.IsOffUiThread, "Sin aplicación nunca se está fuera del hilo de la interfaz");
            bool posted = false;
            Assert.IsFalse(ui.TryPostBackground(() => posted = true), "Sin aplicación no hay cola de la interfaz");
            Assert.IsFalse(posted, "TryPostBackground no la ejecuta: lo hace quien llama");
        }

        int resets = 0;
        ui.ResetSelectionLater(() => resets++);
        Assert.AreEqual(1, resets, "Sin aplicación la selección se limpia en ese momento, una sola vez");

        bool ran = false;
        ui.Post(() => ran = true);
        Assert.IsTrue(ran, "Sin contexto, Post corre aquí mismo");

        var inner = Task.FromResult(7);
        Assert.AreSame(inner, ui.RunAsync(() => inner), "Sin contexto, RunAsync corre aquí mismo y regresa la misma tarea");
    }

    [TestMethod]
    public void InlineUiThread_WithoutContext_RunsEverythingHere()
    {
        var ui = new InlineUiThread();
        Assert.IsFalse(ui.IsOffUiThread);

        bool posted = false;
        Assert.IsFalse(ui.TryPostBackground(() => posted = true));
        Assert.IsFalse(posted);

        int resets = 0;
        ui.ResetSelectionLater(() => resets++);
        Assert.AreEqual(1, resets, "La selección se limpia una sola vez, en ese momento");

        bool invoked = false;
        Assert.IsTrue(ui.InvokeAsync(() => invoked = true).IsCompleted);
        Assert.IsTrue(invoked);

        var inner = Task.FromResult(3);
        Assert.AreSame(inner, ui.InvokeAsync(() => inner), "Sin hilo de interfaz el trabajo asíncrono corre aquí mismo");

        bool ran = false;
        ui.Post(() => ran = true);
        Assert.IsTrue(ran);

        var work = new TaskCompletionSource();
        Assert.AreSame(work.Task, ui.RunAsync(() => work.Task), "RunAsync regresa la tarea del trabajo, sin envolverla");
    }

    [TestMethod]
    public void ResetSelectionLater_WhenQueueingFails_ResetsHere()
    {
        int resets = 0;
        new ThrowingUiThread().ResetSelectionLater(() => resets++);
        Assert.AreEqual(1, resets, "Si no se puede dejar en la cola de la interfaz, se limpia en ese momento");
    }

    private sealed class ThrowingUiThread : IUiThread
    {
        public bool IsOffUiThread => throw new InvalidOperationException();
        public Task InvokeAsync(Action action) => throw new InvalidOperationException();
        public Task InvokeAsync(Func<Task> action) => throw new InvalidOperationException();
        public bool TryPostBackground(Action action) => throw new InvalidOperationException("La aplicación se está cerrando");
        public void Post(Action action) => throw new InvalidOperationException();
        public Task RunAsync(Func<Task> action) => throw new InvalidOperationException();
    }

    // ================================================================== con contexto de sincronización

    [TestMethod]
    public void Post_WithContext_LeavesTheWorkInThatContext()
    {
        var context = new RecordingContext();
        var ui = new InlineUiThread(context);

        bool ran = false;
        ui.Post(() => ran = true);
        Assert.IsFalse(ran, "Post no corre aquí: lo deja en el contexto");
        Assert.AreEqual(1, context.Posted);

        context.RunPending();
        Assert.IsTrue(ran);
    }

    [TestMethod]
    public void Post_FromTheContextItself_StillPosts()
    {
        // Igual que el antiguo PostToUi: siempre pasa por el contexto, aunque ya se esté en él
        var context = new RecordingContext();
        var ui = new InlineUiThread(context);

        bool ran = false;
        With(context, () => { ui.Post(() => ran = true); return 0; });
        Assert.IsFalse(ran);
        Assert.AreEqual(1, context.Posted);
        context.RunPending();
        Assert.IsTrue(ran);
    }

    [TestMethod]
    public void RunAsync_OnTheContext_RunsHere()
    {
        var context = new RecordingContext();
        var ui = new InlineUiThread(context);

        bool ran = false;
        var inner = Task.CompletedTask;
        var task = With(context, () => ui.RunAsync(() =>
        {
            ran = true;
            return inner;
        }));

        Assert.IsTrue(ran, "Ya en el contexto: corre aquí mismo");
        Assert.AreSame(inner, task);
        Assert.AreEqual(0, context.Posted);
    }

    [TestMethod]
    public void RunAsync_OffTheContext_PostsAndWaitsForTheWork()
    {
        var context = new RecordingContext();
        var ui = new InlineUiThread(context);
        var gate = new TaskCompletionSource();
        var steps = new List<string>();

        var task = ui.RunAsync(async () =>
        {
            steps.Add("inicio");
            await gate.Task;
            steps.Add("fin");
        });

        Assert.AreEqual(0, steps.Count, "No corre aquí: se deja en el contexto");
        Assert.AreEqual(1, context.Posted);
        Assert.IsFalse(task.IsCompleted);

        context.RunPending();
        CollectionAssert.AreEqual(new[] { "inicio" }, steps);
        Assert.IsFalse(task.IsCompleted, "Espera a que el trabajo termine, no solo a que empiece");

        gate.SetResult();
        context.RunPending(); // la continuación del trabajo vuelve al contexto
        CollectionAssert.AreEqual(new[] { "inicio", "fin" }, steps);
        Assert.IsTrue(task.IsCompletedSuccessfully);
    }

    [TestMethod]
    public void RunAsync_OffTheContext_ReportsTheFailure()
    {
        var context = new RecordingContext();
        var ui = new InlineUiThread(context);

        var task = ui.RunAsync(() => throw new InvalidOperationException("falló en la interfaz"));
        Assert.IsFalse(task.IsCompleted);
        context.RunPending();

        Assert.IsTrue(task.IsFaulted);
        Assert.AreEqual("falló en la interfaz", task.Exception!.InnerException!.Message);
    }

    [TestMethod]
    public void WpfUiThread_TakesTheContextOfTheThreadThatCreatesIt()
    {
        var context = new RecordingContext();
        var ui = With(context, () => new WpfUiThread());

        bool ran = false;
        ui.Post(() => ran = true);
        Assert.IsFalse(ran);
        Assert.AreEqual(1, context.Posted, "Post usa el contexto que había al crearlo, no el actual");
        context.RunPending();
        Assert.IsTrue(ran);

        var task = ui.RunAsync(() => Task.CompletedTask);
        Assert.IsFalse(task.IsCompleted, "Fuera de su contexto, RunAsync lo deja en él");
        context.RunPending();
        Assert.IsTrue(task.IsCompletedSuccessfully);
    }

    [TestMethod]
    public void MedicalRecords_TakesTheContextWhenItIsCreated()
    {
        // El indicador "Guardado hh:mm:ss" del guardado automático se pone en el contexto que había al
        // crear el ViewModel (antes _autosaveUiContext); sin contexto, en ese momento
        var folder = Path.Combine(Path.GetTempPath(), $"tpvc_uithread_{Guid.NewGuid():N}");
        try
        {
            var context = new RecordingContext();
            var withContext = With(context, () => OpenWithDraftStore(Path.Combine(folder, "a.json")));
            var withoutContext = With(null, () => OpenWithDraftStore(Path.Combine(folder, "b.json")));

            withContext.Autosave.SaveConsultationDraftNow();
            withoutContext.Autosave.SaveConsultationDraftNow();

            StringAssert.StartsWith(withoutContext.Autosave.AutosaveStatus, "Guardado ");
            Assert.AreEqual(string.Empty, withContext.Autosave.AutosaveStatus, "El indicador espera al contexto del ViewModel");
            Assert.IsTrue(context.Pending > 0);

            context.RunPending();
            StringAssert.StartsWith(withContext.Autosave.AutosaveStatus, "Guardado ");

            withContext.Dispose();
            withoutContext.Dispose();
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    private static MedicalRecordsViewModel OpenWithDraftStore(string draftPath)
    {
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = new MedicalRecordsViewModel(null, consultations, consultationDraftStore: new ConsultationDraftStore(draftPath))
        {
            AutosaveDelay = TimeSpan.FromHours(1)
        };
        records.SelectPatient(records.Patients.Single(p => p.Id == 2));
        records.StartActiveConsultation();
        // El cronómetro no cambia (empieza en el futuro): nada llega desde su hilo
        consultations.CurrentConsultation!.StartedAt = DateTime.Now.AddHours(1);
        return records;
    }
}
