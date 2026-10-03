using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace TPVC.Services;

public class GitHubReleaseInfo
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;
}

public class UpdateCheckResult
{
    public bool IsUpdateAvailable { get; set; }
    public string CurrentVersion { get; set; } = "1.0.0";
    public string LatestVersion { get; set; } = "1.0.0";
    public string ReleaseNotes { get; set; } = string.Empty;
    public string ReleaseUrl { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class AppUpdateService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    static AppUpdateService()
    {
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TPVC-Inventory-App");
    }

    public string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public async Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        var result = new UpdateCheckResult
        {
            CurrentVersion = CurrentVersion
        };

        try
        {
            // Consultar el repositorio GitHub oficial
            var url = "https://api.github.com/repos/Marioalr/TPVC/releases/latest";
            var response = await HttpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                var release = await response.Content.ReadFromJsonAsync<GitHubReleaseInfo>();
                if (release != null)
                {
                    result.LatestVersion = release.TagName.TrimStart('v', 'V');
                    result.ReleaseNotes = release.Body;
                    result.ReleaseUrl = release.HtmlUrl;

                    if (Version.TryParse(result.LatestVersion, out var latest) &&
                        Version.TryParse(result.CurrentVersion, out var current))
                    {
                        if (latest > current)
                        {
                            result.IsUpdateAvailable = true;
                            result.Message = $"¡Nueva versión disponible: v{result.LatestVersion}!";
                            return result;
                        }
                    }
                }
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                result.Message = "El sistema está actualizado. (No hay releases publicados todavía en GitHub).";
                return result;
            }

            result.Message = $"El sistema está actualizado (v{CurrentVersion}).";
        }
        catch (Exception ex)
        {
            result.Message = $"No se pudo verificar la actualización: {ex.Message}";
        }

        return result;
    }

    public void OpenReleaseUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            url = "https://github.com/Marioalr/TPVC/releases";
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // En caso de que no haya navegador predeterminado
        }
    }
}
