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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

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
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }
}
