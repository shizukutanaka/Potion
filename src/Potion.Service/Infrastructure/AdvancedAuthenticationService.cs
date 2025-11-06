using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

namespace Potion.Service.Infrastructure;

/// <summary>
/// 認証・認可システムの強化サービス
/// JWTトークンとロールベースアクセス制御の強化を実装
/// </summary>
public interface IAdvancedAuthenticationService
{
    string GenerateJwtToken(string userId, string username, IEnumerable<string> roles, TimeSpan expiration);
    bool ValidateJwtToken(string token);
    ClaimsPrincipal GetClaimsFromToken(string token);
    bool HasPermission(string token, string permission);
    bool HasRole(string token, string role);
    Task<AuthenticationResult> AuthenticateAsync(string username, string password);
    Task<bool> ValidateApiKeyAsync(string apiKey);
}

/// <summary>
/// 認証結果
/// </summary>
public class AuthenticationResult
{
    public bool IsAuthenticated { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();
    public List<string> Permissions { get; set; } = new();
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}

/// <summary>
/// 高度な認証サービス実装
/// </summary>
public class AdvancedAuthenticationService : IAdvancedAuthenticationService
{
    private readonly ILogger<AdvancedAuthenticationService> _logger;
    private readonly string _jwtSecret;
    private readonly string _issuer;
    private readonly string _audience;

    // 権限マッピング（ロールベースアクセス制御）
    private static readonly Dictionary<string, List<string>> RolePermissions = new()
    {
        ["Admin"] = new List<string>
        {
            "user.read", "user.write", "user.delete",
            "system.read", "system.write", "system.delete",
            "audit.read", "audit.write",
            "security.read", "security.write"
        },
        ["Manager"] = new List<string>
        {
            "user.read", "user.write",
            "system.read",
            "audit.read",
            "security.read"
        },
        ["User"] = new List<string>
        {
            "user.read",
            "system.read"
        },
        ["Guest"] = new List<string>
        {
            "system.read"
        }
    };

    public AdvancedAuthenticationService(ILogger<AdvancedAuthenticationService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // 環境変数から設定を取得（実際の実装では設定ファイルから）
        _jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET") ?? GenerateSecureSecret();
        _issuer = Environment.GetEnvironmentVariable("JWT_ISSUER") ?? "Potion.Service";
        _audience = Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? "Potion.Clients";
    }

    public string GenerateJwtToken(string userId, string username, IEnumerable<string> roles, TimeSpan expiration)
    {
        try
        {
            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, userId),
                new Claim(JwtRegisteredClaimNames.Name, username),
                new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
                new Claim(JwtRegisteredClaimNames.Exp, DateTimeOffset.UtcNow.Add(expiration).ToUnixTimeSeconds().ToString()),
                new Claim(JwtRegisteredClaimNames.Iss, _issuer),
                new Claim(JwtRegisteredClaimNames.Aud, _audience)
            };

            // ロール情報を追加
            foreach (var role in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));

