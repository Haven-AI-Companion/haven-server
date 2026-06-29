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
    private Process? _llamaProcess;
    private const int LocalPort = 11436;

    public HardwareProfiler(Database db, IConfiguration config)
    {
        _db = db;
        _config = config;
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

        // 2. Scan for local GGUF models in ./models
        var modelsDir = Path.Combine(AppContext.BaseDirectory, "models");
        if (!Directory.Exists(modelsDir))
        {
            Directory.CreateDirectory(modelsDir);
        }

        var ggufFiles = Directory.GetFiles(modelsDir, "*.gguf");
        if (ggufFiles.Length == 0)
        {
            Console.WriteLine($"[profiler] No local GGUF models found in '{modelsDir}'. Local server will not auto-start.");
            return;
        }

        var modelPath = ggufFiles[0]; // Auto-pick the first model
        Console.WriteLine($"[profiler] Found local model: {Path.GetFileName(modelPath)}");

        // 3. Locate llama-server executable
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "llama-server.exe" : "llama-server";
        var exePath = FindExecutable(exeName);

        if (string.IsNullOrEmpty(exePath))
        {
            Console.WriteLine("[profiler] Warning: 'llama-server' executable not found in ./bin or system PATH. Cannot auto-start local backend.");
            return;
        }

        // 4. Spawn llama-server in the background with --embedding enabled for local RAG
        string args = $"-m \"{modelPath}\" -c 2048 -t {profile.OptimalThreads} -ngl {profile.GpuLayers} --port {LocalPort} --embedding";
        Console.WriteLine($"[profiler] Spawning local backend: {exePath} {args}");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var binDir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrEmpty(binDir))
            {
                startInfo.EnvironmentVariables["LD_LIBRARY_PATH"] = binDir;
            }

            _llamaProcess = new Process { StartInfo = startInfo };
            _llamaProcess.Start();

            // Register the backend in the database if it doesn't exist
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
            Console.Error.WriteLine($"[profiler] Failed to start llama-server: {ex.Message}");
        }
    }

    private string? FindExecutable(string name)
    {
        // Check base directory
        var localPath = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(localPath)) return localPath;

        // Check ./bin directory
        var binPath = Path.Combine(AppContext.BaseDirectory, "bin", name);
        if (File.Exists(binPath)) return binPath;

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

    public void StopLocalBackend()
    {
        try
        {
            if (_llamaProcess != null && !_llamaProcess.HasExited)
            {
                _llamaProcess.Kill(true);
                _llamaProcess.Dispose();
                _llamaProcess = null;
                Console.WriteLine("[profiler] Stopped local llama-server process.");
            }
        }
        catch { }
    }

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
}
