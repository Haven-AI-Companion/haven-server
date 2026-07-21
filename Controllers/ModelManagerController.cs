using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace AshServer.Controllers;

[ApiController]
[Route("api/admin/models")]
[Authorize]
public class ModelManagerController : ControllerBase
{
    private readonly IConfiguration _config;
    private static readonly ConcurrentDictionary<string, DownloadStatusInfo> Downloads = new();

    public ModelManagerController(IConfiguration config)
    {
        _config = config;
    }

    [HttpGet("installed")]
    public IActionResult GetInstalledModels()
    {
        try
        {
            var ggufDir = @"C:\Users\admin\gemma4-turbo-family";
            var loraDir = @"C:\Users\admin\stable-diffusion-cpp\models\lora-models";

            var ggufFiles = new List<object>();
            if (Directory.Exists(ggufDir))
            {
                var files = Directory.GetFiles(ggufDir, "*.gguf", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    var info = new FileInfo(file);
                    ggufFiles.Add(new
                    {
                        filename = info.Name,
                        sizeBytes = info.Length,
                        lastModified = info.LastWriteTime.ToString("o")
                    });
                }
            }

            var loraFiles = new List<object>();
            if (Directory.Exists(loraDir))
            {
                var files = Directory.GetFiles(loraDir, "*.safetensors", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    var info = new FileInfo(file);
                    loraFiles.Add(new
                    {
                        filename = info.Name,
                        sizeBytes = info.Length,
                        lastModified = info.LastWriteTime.ToString("o")
                    });
                }
            }

            return Ok(new
            {
                ok = true,
                gguf = ggufFiles,
                lora = loraFiles
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to scan model directories: {ex.Message}" });
        }
    }

    [HttpPost("download")]
    public IActionResult DownloadModel([FromBody] DownloadRequestInfo req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.RepoId) || string.IsNullOrWhiteSpace(req.Filename))
            return BadRequest(new { error = "RepoId and Filename are required parameters." });

        var modelType = string.IsNullOrWhiteSpace(req.ModelType) ? "lora" : req.ModelType.Trim().ToLowerInvariant();
        if (modelType != "lora" && modelType != "gguf")
            return BadRequest(new { error = "ModelType must be either 'lora' or 'gguf'." });

        var taskKey = $"{req.RepoId}/{req.Filename}";
        
        // Return active status if already downloading
        if (Downloads.TryGetValue(taskKey, out var existingStatus) && existingStatus.State == "Downloading")
        {
            return Ok(new { ok = true, message = "Download is already in progress.", status = existingStatus });
        }

        var destDir = modelType == "lora"
            ? @"C:\Users\admin\stable-diffusion-cpp\models\lora-models"
            : @"C:\Users\admin\gemma4-turbo-family";

        var status = new DownloadStatusInfo
        {
            RepoId = req.RepoId.Trim(),
            Filename = req.Filename.Trim(),
            ModelType = modelType,
            Progress = "0%",
            State = "Pending",
            StartedAt = DateTime.UtcNow.ToString("o")
        };

        Downloads[taskKey] = status;

        // Execute python download helper script as a background process
        _ = Task.Run(async () =>
        {
            try
            {
                status.State = "Downloading";
                
                var psi = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"\"C:\\Users\\admin\\haven-server\\download_helper.py\" --repo-id \"{status.RepoId}\" --filename \"{status.Filename}\" --dest-dir \"{destDir}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                // Read standard output line by line to capture real-time progress percentages
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync();
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    if (line.StartsWith("PROGRESS:"))
                    {
                        status.Progress = line.Replace("PROGRESS:", "").Trim();
                        status.State = "Downloading";
                    }
                    else if (line.StartsWith("STATUS:"))
                    {
                        status.State = "Downloading";
                    }
                    else if (line.StartsWith("SUCCESS:"))
                    {
                        status.Progress = "100%";
                        status.State = "Completed";
                    }
                }

                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    var errorOutput = await process.StandardError.ReadToEndAsync();
                    status.State = "Failed";
                    status.ErrorMessage = string.IsNullOrWhiteSpace(errorOutput) 
                        ? $"Python process exited with error code {process.ExitCode}" 
                        : errorOutput.Trim();
                }
                else
                {
                    status.State = "Completed";
                    status.Progress = "100%";
                }
            }
            catch (Exception ex)
            {
                status.State = "Failed";
                status.ErrorMessage = ex.Message;
            }
        });

        return Ok(new
        {
            ok = true,
            message = "Download task triggered in background.",
            status = status
        });
    }

    [HttpGet("download/status")]
    public IActionResult GetDownloadStatus()
    {
        return Ok(new
        {
            ok = true,
            downloads = Downloads.Values.ToList()
        });
    }
}

public class DownloadRequestInfo
{
    public string RepoId { get; set; } = string.Empty;
    public string Filename { get; set; } = string.Empty;
    public string? ModelType { get; set; } // "lora" | "gguf"
}

public class DownloadStatusInfo
{
    public string RepoId { get; set; } = string.Empty;
    public string Filename { get; set; } = string.Empty;
    public string ModelType { get; set; } = string.Empty;
    public string Progress { get; set; } = "0%";
    public string State { get; set; } = "Pending";
    public string? ErrorMessage { get; set; }
    public string StartedAt { get; set; } = string.Empty;
}
