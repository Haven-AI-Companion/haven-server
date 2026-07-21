using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using AshServer.AI;

namespace AshServer.Controllers;

[ApiController]
[Route("api/admin/models")]
[Authorize]
public class ModelManagerController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly HardwareProfiler _profiler;
    private static readonly ConcurrentDictionary<string, DownloadStatusInfo> Downloads = new();
    
    private static readonly string UserProfileDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string GgufDir = Path.Combine(UserProfileDir, "gemma4-turbo-family");
    private static readonly string LoraDir = Path.Combine(UserProfileDir, "stable-diffusion-cpp", "models", "lora-models");

    public ModelManagerController(IConfiguration config, HardwareProfiler profiler)
    {
        _config = config;
        _profiler = profiler;
    }

    [HttpGet("active")]
    public IActionResult GetActiveModel()
    {
        try
        {
            return Ok(new
            {
                ok = true,
                activeGguf = _profiler.LlamaModel,
                isLlamaRunning = _profiler.IsLlamaRunning,
                isSdRunning = _profiler.IsSdRunning,
                llamaPid = _profiler.LlamaPid,
                sdPid = _profiler.SdPid
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to retrieve active model status: {ex.Message}" });
        }
    }

    [HttpPost("activate")]
    public async Task<IActionResult> ActivateModel([FromBody] ActivateModelRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.ModelFilename))
            return BadRequest(new { error = "ModelFilename is required." });

        var modelFilename = req.ModelFilename.Trim();
        var modelPath = Path.Combine(GgufDir, modelFilename);

        if (!System.IO.File.Exists(modelPath))
            return BadRequest(new { error = $"Model file '{modelFilename}' does not exist in {GgufDir}." });

        try
        {
            // 1. Update config.json on disk
            await UpdateActiveModelInConfig(modelFilename);

            // 2. Restart backend in the background so request completes instantly
            _ = Task.Run(async () =>
            {
                try
                {
                    Console.WriteLine("[ModelsAdmin] Stop requested to apply new model configuration...");
                    _profiler.StopLocalBackend();
                    
                    // Give process termination a moment to release handles/ports
                    await Task.Delay(2000);

                    Console.WriteLine($"[ModelsAdmin] Restarting local backends with active model: {modelFilename}...");
                    await _profiler.InitializeLocalBackendAsync();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ModelsAdmin] Error during backend restart: {ex.Message}");
                }
            });

            return Ok(new
            {
                ok = true,
                message = $"Active model set to '{modelFilename}'. The AI backend is restarting in the background."
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to activate model: {ex.Message}" });
        }
    }

    [HttpGet("installed")]
    public IActionResult GetInstalledModels()
    {
        try
        {
            var ggufDir = GgufDir;
            var loraDir = LoraDir;

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

        var destDir = modelType == "lora" ? LoraDir : GgufDir;

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
                
                var scriptPath = Path.Combine(UserProfileDir, "haven-server", "download_helper.py");
                var psi = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"\"{scriptPath}\" --repo-id \"{status.RepoId}\" --filename \"{status.Filename}\" --dest-dir \"{destDir}\"",
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

    [HttpGet("hf-status")]
    public async Task<IActionResult> GetHfStatus()
    {
        var cliInstalled = false;
        try
        {
            var checkPsi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c where huggingface-cli",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var checkProc = Process.Start(checkPsi);
            if (checkProc != null)
            {
                await checkProc.WaitForExitAsync();
                cliInstalled = checkProc.ExitCode == 0;
            }
        }
        catch {}

        try
        {
            var pythonPath = GetPythonExecutable();
            var psi = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = "-c \"import sys, json; " +
                            "try: " +
                            "  import huggingface_hub; " +
                            "  api = huggingface_hub.HfApi(); " +
                            "  try: " +
                            "    user = api.whoami(); " +
                            "    print(json.dumps({'installed': True, 'loggedIn': True, 'username': user.get('name')})); " +
                            "  except: " +
                            "    print(json.dumps({'installed': True, 'loggedIn': False})); " +
                            "except Exception as e: " +
                            "  print(json.dumps({'installed': False, 'loggedIn': False, 'error': str(e)}));\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return Ok(new { ok = true, status = new { installed = false, cliInstalled, loggedIn = false } });
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (string.IsNullOrWhiteSpace(output))
            {
                return Ok(new { ok = true, status = new { installed = false, cliInstalled, loggedIn = false } });
            }

            using var doc = JsonDocument.Parse(output);
            var statusObj = doc.RootElement;
            return Ok(new { 
                ok = true, 
                status = new { 
                    installed = statusObj.GetProperty("installed").GetBoolean(),
                    cliInstalled = cliInstalled,
                    loggedIn = statusObj.GetProperty("loggedIn").GetBoolean(),
                    username = statusObj.TryGetProperty("username", out var nameVal) ? nameVal.GetString() : null
                }
            });
        }
        catch (Exception ex)
        {
            return Ok(new { ok = true, status = new { installed = false, cliInstalled, loggedIn = false, error = ex.Message } });
        }
    }

    [HttpPost("hf-install")]
    public async Task<IActionResult> HfInstall()
    {
        try
        {
            var pythonPath = GetPythonExecutable();
            var psi = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = "-m pip install -U \"huggingface_hub[cli]\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return StatusCode(500, new { error = "Failed to start python installation process." });

            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                return Ok(new { ok = true, message = "Successfully installed Hugging Face CLI." });
            }
            else
            {
                var err = await process.StandardError.ReadToEndAsync();
                return BadRequest(new { error = string.IsNullOrWhiteSpace(err) ? $"Pip exited with error code {process.ExitCode}" : err.Trim() });
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("hf-login")]
    public async Task<IActionResult> HfLogin([FromBody] HfLoginRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Token))
            return BadRequest(new { error = "Hugging Face token is required." });

        try
        {
            var pythonPath = GetPythonExecutable();
            var psi = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = $"-c \"import sys; " +
                            "try: " +
                            "  from huggingface_hub import login; " +
                            $"  login(token='{req.Token.Trim()}'); " +
                            "  print('SUCCESS'); " +
                            "except Exception as e: " +
                            "  print('ERROR:', str(e));\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return StatusCode(500, new { error = "Failed to start python login process." });

            var output = (await process.StandardOutput.ReadToEndAsync()).Trim();
            await process.WaitForExitAsync();

            if (output == "SUCCESS")
            {
                return Ok(new { ok = true, message = "Successfully logged in to Hugging Face." });
            }
            else
            {
                var err = await process.StandardError.ReadToEndAsync();
                return BadRequest(new { error = string.IsNullOrWhiteSpace(err) ? output : err.Trim() });
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("search")]
    public async Task<IActionResult> SearchModels([FromQuery] string q)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { error = "Search query 'q' is required." });

        try
        {
            var pythonPath = GetPythonExecutable();
            var scriptPath = Path.Combine(UserProfileDir, "haven-server", "search_helper.py");

            var psi = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = $"\"{scriptPath}\" \"{q.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return StatusCode(500, new { error = "Failed to start search process." });

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                return BadRequest(new { error = string.IsNullOrWhiteSpace(error) ? "Search exited with code " + process.ExitCode : error.Trim() });
            }

            using var doc = JsonDocument.Parse(output);
            return Ok(new { ok = true, results = doc.RootElement });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    private static string GetPythonExecutable()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        
        // 1. Check AppData Local Python binary location directly (Windows custom installs)
        var pythonLocalPath = Path.Combine(localAppData, "Python", "bin", "python.exe");
        if (System.IO.File.Exists(pythonLocalPath)) return pythonLocalPath;
        
        // 2. Check AppData Programs Python location (standard installers)
        var programsPath = Path.Combine(localAppData, "Programs", "Python");
        if (Directory.Exists(programsPath))
        {
            var versions = Directory.GetDirectories(programsPath, "Python*");
            foreach (var v in versions)
            {
                var p = Path.Combine(v, "python.exe");
                if (System.IO.File.Exists(p)) return p;
            }
        }

        // 3. Fallback to path-based "python"
        return "python";
    }

    private async Task UpdateActiveModelInConfig(string modelFilename)
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "../../../config.json");
        if (!System.IO.File.Exists(configPath))
        {
            configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        }

        if (System.IO.File.Exists(configPath))
        {
            var jsonText = await System.IO.File.ReadAllTextAsync(configPath);
            var configNode = System.Text.Json.Nodes.JsonNode.Parse(jsonText);
            if (configNode != null)
            {
                var aiNode = configNode["ai"];
                if (aiNode == null)
                {
                    configNode["ai"] = new System.Text.Json.Nodes.JsonObject();
                    aiNode = configNode["ai"];
                }
                aiNode!["model"] = modelFilename;

                await System.IO.File.WriteAllTextAsync(configPath, configNode.ToString());
                
                if (_config is IConfigurationRoot root)
                {
                    root.Reload();
                }
            }
        }
    }
}

public class ActivateModelRequest
{
    public string ModelFilename { get; set; } = string.Empty;
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

public class HfLoginRequest
{
    public string Token { get; set; } = string.Empty;
}
