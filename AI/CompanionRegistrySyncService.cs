using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AshServer.Data;
using AshServer.Models;
using AshServer.Controllers;

namespace AshServer.AI;

public class CompanionRegistrySyncService : BackgroundService
{
    private readonly Database _db;
    private readonly IConfiguration _config;
    private readonly ILogger<CompanionRegistrySyncService> _log;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public CompanionRegistrySyncService(
        Database db,
        IConfiguration config,
        ILogger<CompanionRegistrySyncService> log)
    {
        _db = db;
        _config = config;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("[registry-sync] Companion registry background service started.");

        // Loop runs every 4 hours
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // First sync runs 1 minute after startup, then every 4 hours
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                await SyncAllNow(stoppingToken);
                await Task.Delay(TimeSpan.FromHours(4), stoppingToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "[registry-sync] Error in background registry sync loop");
            }
        }
    }

    public async Task SyncAllNow(CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return;

        await _syncLock.WaitAsync(ct);
        try
        {
            _log.LogInformation("[registry-sync] Beginning companion repository sync...");
            var repos = await _db.GetRepositories();
            if (repos.Count == 0)
            {
                _log.LogInformation("[registry-sync] No registered companion repositories found.");
                return;
            }

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(30);

            var relativePath = _config["personality:path"] ?? "personality";
            var companionsDir = Path.Combine(AppContext.BaseDirectory, relativePath, "companions");
            Directory.CreateDirectory(companionsDir);

            var uploadsDir = Path.Combine(AppContext.BaseDirectory, "wwwroot", "uploads");
            Directory.CreateDirectory(uploadsDir);

            foreach (var repo in repos)
            {
                try
                {
                    _log.LogInformation("[registry-sync] Syncing repository: {RepoName} ({RepoUrl})", repo.Name, repo.Url);
                    string manifestJson;
                    bool isLocalPath = !repo.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && 
                                       !repo.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

                    if (isLocalPath)
                    {
                        var localCompanions = new List<CompanionRegistryItem>();
                        if (Directory.Exists(repo.Url))
                        {
                            var files = Directory.GetFiles(repo.Url, "*.json");
                            foreach (var file in files)
                            {
                                try
                                {
                                    var fileJson = await File.ReadAllTextAsync(file, ct);
                                    using var doc = JsonDocument.Parse(fileJson);
                                    var root = doc.RootElement;
                                    var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                                    var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                                    if (!string.IsNullOrEmpty(id))
                                    {
                                        localCompanions.Add(new CompanionRegistryItem
                                        {
                                            Id = id,
                                            Name = name ?? id,
                                            DownloadUrl = file,
                                            AvatarUrl = ""
                                        });
                                    }
                                }
                                catch {}
                            }
                        }
                        var localManifestObj = new CompanionRegistryManifest
                        {
                            Name = repo.Name,
                            Companions = localCompanions
                        };
                        manifestJson = JsonSerializer.Serialize(localManifestObj);
                    }
                    else
                    {
                        var url = repo.Url;
                        if (url.Contains("github.com", StringComparison.OrdinalIgnoreCase) && 
                            !url.Contains("/raw/", StringComparison.OrdinalIgnoreCase) && 
                            !url.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        {
                            url = url.Replace("github.com", "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
                            url = url.TrimEnd('/') + "/main/registry.json";
                        }

                        try
                        {
                            manifestJson = await http.GetStringAsync(url, ct);
                        }
                        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound && url.Contains("/main/registry.json", StringComparison.OrdinalIgnoreCase))
                        {
                            var fallbackUrl = url.Replace("/main/registry.json", "/master/registry.json", StringComparison.OrdinalIgnoreCase);
                            _log.LogInformation("[registry-sync] Manifest not found on main branch, trying master fallback: {FallbackUrl}", fallbackUrl);
                            manifestJson = await http.GetStringAsync(fallbackUrl, ct);
                        }
                    }
                    
                    var manifest = JsonSerializer.Deserialize<CompanionRegistryManifest>(manifestJson, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (manifest == null)
                    {
                        _log.LogWarning("[registry-sync] Failed to parse manifest from {RepoUrl}", repo.Url);
                        continue;
                    }

                    foreach (var companion in manifest.Companions)
                    {
                        try
                        {
                            var cleanId = string.Concat(companion.Id.Split(Path.GetInvalidFileNameChars())).Trim().ToLowerInvariant();
                            if (string.IsNullOrEmpty(cleanId)) continue;

                            var filePath = Path.Combine(companionsDir, $"{cleanId}.json");
                            
                            string profileJson;
                            if (isLocalPath || !companion.DownloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            {
                                _log.LogInformation("[registry-sync] Reading companion profile from local path: {CompName} ({CleanId})", companion.Name, cleanId);
                                profileJson = await File.ReadAllTextAsync(companion.DownloadUrl, ct);
                            }
                            else
                            {
                                _log.LogInformation("[registry-sync] Downloading companion profile: {CompName} ({CleanId})", companion.Name, cleanId);
                                profileJson = await http.GetStringAsync(companion.DownloadUrl, ct);
                            }

                            // Validate json by attempting to deserialize it
                            var companionConfig = JsonSerializer.Deserialize<CompanionConfig>(profileJson, new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            });

                            if (companionConfig == null)
                            {
                                _log.LogWarning("[registry-sync] Downloaded invalid profile JSON for {CleanId}", cleanId);
                                continue;
                            }

                            // Write profile to the local companion directory
                            await File.WriteAllTextAsync(filePath, profileJson, ct);

                            // Download avatar image if provided
                            if (!string.IsNullOrEmpty(companion.AvatarUrl) && companion.AvatarUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            {
                                _log.LogInformation("[registry-sync] Downloading avatar for companion: {CleanId} from {AvatarUrl}", cleanId, companion.AvatarUrl);
                                var imgBytes = await http.GetByteArrayAsync(companion.AvatarUrl, ct);
                                
                                var ext = Path.GetExtension(companion.AvatarUrl);
                                if (string.IsNullOrEmpty(ext)) ext = ".png";
                                
                                var avatarFilename = $"companion_{cleanId}{ext}";
                                var avatarPath = Path.Combine(uploadsDir, avatarFilename);
                                
                                await File.WriteAllBytesAsync(avatarPath, imgBytes, ct);
                                _log.LogInformation("[registry-sync] Avatar saved to {AvatarPath}", avatarPath);
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "[registry-sync] Error syncing companion {CompName} from {RepoName}", companion.Name, repo.Name);
                        }
                    }

                    // Update last synced time in database
                    await _db.UpdateRepositoryLastSynced(repo.Id, DateTime.UtcNow.ToString("o"));
                    _log.LogInformation("[registry-sync] Finished syncing repository {RepoName}", repo.Name);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "[registry-sync] Error syncing repository {RepoName} ({RepoUrl})", repo.Name, repo.Url);
                }
            }
            _log.LogInformation("[registry-sync] Companion repository sync completed successfully.");
        }
        finally
        {
            _syncLock.Release();
        }
    }
}
