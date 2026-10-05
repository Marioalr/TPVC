using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;

namespace TPVC.Services;

public class GitHubReleaseAsset
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

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

public class UpdateConfig
{
    public string? GitHubToken { get; set; }
}

public class UpdateCheckResult
{
    public bool IsUpdateAvailable { get; set; }
    public string CurrentVersion { get; set; } = "1.3.19";
    public string LatestVersion { get; set; } = "1.3.19";
    public string ReleaseNotes { get; set; } = string.Empty;
    public string ReleaseUrl { get; set; } = string.Empty;
    public string DirectDownloadUrl { get; set; } = string.Empty;
    public string AssetApiUrl { get; set; } = string.Empty;
    public string DownloadFileName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class AppUpdateService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TPVC",
        "update_config.json"
    );

    static AppUpdateService()
    {
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TPVC-Inventory-App");
    }

    private string? _currentVersion;
    public string CurrentVersion
    {
        get => _currentVersion ?? 
               Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? 
               typeof(AppUpdateService).Assembly.GetName().Version?.ToString(3) ?? 
               "1.3.19";
        set => _currentVersion = value;
    }

    public string? GitHubToken { get; set; }

    public AppUpdateService()
    {
        LoadConfig();
    }

    public void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<UpdateConfig>(json);
                if (config != null && !string.IsNullOrWhiteSpace(config.GitHubToken))
                {
                    GitHubToken = config.GitHubToken.Trim();
                }
            }
        }
        catch
        {
            // Omitir fallos de lectura de configuración
        }
    }

    public void SaveToken(string? token)
    {
        GitHubToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var config = new UpdateConfig { GitHubToken = GitHubToken };
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // Omitir fallos de escritura de configuración
        }
    }

    public string? GetEffectiveToken()
    {
        if (!string.IsNullOrWhiteSpace(GitHubToken))
            return GitHubToken;

        var envToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken))
            return envToken.Trim();

        return null;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        var result = new UpdateCheckResult
        {
            CurrentVersion = CurrentVersion
        };

        try
        {
            var url = "https://api.github.com/repos/Marioalr/TPVC/releases/latest";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            var token = GetEffectiveToken();
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await HttpClient.SendAsync(request);

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
                        result.AssetApiUrl = asset.Url;
                        result.DownloadFileName = asset.Name;
                    }

                    var currentClean = result.CurrentVersion.TrimStart('v', 'V');
                    if (Version.TryParse(result.LatestVersion, out var latest) &&
                        Version.TryParse(currentClean, out var current))
                    {
                        if (latest > current)
                        {
                            result.IsUpdateAvailable = true;
                            result.Message = $"¡Nueva versión disponible: v{result.LatestVersion}!";
                        }
                        else
                        {
                            result.IsUpdateAvailable = false;
                            result.Message = $"La aplicación está actualizada (v{result.CurrentVersion}).";
                        }
                        return result;
                    }
                    else
                    {
                        result.IsUpdateAvailable = false;
                        result.Message = $"Versión detectada: v{result.LatestVersion} (actual: v{result.CurrentVersion}).";
                        return result;
                    }
                }
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                result.IsUpdateAvailable = false;
                if (string.IsNullOrWhiteSpace(token))
                {
                    result.Message = "El repositorio es privado o no tiene releases públicas. Configure un Personal Access Token (PAT) de GitHub en Ajustes para consultar actualizaciones.";
                }
                else
                {
                    result.Message = "No se encontraron releases en el repositorio con el token configurado (verifique permiso 'repo').";
                }
                return result;
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Forbidden || response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                result.IsUpdateAvailable = false;
                result.Message = "Acceso no autorizado o límite de peticiones de GitHub alcanzado. Verifique el token configurado.";
                return result;
            }
            else
            {
                result.IsUpdateAvailable = false;
                result.Message = $"No se pudo consultar el servidor de actualizaciones (Error HTTP {(int)response.StatusCode}: {response.ReasonPhrase}).";
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

    public async Task<bool> DownloadAndInstallUpdateAsync(string downloadUrl, string fileName, string? assetApiUrl = null, IProgress<double>? progress = null)
    {
        try
        {
            var destination = Path.Combine(Path.GetTempPath(), fileName);
            var token = GetEffectiveToken();

            // Para repositorios privados con token, descargar directamente del endpoint de asset con Accept: application/octet-stream
            string targetUrl = (!string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(assetApiUrl))
                ? assetApiUrl
                : downloadUrl;

            using var request = new HttpRequestMessage(HttpMethod.Get, targetUrl);

            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                if (!string.IsNullOrWhiteSpace(assetApiUrl))
                {
                    request.Headers.Accept.Clear();
                    request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/octet-stream"));
                }
            }

            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
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
