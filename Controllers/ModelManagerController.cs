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
    public async Task<IActionResult> ActivateModel([FromBody] JsonElement body)
    {
        string? rawFilename = null;
        if (body.ValueKind == JsonValueKind.Object)
        {
            if (body.TryGetProperty("modelFilename", out var p1)) rawFilename = p1.GetString();
            else if (body.TryGetProperty("model_filename", out var p2)) rawFilename = p2.GetString();
            else if (body.TryGetProperty("ModelFilename", out var p3)) rawFilename = p3.GetString();
            else if (body.TryGetProperty("model", out var p4)) rawFilename = p4.GetString();
        }
        else if (body.ValueKind == JsonValueKind.String)
        {
            rawFilename = body.GetString();
        }

        if (string.IsNullOrWhiteSpace(rawFilename))
            return BadRequest(new { error = "ModelFilename is required." });

        var modelFilename = rawFilename;
        if (!modelFilename.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            modelFilename += ".gguf";
        }

        var modelPath = Path.Combine(GgufDir, Path.GetFileName(modelFilename));
        if (!System.IO.File.Exists(modelPath) && System.IO.File.Exists(rawFilename))
        {
            var targetFull = Path.GetFullPath(rawFilename);
            if (targetFull.StartsWith(Path.GetFullPath(GgufDir), StringComparison.OrdinalIgnoreCase))
            {
                modelPath = targetFull;
                modelFilename = Path.GetFileName(rawFilename);
            }
        }

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
                    
                    // Give process termination a moment to release handles/ports/VRAM
                    await Task.Delay(2000);

                    Console.WriteLine($"[ModelsAdmin] Restarting local backends with active model: {modelFilename}...");
                    await _profiler.InitializeLocalBackendAsync();

                    var hotswapEvent = new
                    {
                        type = "MODEL_HOTSWAPPED",
                        model = modelFilename,
                        timestamp = DateTime.UtcNow.ToString("o")
                    };
                    await AshServer.Chat.ChatHandler.BroadcastToAllSockets(hotswapEvent);
                    await AshServer.Service.SyncHub.BroadcastRawJson(System.Text.Json.JsonSerializer.Serialize(hotswapEvent));
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
        if (req == null)
        {
            Console.WriteLine("[download] Request body parsed as null!");
            return BadRequest(new { error = "Request body is null." });
        }

        Console.WriteLine($"[download] Received: RepoId='{req.RepoId}', Filename='{req.Filename}', ModelType='{req.ModelType}'");

        if (string.IsNullOrWhiteSpace(req.RepoId) || string.IsNullOrWhiteSpace(req.Filename))
        {
            return BadRequest(new { error = "RepoId and Filename are required parameters." });
        }

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
                var pythonPath = GetPythonExecutable();
                var psi = new ProcessStartInfo
                {
                    FileName = pythonPath,
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
            RefreshProcessPathEnvironment();
            Console.WriteLine("[hf-status] Initiating Hugging Face CLI path verification check...");

            // 1. Try standard 'where' command check
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
            Console.WriteLine($"[hf-status] Step 1: cmd.exe 'where' check. Succeeded={cliInstalled}");

            // 2. If 'where' fails, scan local python AppData directories for the Scripts executable
            if (!cliInstalled)
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var pythonDir = Path.Combine(localAppData, "Python");
                if (Directory.Exists(pythonDir))
                {
                    var found = SafeSearchCliExecutable(pythonDir);
                    Console.WriteLine($"[hf-status] Step 2: SafeSearchCliExecutable in '{pythonDir}'. Found={found}");
                    if (found)
                    {
                        cliInstalled = true;
                    }
                }
                else
                {
                    Console.WriteLine($"[hf-status] Step 2: Python AppData directory '{pythonDir}' does not exist.");
                }
            }

            // 3. Fallback to check if it's importable via python
            if (!cliInstalled)
            {
                var pythonPath = GetPythonExecutable();
                var pythonCheckPsi = new ProcessStartInfo
                {
                    FileName = pythonPath,
                    Arguments = "-c \"import huggingface_hub.cli.hf\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var pythonCheckProc = Process.Start(pythonCheckPsi);
                if (pythonCheckProc != null)
                {
                    await pythonCheckProc.WaitForExitAsync();
                    cliInstalled = pythonCheckProc.ExitCode == 0;
                }
                Console.WriteLine($"[hf-status] Step 3: Python import 'huggingface_hub.cli.hf' check using '{pythonPath}'. Succeeded={cliInstalled}");
            }
            
            Console.WriteLine($"[hf-status] Final CLI verification result: cliInstalled={cliInstalled}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[hf-status] CLI detection failed with exception: {ex.Message}");
        }

        try
        {
            var pythonPath = GetPythonExecutable();
            var scriptPath = Path.Combine(UserProfileDir, "haven-server", "hf_status_helper.py");
            var psi = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = $"\"{scriptPath}\"",
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
                Arguments = "-c \"import sys, os; " +
                            "try: " +
                            "  from huggingface_hub import login; " +
                            "  login(token=os.environ.get('HF_TOKEN')); " +
                            "  print('SUCCESS'); " +
                            "except Exception as e: " +
                            "  print('ERROR:', str(e));\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.EnvironmentVariables["HF_TOKEN"] = req.Token.Trim();

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

    [HttpGet("files")]
    public async Task<IActionResult> GetRepoFiles([FromQuery] string repoId)
    {
        if (string.IsNullOrWhiteSpace(repoId))
            return BadRequest(new { error = "Query parameter 'repoId' is required." });

        try
        {
            var pythonPath = GetPythonExecutable();
            var scriptPath = Path.Combine(UserProfileDir, "haven-server", "repo_files_helper.py");

            var psi = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = $"\"{scriptPath}\" \"{repoId.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return StatusCode(500, new { error = "Failed to start repository files query process." });

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                return BadRequest(new { error = string.IsNullOrWhiteSpace(error) ? "Files helper exited with code " + process.ExitCode : error.Trim() });
            }

            using var doc = JsonDocument.Parse(output);
            return Ok(new { ok = true, files = doc.RootElement.Clone() });
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
            return Ok(new { ok = true, results = doc.RootElement.Clone() });
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

    private static bool SafeSearchCliExecutable(string pythonDir)
    {
        try
        {
            if (!Directory.Exists(pythonDir)) return false;
            
            var target = Path.Combine(pythonDir, "huggingface-cli.exe");
            if (System.IO.File.Exists(target)) return true;
            
            foreach (var sub in Directory.GetDirectories(pythonDir))
            {
                try
                {
                    if (SafeSearchCliExecutable(sub)) return true;
                }
                catch {}
            }
        }
        catch {}
        return false;
    }

    private static void RefreshProcessPathEnvironment()
    {
        try
        {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                var userPath = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment")?.GetValue("Path")?.ToString() ?? "";
                var systemPath = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"System\CurrentControlSet\Control\Session Manager\Environment")?.GetValue("Path")?.ToString() ?? "";
                var combinedPath = $"{systemPath};{userPath}";
                Environment.SetEnvironmentVariable("PATH", combinedPath);
            }
        }
        catch {}
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

    [HttpGet("store")]
    public IActionResult GetModelCatalog()
    {
        var catalog = new[]
        {
            new
            {
                id = "gemma-4-2b-it-q4",
                name = "Gemma 4 Nano (2B Q4_K_M)",
                description = "Ultra-fast, lightweight companion model. Perfect for laptops, mobile devices, and low VRAM GPUs.",
                size_mb = 1450,
                recommended_ram_gb = 4,
                recommended_vram_gb = 2,
                filename = "gemma-4-2b-it-q4_k_m.gguf",
                url = "https://huggingface.co/google/gemma-2b-it-GGUF/resolve/main/gemma-2b-it.gguf"
            },
            new
            {
                id = "gemma-4-9b-turbo",
                name = "Gemma 4 Turbo (9B IQ4_XS)",
                description = "High speed, creative roleplay & natural dialogue companion model.",
                size_mb = 4200,
                recommended_ram_gb = 8,
                recommended_vram_gb = 6,
                filename = "gemma4-e4b-iq4xs-turbo.gguf",
                url = "https://huggingface.co/Haven-AI-Companion/haven-models/resolve/main/gemma4-e4b-iq4xs-turbo.gguf"
            },
            new
            {
                id = "haven-chat-v3",
                name = "Haven Companion v3.0 (Q4_K_M)",
                description = "Custom fine-tuned companion model optimized for deep empathy, memory recall, and warm banter.",
                size_mb = 4600,
                recommended_ram_gb = 8,
                recommended_vram_gb = 6,
                filename = "haven-chat-v3.0.gguf",
                url = "https://huggingface.co/Haven-AI-Companion/haven-models/resolve/main/haven-chat-v3.0.gguf"
            },
            new
            {
                id = "llama-3.2-3b-it",
                name = "Llama 3.2 Instruct (3B Q4_K_M)",
                description = "Meta's highly capable 3B instruct model. Intelligent, responsive, and versatile.",
                size_mb = 2020,
                recommended_ram_gb = 6,
                recommended_vram_gb = 4,
                filename = "Llama-3.2-3B-Instruct-Q4_K_M.gguf",
                url = "https://huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF/resolve/main/Llama-3.2-3B-Instruct-Q4_K_M.gguf"
            },
            new
            {
                id = "mistral-7b-instruct-v0.3",
                name = "Mistral 7B Instruct (Q4_K_M)",
                description = "High reasoning and roleplay capacity. Exceptional storytelling and intelligence.",
                size_mb = 4370,
                recommended_ram_gb = 10,
                recommended_vram_gb = 8,
                filename = "mistral-7b-instruct-v0.3.Q4_K_M.gguf",
                url = "https://huggingface.co/bartowski/Mistral-7B-Instruct-v0.3-GGUF/resolve/main/Mistral-7B-Instruct-v0.3-Q4_K_M.gguf"
            }
        };

        var profile = _profiler.ProfileSystem();
        return Ok(new
        {
            catalog,
            system_hardware = new
            {
                ram_gb = Math.Round(profile.TotalRamGb, 1),
                has_cuda = profile.HasCuda,
                recommended_model = profile.RecommendedModelSize
            }
        });
    }
}

public class ActivateModelRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("modelFilename")]
    public string? ModelFilename { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("model_filename")]
    public string? ModelFilenameSnake { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("model")]
    public string? ModelShort { get; set; }

    public string GetFilename()
    {
        if (!string.IsNullOrWhiteSpace(ModelFilename)) return ModelFilename.Trim();
        if (!string.IsNullOrWhiteSpace(ModelFilenameSnake)) return ModelFilenameSnake.Trim();
        if (!string.IsNullOrWhiteSpace(ModelShort)) return ModelShort.Trim();
        return string.Empty;
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

public class HfLoginRequest
{
    public string Token { get; set; } = string.Empty;
}
