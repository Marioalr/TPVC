using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;

namespace TPVC.Services;

public class GitHubReleaseAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

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

    [JsonPropertyName("assets")]
    public List<GitHubReleaseAsset> Assets { get; set; } = new();
}

public class UpdateCheckResult
{
    public bool IsUpdateAvailable { get; set; }
    public string CurrentVersion { get; set; } = "1.2.0";
    public string LatestVersion { get; set; } = "1.2.0";
    public string ReleaseNotes { get; set; } = string.Empty;
    public string ReleaseUrl { get; set; } = string.Empty;
    public string DirectDownloadUrl { get; set; } = string.Empty;
    public string DownloadFileName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class AppUpdateService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    static AppUpdateService()
    {
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TPVC-Inventory-App");
    }

    public string CurrentVersion => 
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? 
        typeof(AppUpdateService).Assembly.GetName().Version?.ToString(3) ?? 
        "1.2.1";

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

                    var asset = release.Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || 
                                                                 a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                    if (asset != null)
                    {
                        result.DirectDownloadUrl = asset.BrowserDownloadUrl;
                        result.DownloadFileName = asset.Name;
                    }

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
                result.Message = "El sistema está en su versión actual (sin releases pendientes en GitHub).";
                return result;
            }

            result.Message = $"La aplicación está actualizada (v{CurrentVersion}).";
        }
        catch (Exception ex)
        {
            result.Message = $"No se pudo verificar la actualización: {ex.Message}";
        }

        return result;
    }

    public async Task<bool> DownloadAndInstallUpdateAsync(string downloadUrl, string fileName, IProgress<double>? progress = null)
    {
        try
        {
            var destination = Path.Combine(Path.GetTempPath(), fileName);

            using var response = await HttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalRead = 0;
            int read;

            while ((read = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read));
                totalRead += read;
                if (totalBytes > 0 && progress != null)
                {
                    progress.Report((double)totalRead / totalBytes * 100);
                }
            }

            fileStream.Close();

            // Si es un instalador o ejecutable, ejecutarlo
            if (destination.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = destination,
                    UseShellExecute = true
                });

                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    Application.Current.Shutdown();
                });
                return true;
            }

            // Si es un zip, abrir la carpeta contenedora
            OpenReleaseUrl(Path.GetDirectoryName(destination) ?? destination);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
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
            // Silencioso
        }
    }
}
