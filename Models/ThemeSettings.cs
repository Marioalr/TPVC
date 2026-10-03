namespace TPVC.Models;

public class ThemeSettings
{
    public string ThemeMode { get; set; } = "Claro"; // Claro, Oscuro, Personalizado
    public string AccentColor { get; set; } = "#0284C7"; // Azul cielo médico por defecto
    public string BackgroundColor { get; set; } = "#F8FAFC"; // Fondo claro suave
    public string SurfaceColor { get; set; } = "#FFFFFF"; // Superficie de tarjetas/contenedores
    public string TextColor { get; set; } = "#0F172A"; // Texto principal
    public string SecondaryTextColor { get; set; } = "#64748B"; // Texto secundario/pistas
    public string BorderColor { get; set; } = "#E2E8F0"; // Bordes sutiles
}
