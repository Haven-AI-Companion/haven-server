using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using System.Text;
using System.IdentityModel.Tokens.Jwt;
using AshServer.Auth;
using AshServer.Data;
using AshServer.Models;

namespace AshServer.Controllers;

[ApiController]
public class MobilePairingController : ControllerBase
{
    private readonly Database _db;
    private readonly AuthService _auth;
    private readonly IConfiguration _config;
    private readonly ILogger<MobilePairingController> _log;

    // Cache to hold pairing codes. Cleared after 5 minutes or upon consumption.
    private static readonly ConcurrentDictionary<string, MobilePairingSession> _pairingSessions = new();

    public MobilePairingController(Database db, AuthService auth, IConfiguration config, ILogger<MobilePairingController> log)
    {
        _db = db;
        _auth = auth;
        _config = config;
        _log = log;
    }

    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string Username => User.FindFirstValue(ClaimTypes.Name) ?? "User";

    [HttpPost("api/auth/mobile/pair/initiate")]
    [Authorize]
    public IActionResult InitiatePairing()
    {
        // 1. Generate a user-friendly 6-character code
        var code = GeneratePairingCode();
        var expiresAt = DateTime.UtcNow.AddMinutes(5);

        // 2. Cache it in-memory
        var session = new MobilePairingSession(UserId, Username, expiresAt);
        _pairingSessions[code] = session;

        // Clean up any stale sessions
        foreach (var key in _pairingSessions.Keys)
        {
            if (_pairingSessions.TryGetValue(key, out var s) && s.ExpiresAt < DateTime.UtcNow)
            {
                _pairingSessions.TryRemove(key, out _);
            }
        }

        // 3. Resolve active IPv4 network interface addresses
        var ips = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                
                var ipProps = ni.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) // IPv4
                    {
                        var ipStr = addr.Address.ToString();
                        if (ipStr != "127.0.0.1" && ipStr != "0.0.0.0")
                        {
                            ips.Add(ipStr);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to retrieve network interfaces for mobile pairing");
        }

        var port = _config.GetValue("Port", 18799);

        // 4. Formulate the QR payload
        var qrPayload = new
        {
            ips = ips.ToArray(),
            port = port,
            code = code,
            username = Username
        };

        return Ok(new
        {
            code = code,
            expires_at = expiresAt.ToString("o"),
            ips = ips,
            port = port,
            qr_data = JsonSerializer.Serialize(qrPayload)
        });
    }

    [HttpPost("api/auth/mobile/pair/confirm")]
    public async Task<IActionResult> ConfirmPairing([FromBody] MobilePairConfirmRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Code))
            return BadRequest(new { error = "Pairing code is required" });

        var code = req.Code.Trim().ToUpper();

        // 1. Consume code from store
        if (!_pairingSessions.TryRemove(code, out var session))
            return BadRequest(new { error = "Invalid or expired pairing code" });

        // 2. Validate expiration
        if (session.ExpiresAt < DateTime.UtcNow)
            return BadRequest(new { error = "Pairing code has expired" });

        // 3. Retrieve user profile
        var user = await _db.GetUserById(session.UserId);
        if (user == null)
            return NotFound(new { error = "User not found" });

        // 4. Issue a long-lived JWT token (365 days / 1 year)
        var token = GenerateLongLivedToken(user);
        
        _log.LogInformation("Successfully paired mobile device for user '{Username}' (ID: {UserId})", user.Username, user.Id);

        return Ok(new
        {
            ok = true,
            token = token,
            user = await _auth.ToInfoWithPerms(user)
        });
    }

    private string GeneratePairingCode()
    {
        // Alphanumeric subset excluding confusing characters (I, O, 1, 0)
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var random = new Random();
        return new string(Enumerable.Repeat(chars, 6)
            .Select(s => s[random.Next(s.Length)]).ToArray());
    }

    private string GenerateLongLivedToken(User user)
    {
        var secret = _config["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret not configured");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim("is_admin", user.IsAdmin ? "true" : "false"),
            new Claim("client_type", "mobile")
        };
        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddDays(365), // 1 year expiry
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public record MobilePairingSession(int UserId, string Username, DateTime ExpiresAt);
public record MobilePairConfirmRequest(string Code, string? DeviceName);
