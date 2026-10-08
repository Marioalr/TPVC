using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TPVC.Services;

namespace TPVC.Helpers;

/// <summary>
/// La barra de título la dibuja Windows: sin esto queda blanca encima del modo oscuro.
/// Pide a Windows (10 20H1 o posterior y 11) la variante oscura cuando el tema es oscuro y la
/// actualiza al cambiar de tema. En versiones que no lo admiten no hace nada.
/// </summary>
public static class TitleBar
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;

    private const int WmNcActivate = 0x0086;
    private const int FirstWindows11Build = 22000;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    /// <summary>Aplica el modo del tema actual a la ventana y lo sigue en cada cambio de tema.</summary>
    public static void Follow(Window window, ThemeService themes)
    {
        void Apply() => SetDark(window, ThemePalette.IsDark(themes.CurrentSettings));

        // LoadTheme no avisa con ThemeChanged: el primer pintado se hace al crear la ventana
        window.SourceInitialized += (_, _) => Apply();
        themes.ThemeChanged += () => window.Dispatcher.Invoke(Apply);
        Apply();
    }

    public static void SetDark(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        int value = dark ? 1 : 0;
        try
        {
            if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, UseImmersiveDarkModeBefore20H1, ref value, sizeof(int));
            }

            // Windows 10 no repinta la barra con la ventana ya abierta hasta que cambia el foco:
            // se le pide que la dibuje inactiva y otra vez como estaba
            if (window.IsVisible && Environment.OSVersion.Version.Build < FirstWindows11Build)
            {
                bool active = GetActiveWindow() == handle;
                SendMessage(handle, WmNcActivate, active ? IntPtr.Zero : new IntPtr(1), IntPtr.Zero);
                SendMessage(handle, WmNcActivate, active ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }
}
