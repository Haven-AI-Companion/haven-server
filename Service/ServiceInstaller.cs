using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AshServer.Service;

/// <summary>
/// Cross-platform service installer.
/// Windows  → Windows Service Control Manager (sc.exe)
/// Linux    → systemd unit file (/etc/systemd/system/ash-server.service)
/// macOS    → launchd plist (/Library/LaunchDaemons/com.ash-server.plist)
/// </summary>
public static class ServiceInstaller
{
    private const string ServiceName  = "haven-server";
    private const string DisplayName  = "Haven Server";
    private const string Description  = "Haven AI server — REST/WebSocket API, Discord bot, MCP integration.";

    // Resolved once at call time so install always points at the real binary.
    private static string ExePath => Process.GetCurrentProcess().MainModule?.FileName
        ?? throw new InvalidOperationException("Cannot determine executable path.");

    // ── Public entry points ────────────────────────────────────────────────

    public static void Install()
    {
        Console.WriteLine($"[haven-server] Installing service on {OsName()}…");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            InstallWindows();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            InstallLinux();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            InstallMacOs();
        else
            Die("Unsupported OS — please install the service manually.");
    }

    public static void Uninstall()
    {
        Console.WriteLine($"[haven-server] Uninstalling service on {OsName()}…");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            UninstallWindows();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            UninstallLinux();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            UninstallMacOs();
        else
            Die("Unsupported OS.");
    }

    public static void Status()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Run("sc.exe", $"query {ServiceName}");
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            Run("systemctl", $"status {ServiceName}");
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Run("launchctl", $"list com.{ServiceName}");
        else
            Die("Unsupported OS.");
    }

    // ── Windows ────────────────────────────────────────────────────────────

    private static void InstallWindows()
    {
        RequireAdmin();

        // Create the service
        Run("sc.exe", $"create {ServiceName} " +
            $"binPath= \"{ExePath}\" " +
            $"DisplayName= \"{DisplayName}\" " +
            $"start= auto " +
            $"obj= LocalSystem");

        // Set the description
        Run("sc.exe", $"description {ServiceName} \"{Description}\"");

        // Configure failure action: restart after 5 s, up to 3 times
        Run("sc.exe",
            $"failure {ServiceName} reset= 86400 " +
            $"actions= restart/5000/restart/5000/restart/5000");

        Console.WriteLine($"[haven-server] Service installed. Start with:  sc.exe start {ServiceName}");
    }

    private static void UninstallWindows()
    {
        RequireAdmin();
        Run("sc.exe", $"stop {ServiceName}");
        Run("sc.exe", $"delete {ServiceName}");
        Console.WriteLine($"[haven-server] Service removed.");
    }

    // ── Linux (systemd) ───────────────────────────────────────────────────

    private const string SystemdUnitPath = $"/etc/systemd/system/{ServiceName}.service";

    private static void InstallLinux()
    {
        RequireRoot();

        var workDir = Path.GetDirectoryName(ExePath) ?? "/opt/haven-server";
        var unit = $"""
[Unit]
Description={Description}
After=network.target
StartLimitIntervalSec=0

[Service]
Type=notify
ExecStart={ExePath}
WorkingDirectory={workDir}
Restart=on-failure
RestartSec=5
User=haven-server
# Uncomment if you want a dedicated user:
# DynamicUser=true

# Security hardening
NoNewPrivileges=true
ProtectSystem=strict
ReadWritePaths={workDir}

[Install]
WantedBy=multi-user.target
""";

        File.WriteAllText(SystemdUnitPath, unit);
        Run("systemctl", "daemon-reload");
        Run("systemctl", $"enable {ServiceName}");

        Console.WriteLine($"[haven-server] systemd unit installed at {SystemdUnitPath}");
        Console.WriteLine($"[haven-server] Start with:  sudo systemctl start {ServiceName}");
        Console.WriteLine($"[haven-server] Logs  with:  journalctl -u {ServiceName} -f");
    }

    private static void UninstallLinux()
    {
        RequireRoot();
        Run("systemctl", $"stop {ServiceName}");
        Run("systemctl", $"disable {ServiceName}");
        if (File.Exists(SystemdUnitPath)) File.Delete(SystemdUnitPath);
        Run("systemctl", "daemon-reload");
        Console.WriteLine($"[haven-server] systemd unit removed.");
    }

    // ── macOS (launchd) ──────────────────────────────────────────────────

    private const string LaunchDaemonLabel = $"com.{ServiceName}";
    private const string PlistPath = $"/Library/LaunchDaemons/{LaunchDaemonLabel}.plist";

    private static void InstallMacOs()
    {
        RequireRoot();

        var workDir = Path.GetDirectoryName(ExePath) ?? "/usr/local/opt/haven-server";
        var logDir  = $"/var/log/{ServiceName}";
        Directory.CreateDirectory(logDir);

        var plist = $"""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
    "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>{LaunchDaemonLabel}</string>

    <key>ProgramArguments</key>
    <array>
        <string>{ExePath}</string>
    </array>

    <key>WorkingDirectory</key>
    <string>{workDir}</string>

    <key>RunAtLoad</key>
    <true/>

    <key>KeepAlive</key>
    <true/>

    <key>StandardOutPath</key>
    <string>{logDir}/stdout.log</string>

    <key>StandardErrorPath</key>
    <string>{logDir}/stderr.log</string>

    <key>EnvironmentVariables</key>
    <dict>
        <key>ASPNETCORE_ENVIRONMENT</key>
        <string>Production</string>
    </dict>
</dict>
</plist>
""";

        File.WriteAllText(PlistPath, plist);
        // Fix ownership so launchd trusts it
        Run("chown", $"root:wheel {PlistPath}");
        Run("chmod", $"644 {PlistPath}");
        Run("launchctl", $"load -w {PlistPath}");

        Console.WriteLine($"[haven-server] launchd daemon installed at {PlistPath}");
        Console.WriteLine($"[haven-server] Start with:  sudo launchctl start {LaunchDaemonLabel}");
        Console.WriteLine($"[haven-server] Logs  with:  tail -f {logDir}/stdout.log");
    }

    private static void UninstallMacOs()
    {
        RequireRoot();
        Run("launchctl", $"unload -w {PlistPath}");
        if (File.Exists(PlistPath)) File.Delete(PlistPath);
        Console.WriteLine($"[haven-server] launchd daemon removed.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static void Run(string cmd, string args)
    {
        var psi = new ProcessStartInfo(cmd, args)
        {
            UseShellExecute  = false,
            CreateNoWindow   = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        using var proc = Process.Start(psi) ?? throw new Exception($"Failed to start {cmd}");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout.TrimEnd());
        if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr.TrimEnd());

        if (proc.ExitCode != 0)
            Console.WriteLine($"[warn] {cmd} exited with code {proc.ExitCode}");
    }

    private static void RequireAdmin()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(id);
        if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            Die("This command must be run as Administrator.");
    }

    private static void RequireRoot()
    {
        if (Environment.UserName != "root")
            Die("This command must be run as root (sudo ash-server install-service).");
    }

    private static string OsName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" :
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)   ? "Linux"   :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? "macOS"   : "Unknown";

    private static void Die(string msg)
    {
        Console.Error.WriteLine($"[error] {msg}");
        Environment.Exit(1);
    }
}
