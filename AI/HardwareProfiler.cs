using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AshServer.Data;

namespace AshServer.AI;

public class HardwareProfile
{
    public int CpuCores { get; set; }
    public bool HasCuda { get; set; }
    public double TotalRamGb { get; set; }
    public string RecommendedModelSize { get; set; } = "2b";
    public int OptimalThreads { get; set; }
    public int GpuLayers { get; set; }
}

public class HardwareProfiler
{
    private readonly Database _db;
    private readonly IConfiguration _config;
    private const int LocalPort = 11436;
    private Process? _llamaProcess;
    private Process? _sdProcess;

    private double _llamaCpu;
    private double _llamaRamMb;
    private double _sdCpu;
    private double _sdRamMb;

    public HardwareProfiler(Database db, IConfiguration config)
    {
        _db = db;
        _config = config;
        Task.Run(MonitorSidecarsPerformanceLoop);
    }

    public HardwareProfile ProfileSystem()
    {
        var profile = new HardwareProfile();

        // 1. Detect CPU Cores (prefer physical cores, fallback to logical / 2)
        int logicalCores = Environment.ProcessorCount;
        profile.CpuCores = Math.Max(1, logicalCores / 2);
        profile.OptimalThreads = profile.CpuCores;

        // 2. Detect RAM
        profile.TotalRamGb = GetTotalRamGb();
        if (profile.TotalRamGb < 8)
        {
            profile.RecommendedModelSize = "2b";
        }
        else if (profile.TotalRamGb <= 16)
        {
            profile.RecommendedModelSize = "9b"; // e.g. Gemma 4 Nano / 9B
        }
        else
        {
            profile.RecommendedModelSize = "27b"; // e.g. Gemma 4 Turbo / 27B
        }

        // 3. Detect NVIDIA CUDA GPU via nvidia-smi
        profile.HasCuda = DetectCuda();
        profile.GpuLayers = profile.HasCuda ? 999 : 0; // Offload all layers if GPU is present

        return profile;
    }