                // ロールに基づく権限を追加
                if (RolePermissions.TryGetValue(role, out var permissions))
                {
                    foreach (var permission in permissions)
                    {
                        claims.Add(new Claim("permission", permission));
                    }
                }
            }

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _issuer,
                audience: _audience,
                claims: claims,
                expires: DateTime.UtcNow.Add(expiration),
                signingCredentials: credentials
            );

            var tokenHandler = new JwtSecurityTokenHandler();
            var jwtToken = tokenHandler.WriteToken(token);

            _logger.LogDebug("Generated JWT token for user {UserId} with roles: {Roles}",
                userId, string.Join(", ", roles));

            return jwtToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating JWT token for user {UserId}", userId);
            throw new InvalidOperationException("Failed to generate JWT token", ex);
        }
    }

    public bool ValidateJwtToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));

            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = _issuer,
                ValidAudience = _audience,
                IssuerSigningKey = key,
                ClockSkew = TimeSpan.FromMinutes(5) // 5分間の猶予時間
            };

            var claimsPrincipal = tokenHandler.ValidateToken(token, validationParameters, out _);

            _logger.LogDebug("JWT token validated successfully");
            return true;
        }
        catch (SecurityTokenExpiredException ex)
        {
            _logger.LogWarning("JWT token expired: {Error}", ex.Message);
            return false;
        }
        catch (SecurityTokenInvalidSignatureException ex)
        {
            _logger.LogWarning("JWT token has invalid signature: {Error}", ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JWT token validation failed");
            return false;
        }
    }

    public ClaimsPrincipal GetClaimsFromToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Token cannot be empty", nameof(token));
        }

        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));

            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = _issuer,
                ValidAudience = _audience,
                IssuerSigningKey = key,
                ClockSkew = TimeSpan.FromMinutes(5)
            };

            var claimsPrincipal = tokenHandler.ValidateToken(token, validationParameters, out _);

            return claimsPrincipal;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting claims from JWT token");
            throw new InvalidOperationException("Failed to extract claims from token", ex);
        }
    }

    public bool HasPermission(string token, string permission)
    {
        try
        {
            var claimsPrincipal = GetClaimsFromToken(token);
            return claimsPrincipal.HasClaim("permission", permission);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking permission {Permission} for token", permission);
            return false;
        }
    }

    public bool HasRole(string token, string role)
    {
        try
        {
            var claimsPrincipal = GetClaimsFromToken(token);
            return claimsPrincipal.IsInRole(role);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking role {Role} for token", role);
            return false;
        }
    }

    public async Task<AuthenticationResult> AuthenticateAsync(string username, string password)
    {
        // 実際の実装ではデータベースや外部サービスで認証を行う
        // ここでは簡易的な実装として固定のユーザーを使用

        try
        {
            // デモ用の認証ロジック（実際の実装では安全な認証システムを使用）
            if (await ValidateCredentialsAsync(username, password))
            {
                var userRoles = await GetUserRolesAsync(username);
                var userPermissions = GetUserPermissions(userRoles);

                var token = GenerateJwtToken(
                    userId: $"user_{username}",
                    username: username,
                    roles: userRoles,
                    expiration: TimeSpan.FromHours(8)
                );

                return new AuthenticationResult
                {
                    IsAuthenticated = true,
                    UserId = $"user_{username}",
                    Username = username,
                    Roles = userRoles.ToList(),
                    Permissions = userPermissions.ToList(),
                    Token = token,
                    ExpiresAt = DateTime.UtcNow.AddHours(8)
                };
            }

            return new AuthenticationResult
            {
                IsAuthenticated = false,
                ErrorMessage = "Invalid credentials"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Authentication failed for user {Username}", username);

            return new AuthenticationResult
            {
                IsAuthenticated = false,
                ErrorMessage = "Authentication service error"
            };
        }
    }

    public async Task<bool> ValidateApiKeyAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        try
        {
            // APIキーの検証（実際の実装ではデータベースで確認）
            // ここでは簡易的なチェックとして長さと形式を確認

            if (apiKey.Length < 32)
            {
                return false;
            }

            // APIキーのフォーマットチェック（例: プレフィックス + ハッシュ）
            if (!apiKey.StartsWith("pot_"))
            {
                return false;
            }

            // 実際の実装ではデータベースで有効性を確認
            return await Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating API key");
            return false;
        }
    }

    private async Task<bool> ValidateCredentialsAsync(string username, string password)
    {
        // デモ用の認証（実際の実装ではセキュアな認証システムを使用）
        await Task.Delay(100); // タイミング攻撃対策のための遅延

        // 実際の実装ではパスワードハッシュの検証を行う
        var validCredentials = new Dictionary<string, string>
        {
            ["admin"] = "admin123", // 実際にはハッシュ化されたパスワードを使用
            ["user"] = "user123",
            ["guest"] = "guest123"
        };

        return validCredentials.TryGetValue(username.ToLowerInvariant(), out var expectedPassword) &&
               expectedPassword == password; // 実際にはタイミング攻撃耐性のある比較を使用
    }

    private async Task<IEnumerable<string>> GetUserRolesAsync(string username)
    {
        // 実際の実装ではデータベースからロールを取得
        var userRoles = new Dictionary<string, string[]>
        {
            ["admin"] = new[] { "Admin", "Manager" },
            ["manager"] = new[] { "Manager" },
            ["user"] = new[] { "User" },
            ["guest"] = new[] { "Guest" }
        };

        if (userRoles.TryGetValue(username.ToLowerInvariant(), out var roles))
        {
            return roles;
        }

        return new[] { "Guest" };
    }

    private IEnumerable<string> GetUserPermissions(IEnumerable<string> roles)
    {
        var permissions = new HashSet<string>();

        foreach (var role in roles)
        {
            if (RolePermissions.TryGetValue(role, out var rolePermissions))
            {
                foreach (var permission in rolePermissions)
                {
                    permissions.Add(permission);
                }
            }
        }

        return permissions;
    }

    private string GenerateSecureSecret()
    {
        var secretBytes = new byte[64];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(secretBytes);
        }

        return Convert.ToBase64String(secretBytes);
    }

    /// <summary>
/// JWTトークンヘルパー
/// </summary>
    public static class JwtTokenHelpers
    {
        public static string ExtractUserId(string token)
        {
            try
            {
                var handler = new JwtSecurityTokenHandler();
                var jwtToken = handler.ReadJwtToken(token);

                return jwtToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public static string ExtractUsername(string token)
        {
            try
            {
                var handler = new JwtSecurityTokenHandler();
                var jwtToken = handler.ReadJwtToken(token);

                return jwtToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Name)?.Value ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public static DateTime? GetExpirationTime(string token)
        {
            try
            {
                var handler = new JwtSecurityTokenHandler();
                var jwtToken = handler.ReadJwtToken(token);

                var expClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Exp);
                if (expClaim != null && long.TryParse(expClaim.Value, out var exp))
                {
                    return DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        public static bool IsTokenExpired(string token)
        {
            var expirationTime = GetExpirationTime(token);
            return expirationTime == null || expirationTime <= DateTime.UtcNow;
        }
    }
}
