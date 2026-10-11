using System;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TPVC.Helpers;

namespace TPVC.Tests;

/// <summary>
/// Una sola ventana de TPVC: la segunda apertura trae al frente la primera y termina. El mutex pertenece a un hilo,
/// así que la primera instancia de cada prueba vive en su propio hilo, como el hilo de la interfaz de la app, y cada
/// segunda apertura también (no en el pool: la respuesta corre en un hilo del pool y con las pruebas en paralelo podía
/// tardar más que la espera). Mutex y semáforos con nombre son de Windows.
/// </summary>
[TestClass]
[TestCategory("Windows")]
[DoNotParallelize]
public class SingleInstanceGuardTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    /// <summary>La primera instancia, en su hilo: toma el mutex, escucha si se pide y lo suelta al detenerse.</summary>
    private sealed class FirstInstance : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new();
        private readonly ManualResetEventSlim _stop = new();
        private readonly Thread _thread;
        private SingleInstanceGuard? _lateListener;
        private int _activations;

        public bool Acquired { get; private set; }
        public int Activations => Volatile.Read(ref _activations);

        public FirstInstance(string key, bool listen = true, bool disposeOnStop = true)
        {
            _thread = new Thread(() =>
            {
                var guard = new SingleInstanceGuard(key);
                Acquired = guard.TryAcquire();
                if (listen) guard.ListenForActivation(() => Interlocked.Increment(ref _activations));
                _ready.Set();
                _stop.Wait();
                // Sin Dispose: el proceso se cerró a la fuerza y el mutex queda abandonado
                if (disposeOnStop) guard.Dispose();
            }) { IsBackground = true };
            _thread.Start();
            Assert.IsTrue(_ready.Wait(Wait));
        }

        /// <summary>Para la prueba del aviso que llega antes: otra guardia con la misma clave solo escucha.</summary>
        public void StartListening(string key)
        {
            _lateListener = new SingleInstanceGuard(key);
            _lateListener.ListenForActivation(() => Interlocked.Increment(ref _activations));
        }

        public void Dispose()
        {
            _stop.Set();
            Assert.IsTrue(_thread.Join(Wait));
            _lateListener?.Dispose();
        }
    }

    /// <summary>Una segunda apertura en su propio hilo: avisa y espera la respuesta.</summary>
    private sealed class SecondOpening
    {
        private readonly Thread _thread;
        private bool _answered;

        public SecondOpening(string key, TimeSpan timeout)
        {
            _thread = new Thread(() => _answered = new SingleInstanceGuard(key).SignalExistingInstance(timeout)) { IsBackground = true };
            _thread.Start();
        }

        public bool Answered()
        {
            Assert.IsTrue(_thread.Join(Wait + Wait));
            return _answered;
        }
    }

    private static string NewKey() => "prueba-" + Guid.NewGuid().ToString("N");

    /// <summary>TryAcquire en un hilo aparte que termina enseguida (soltando lo que haya tomado).</summary>
    private static bool AcquiresOnAnotherThread(string key)
    {
        bool acquired = false;
        var thread = new Thread(() =>
        {
            using var guard = new SingleInstanceGuard(key);
            acquired = guard.TryAcquire();
        });
        thread.Start();
        thread.Join();
        return acquired;
    }

    [TestMethod]
    public void ASecondOpening_DoesNotGetTheInstance_AndBringsTheFirstToTheFront()
    {
        var key = NewKey();
        using var first = new FirstInstance(key);
        Assert.IsTrue(first.Acquired);

        using var second = new SingleInstanceGuard(key);
        Assert.IsFalse(second.TryAcquire(), "La segunda apertura no abre otra ventana");
        Assert.IsTrue(second.SignalExistingInstance(Wait), "La primera respondió");
        Assert.AreEqual(1, first.Activations);
    }

    [TestMethod]
    public void TwoQuickDoubleClicks_EachGetsItsAnswer()
    {
        var key = NewKey();
        using var first = new FirstInstance(key);

        var openings = Enumerable.Range(0, 2).Select(_ => new SecondOpening(key, Wait)).ToArray();

        Assert.IsTrue(openings.All(o => o.Answered()), "Ningún aviso se pierde ni se junta con el otro");
        Assert.AreEqual(2, first.Activations);
    }

    [TestMethod]
    public void AFirstInstanceThatDoesNotAnswer_IsReported()
    {
        var key = NewKey();
        using var first = new FirstInstance(key, listen: false);

        using var second = new SingleInstanceGuard(key);
        Assert.IsFalse(second.TryAcquire());
        Assert.IsFalse(second.SignalExistingInstance(TimeSpan.FromMilliseconds(300)));
    }

    [TestMethod]
    public void ARequestThatArrivesBeforeListening_StillCounts()
    {
        var key = NewKey();
        using var first = new FirstInstance(key, listen: false);

        // La primera todavía arranca: el aviso queda guardado hasta que escucha
        var opening = new SecondOpening(key, Wait);
        Thread.Sleep(200);
        first.StartListening(key);

        Assert.IsTrue(opening.Answered());
        Assert.AreEqual(1, first.Activations);
    }

    [TestMethod]
    public void AfterClosing_TheNextOpeningIsTheFirstAgain()
    {
        var key = NewKey();
        new FirstInstance(key).Dispose();
        Assert.IsTrue(AcquiresOnAnotherThread(key));
    }

    [TestMethod]
    public void AfterACrash_TheNextOpeningIsTheFirstAgain()
    {
        // Cerrada a la fuerza (sin soltar el mutex): Windows lo deja abandonado y la siguiente apertura lo toma
        var key = NewKey();
        new FirstInstance(key, disposeOnStop: false).Dispose();
        Assert.IsTrue(AcquiresOnAnotherThread(key));
    }

    [TestMethod]
    public void TheKey_IsTheDatabasePath_WithoutCase()
    {
        Assert.AreEqual(SingleInstanceGuard.KeyFor(@"C:\Users\Doc\AppData\Local\TPVC\consultorio.db"),
            SingleInstanceGuard.KeyFor(@"c:\users\doc\appdata\local\tpvc\CONSULTORIO.db"));
        Assert.AreNotEqual(SingleInstanceGuard.KeyFor(@"C:\TPVC\consultorio.db"), SingleInstanceGuard.KeyFor(@"D:\TPVC\consultorio.db"));
    }
}
