using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using AshServer.Data;
using AshServer.Models;

namespace AshServer.Auth;

public class AuthService
{
    private readonly Database _db;
    private readonly string _jwtSecret;
    private readonly int _tokenExpiryDays;

    public AuthService(Database db, IConfiguration config)
    {
        _db = db;
        _jwtSecret = config["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret not configured");
        _tokenExpiryDays = config.GetValue("Jwt:ExpiryDays", 30);
    }

    public string HashPassword(string password) => BCrypt.Net.BCrypt.HashPassword(password);

    public bool VerifyPassword(string password, string hash) => BCrypt.Net.BCrypt.Verify(password, hash);

    public async Task<(User? user, string? error)> Register(string username, string password, string? email)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length < 2)
            return (null, "Username must be at least 2 characters");
        if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
            return (null, "Password must be at least 6 characters");

        var existing = await _db.GetUserByUsername(username);
        if (existing != null) return (null, "Username already taken");

        // First user ever becomes admin
        var count = await _db.CountUsers();
        var isAdmin = count == 0;

        var hash = HashPassword(password);
        var user = await _db.CreateUser(username, hash, email, isAdmin);
        return (user, null);
    }

    public async Task<(User? user, string? error)> Login(string username, string password)
    {
        var user = await _db.GetUserByUsername(username);
        if (user == null) return (null, "Invalid credentials");
        if (!VerifyPassword(password, user.PasswordHash)) return (null, "Invalid credentials");
        return (user, null);
    }

    public string GenerateToken(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim("is_admin", user.IsAdmin ? "true" : "false"),
        };
        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddDays(_tokenExpiryDays),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static UserInfo ToInfo(User u, List<string>? roles = null, List<string>? permissions = null) =>
        new(u.Id, u.Username, u.Email, u.IsAdmin, u.CreatedAt, roles ?? [], permissions ?? [], u.DisplayName, u.Gender, u.AvatarPath);

    public async Task<UserInfo> ToInfoWithPerms(User u)
    {
        var roles = await _db.GetUserRoleNames(u.Id);
        var perms = u.IsAdmin
            ? new List<string>(AshServer.Auth.Permissions.All)
            : new List<string>(await _db.GetUserPermissions(u.Id));
        return ToInfo(u, roles, perms);
    }
}