    private double GetTotalRamGb()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Use P/Invoke to call GlobalMemoryStatusEx for exact Windows RAM
                var memStatus = new MEMORYSTATUSEX();
                memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref memStatus))
                {
                    return memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Parse /proc/meminfo on Linux
                if (File.Exists("/proc/meminfo"))
                {
                    var lines = File.ReadAllLines("/proc/meminfo");
                    var memLine = lines.FirstOrDefault(l => l.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase));
                    if (memLine != null)
                    {
                        var parts = memLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 1 && long.TryParse(parts[1], out var kb))
                        {
                            return kb / (1024.0 * 1024.0);
                        }
                    }
                }
            }
        }
        catch { }

        // Fallback to GC total memory if system-level detection fails
        return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0 * 1024.0);
    }

    private bool DetectCuda()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "nvidia-smi.exe" : "nvidia-smi",
                Arguments = "-L", // List GPUs briefly
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                process.WaitForExit(2000);
                return process.ExitCode == 0;
            }
        }
        catch { }
        return false;
    }

    public async Task InitializeLocalBackendAsync()
    {
        // 1. Profile system
        var profile = ProfileSystem();
        Console.WriteLine($"[profiler] System profiled: {profile.CpuCores} CPU Cores, {profile.TotalRamGb:F1} GB RAM, CUDA GPU: {(profile.HasCuda ? "DETECTED" : "NOT DETECTED")}");
        Console.WriteLine($"[profiler] Recommended model size: {profile.RecommendedModelSize}, Threads: {profile.OptimalThreads}, GPU Layers: {profile.GpuLayers}");

        // Register the backend in the database if it doesn't exist
        try
        {
            var backends = await _db.GetAllBackends();
            var localBackendUrl = $"http://127.0.0.1:{LocalPort}";
            var exists = backends.Any(b => b.BaseUrl.TrimEnd('/') == localBackendUrl);

            if (!exists)
            {
                await _db.CreateBackend("Local Llama.cpp", "openai_compat", localBackendUrl, null);
                Console.WriteLine("[profiler] Registered 'Local Llama.cpp' in the database.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[profiler] Failed to register local backend in DB: {ex.Message}");
        }

        // 2. Auto-start llama-server sidecar
        if (IsPortInUse(LocalPort))
        {
            Console.WriteLine($"[profiler] Port {LocalPort} is already in use. Assuming llama-server is running externally.");
        }
        else
        {
            var execPath = FindExecutable("llama-server.exe");
            if (execPath != null)
            {
                var modelPath = FindModelGguf();
                if (modelPath != null)
                {
                    var mmprojPath = FindMmprojGguf(modelPath);
                    var gpuLayers = _config.GetValue<int?>("sidecars:llama:gpu_layers") ?? (profile.HasCuda ? profile.GpuLayers : 0);
                    var argsList = new System.Collections.Generic.List<string>
                    {
                        "--model", $"\"{modelPath}\"",
                        "--threads", _config.GetValue("sidecars:llama:threads", profile.OptimalThreads).ToString(),
                        "--ctx-size", _config.GetValue("sidecars:llama:context_size", 16384).ToString(),
                        "--n-gpu-layers", gpuLayers.ToString(),
                        "--host", "127.0.0.1",
                        "--port", LocalPort.ToString(),
                        "--batch-size", "512",
                        "--ubatch-size", "512",
                        "--n-predict", "-1",
                        "--log-disable"
                    };

                    if (!string.IsNullOrEmpty(mmprojPath))
                    {
                        argsList.Add("--mmproj");
                        argsList.Add($"\"{mmprojPath}\"");
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = execPath,
                        Arguments = string.Join(" ", argsList),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false
                    };

                    Console.WriteLine($"[profiler] Starting llama-server sidecar: {execPath} {psi.Arguments}");
                    try
                    {
                        _llamaProcess = Process.Start(psi);
                        if (_llamaProcess != null)
                        {
                            Console.WriteLine($"[profiler] llama-server started successfully (PID: {_llamaProcess.Id})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[profiler] Failed to start llama-server process: {ex.Message}");
                    }
                }
                else
                {
                    Console.Error.WriteLine("[profiler] llama-server executable found but no GGUF models found in C:\\Users\\admin\\gemma4-turbo-family");
                }
            }
            else
            {
                Console.Error.WriteLine("[profiler] llama-server.exe not found in PATH or search paths.");
            }
        }

        // 3. Auto-start Stable Diffusion sidecar
        await InitializeSdBackendAsync();
    }

    private bool IsPortInUse(int port)
    {
        try
        {
            var ipProperties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            var ipEndPoints = ipProperties.GetActiveTcpListeners();
            return ipEndPoints.Any(endPoint => 
                endPoint.Port == port && 
                (endPoint.Address.ToString() == "127.0.0.1" || endPoint.Address.ToString() == "0.0.0.0"));
        }
        catch
        {
            return false;
        }
    }

    private string? FindModelGguf()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "gemma4-turbo-family");
        if (!Directory.Exists(dir)) return null;

        // Try reading custom configured model from appsettings / config first
        var configuredModel = _config["ai:model"] ?? _config["sidecars:llama:active_model"];
        if (!string.IsNullOrWhiteSpace(configuredModel))
        {
            var p = Path.Combine(dir, configuredModel);
            if (File.Exists(p)) return p;

            if (File.Exists(configuredModel)) return configuredModel;
        }

        var candidates = new[]
        {
            "gemma4-e4b-merged-iq4xs-turbo.gguf",
            "gemma4-e4b-iq4xs-turbo.gguf",
            "gemma4-e2b-iq4xs-turbo.gguf"
        };

        foreach (var name in candidates)
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }

        var files = Directory.GetFiles(dir, "*.gguf");
        return files.FirstOrDefault(f => !f.Contains("mmproj"));
    }

    private string? FindMmprojGguf(string modelPath)
    {
        if (modelPath.Contains("merged")) return null; // Merged models are self-contained and don't need a separate --mmproj argument

        var dir = Path.GetDirectoryName(modelPath);
        if (dir == null) return null;

        if (modelPath.Contains("e4b"))
        {
            var p = Path.Combine(dir, "mmproj-e4b-f16.gguf");
            if (File.Exists(p)) return p;
        }
        else if (modelPath.Contains("e2b"))
        {
            var p = Path.Combine(dir, "mmproj-e2b-f16.gguf");
            if (File.Exists(p)) return p;
        }

        return null;
    }

    private string? FindExecutable(string name)
    {
        // Check base directory
        var localPath = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(localPath)) return localPath;

        // Check ./bin directory
        var binPath = Path.Combine(AppContext.BaseDirectory, "bin", name);
        if (File.Exists(binPath)) return binPath;

        // Check gemma4-turbo-family local path candidate
        var gemmaTurboPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "gemma4-turbo-family", "llama-cpp", name);
        if (File.Exists(gemmaTurboPath)) return gemmaTurboPath;

        // Check system PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var paths = pathEnv.Split(Path.PathSeparator);
        foreach (var path in paths)
        {
            try
            {
                var fullPath = Path.Combine(path, name);
                if (File.Exists(fullPath)) return fullPath;
            }
            catch { }
        }

        return null;
    }

    public async Task InitializeSdBackendAsync()
    {
        var sdPort = 8080;
        if (IsPortInUse(sdPort))
        {
            Console.WriteLine($"[profiler] Port {sdPort} is already in use. Assuming Stable Diffusion server is running externally.");
        }
        else
        {
            var sdDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "stable-diffusion-cpp");
            var execPath = Path.Combine(sdDir, "sd-server.exe");

            if (File.Exists(execPath))
            {
                var modelPath = Path.Combine(sdDir, @"models\DreamShaper8_LCM_q8_0.gguf");
                var taesdPath = Path.Combine(sdDir, @"models\taesd.safetensors");
                var embdDir = Path.Combine(sdDir, @"models\embeddings");

                if (File.Exists(modelPath))
                {
                    var steps = _config.GetValue("sidecars:stable_diffusion:steps", 8);
                    var sampling = _config.GetValue("sidecars:stable_diffusion:sampling_method", "lcm");
                    var cfgScale = _config.GetValue("sidecars:stable_diffusion:cfg_scale", 2.0);
                    var args = $"--model \"{modelPath}\" --taesd \"{taesdPath}\" --embd-dir \"{embdDir}\" --listen-ip 127.0.0.1 --listen-port {sdPort} --steps {steps} --sampling-method {sampling} --cfg-scale {cfgScale.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}";
                    
                    var psi = new ProcessStartInfo
                    {
                        FileName = execPath,
                        Arguments = args,
                        WorkingDirectory = sdDir,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false
                    };

                    Console.WriteLine($"[profiler] Starting Stable Diffusion sidecar: {execPath} with arguments: {args}");
                    try
                    {
                        _sdProcess = Process.Start(psi);
                        if (_sdProcess != null)
                        {
                            Console.WriteLine($"[profiler] Stable Diffusion started successfully (PID: {_sdProcess.Id})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[profiler] Failed to start Stable Diffusion sidecar: {ex.Message}");
                    }
                }
                else
                {
                    Console.Error.WriteLine($"[profiler] Stable Diffusion model not found at {modelPath}");
                }
            }
            else
            {
                Console.Error.WriteLine("[profiler] sd-server.exe not found at C:\\Users\\admin\\stable-diffusion-cpp\\sd-server.exe");
            }
        }
        await Task.CompletedTask;
    }

    public void StopLocalBackend()
    {
        try
        {
            if (_llamaProcess != null && !_llamaProcess.HasExited)
            {
                Console.WriteLine($"[profiler] Stopping llama-server sidecar (PID: {_llamaProcess.Id})...");
                _llamaProcess.Kill(true);
                _llamaProcess.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[profiler] Error killing llama-server sidecar: {ex.Message}");
        }

        try
        {
            if (_sdProcess != null && !_sdProcess.HasExited)
            {
                Console.WriteLine($"[profiler] Stopping Stable Diffusion sidecar (PID: {_sdProcess.Id})...");
                _sdProcess.Kill(true);
                _sdProcess.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[profiler] Error killing Stable Diffusion sidecar: {ex.Message}");
        }
    }

    public bool IsLlamaRunning => (_llamaProcess != null && !_llamaProcess.HasExited) || IsPortInUse(LocalPort);
    public bool IsSdRunning => (_sdProcess != null && !_sdProcess.HasExited) || IsPortInUse(8080);
    public int? LlamaPid => (_llamaProcess != null && !_llamaProcess.HasExited) ? _llamaProcess.Id : null;
    public int? SdPid => (_sdProcess != null && !_sdProcess.HasExited) ? _sdProcess.Id : null;

    public string LlamaModel => Path.GetFileName(FindModelGguf() ?? "None");
    public int LlamaContextSize => _config.GetValue("sidecars:llama:context_size", 16384);
    public string LlamaModelPath => FindModelGguf() ?? "";
    public int LlamaThreads => _config.GetValue("sidecars:llama:threads", ProfileSystem().OptimalThreads);
    public int LlamaGpuLayers => _config.GetValue<int?>("sidecars:llama:gpu_layers") ?? (ProfileSystem().HasCuda ? ProfileSystem().GpuLayers : 0);

    public string SdModel => "DreamShaper8_LCM_q8_0.gguf";
    public int SdSteps => _config.GetValue("sidecars:stable_diffusion:steps", 8);
    public string SdSampling => _config.GetValue("sidecars:stable_diffusion:sampling_method", "lcm");
    public double SdCfgScale => _config.GetValue("sidecars:stable_diffusion:cfg_scale", 2.0);

    // Win32 memory struct and P/Invoke
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public double LlamaCpu => _llamaCpu;
    public double LlamaRamMb => _llamaRamMb;
    public double SdCpu => _sdCpu;
    public double SdRamMb => _sdRamMb;

    public async Task StopLlamaAsync()
    {
        try
        {
            if (_llamaProcess != null && !_llamaProcess.HasExited)
            {
                _llamaProcess.Kill(true);
                await _llamaProcess.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[profiler] Error killing Llama process: {ex.Message}");
        }
        _llamaProcess = null;
        _llamaCpu = 0;
        _llamaRamMb = 0;
    }

    public async Task StopSdAsync()
    {
        try
        {
            if (_sdProcess != null && !_sdProcess.HasExited)
            {
                _sdProcess.Kill(true);
                await _sdProcess.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[profiler] Error killing SD process: {ex.Message}");
        }
        _sdProcess = null;
        _sdCpu = 0;
        _sdRamMb = 0;
    }

    private async Task MonitorSidecarsPerformanceLoop()
    {
        var lastLlamaTime = TimeSpan.Zero;
        var lastLlamaStamp = DateTime.UtcNow;
        var lastSdTime = TimeSpan.Zero;
        var lastSdStamp = DateTime.UtcNow;

        while (true)
        {
            try
            {
                await Task.Delay(3000);

                // Monitor Llama
                if (_llamaProcess != null && !_llamaProcess.HasExited)
                {
                    try
                     {
                        _llamaProcess.Refresh();
                        _llamaRamMb = Math.Round((double)_llamaProcess.WorkingSet64 / (1024 * 1024), 1);
                        
                        var now = DateTime.UtcNow;
                        var currentCpuTime = _llamaProcess.TotalProcessorTime;
                        var timeDiff = (now - lastLlamaStamp).TotalMilliseconds;
                        var cpuDiff = (currentCpuTime - lastLlamaTime).TotalMilliseconds;
                        
                        if (timeDiff > 0)
                        {
                            _llamaCpu = Math.Round((cpuDiff / (Environment.ProcessorCount * timeDiff)) * 100, 1);
                            _llamaCpu = Math.Min(100.0, Math.Max(0.0, _llamaCpu));
                        }
                        
                        lastLlamaTime = currentCpuTime;
                        lastLlamaStamp = now;
                    }
                    catch { }
                }
                else
                {
                    _llamaCpu = 0;
                    _llamaRamMb = 0;
                }

                // Monitor SD
                if (_sdProcess != null && !_sdProcess.HasExited)
                {
                    try
                    {
                        _sdProcess.Refresh();
                        _sdRamMb = Math.Round((double)_sdProcess.WorkingSet64 / (1024 * 1024), 1);
                        
                        var now = DateTime.UtcNow;
                        var currentCpuTime = _sdProcess.TotalProcessorTime;
                        var timeDiff = (now - lastSdStamp).TotalMilliseconds;
                        var cpuDiff = (currentCpuTime - lastSdTime).TotalMilliseconds;
                        
                        if (timeDiff > 0)
                        {
                            _sdCpu = Math.Round((cpuDiff / (Environment.ProcessorCount * timeDiff)) * 100, 1);
                            _sdCpu = Math.Min(100.0, Math.Max(0.0, _sdCpu));
                        }
                        
                        lastSdTime = currentCpuTime;
                        lastSdStamp = now;
                    }
                    catch { }
                }
                else
                {
                    _sdCpu = 0;
                    _sdRamMb = 0;
                }
            }
            catch { }
        }
    }
}

public static class LogStore
{
    private static readonly System.Collections.Generic.List<string> _logs = new();
    private static readonly object _lock = new();

    public static void Add(string message)
    {
        lock (_lock)
        {
            _logs.Add($"[{DateTime.UtcNow:HH:mm:ss}] {message}");
            if (_logs.Count > 50)
            {
                _logs.RemoveAt(0);
            }
        }
    }

    public static System.Collections.Generic.List<string> GetLogs()
    {
        lock (_lock)
        {
            return new System.Collections.Generic.List<string>(_logs);
        }
    }
}

public class MemoryLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
{
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new MemoryLogger(categoryName);
    public void Dispose() {}

    private class MemoryLogger : Microsoft.Extensions.Logging.ILogger
    {
        private readonly string _category;
        public MemoryLogger(string category) => _category = category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (exception != null) message += "\n" + exception;
            LogStore.Add($"[{logLevel}] {_category}: {message}");
        }
    }
}
