using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace CIPAdmin.Backend.Services;

public interface ITokenService
{
    (string token, DateTime expiresAt, string jti) CreateToken(int empCode, string username, string empName, int roleId, string roleName);
    ClaimsPrincipal? ValidateToken(string token);
}

public class TokenService : ITokenService
{
    private readonly IConfiguration _config;

    public TokenService(IConfiguration config) => _config = config;

    public (string token, DateTime expiresAt, string jti) CreateToken(int empCode, string username, string empName, int roleId, string roleName)
    {
        var jwt = _config.GetSection("Jwt");
        var key = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(jwt["Secret"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var jti = Guid.NewGuid().ToString("N");
        var expires = DateTime.UtcNow.AddMinutes(double.Parse(jwt["ExpiryMinutes"] ?? "480"));

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, empCode.ToString()),
            new(JwtRegisteredClaimNames.Jti, jti),
            new("empCode", empCode.ToString()),
            new("username", username),
            new("empName", empName),
            new(ClaimTypes.Role, roleId.ToString()),
            new("roleName", roleName)
        };

        var token = new JwtSecurityToken(
            issuer: jwt["Issuer"],
            audience: jwt["Audience"],
            claims: claims,
            expires: expires,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires, jti);
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        var jwt = _config.GetSection("Jwt");
        var key = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(jwt["Secret"]!));
        var handler = new JwtSecurityTokenHandler();
        try
        {
            var principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwt["Issuer"],
                ValidAudience = jwt["Audience"],
                IssuerSigningKey = key,
                ClockSkew = TimeSpan.FromMinutes(1)
            }, out _);
            return principal;
        }
        catch
        {
            return null;
        }
    }
}

public interface ITotpService
{
    string GenerateSecret();
    string ComputeOtp(string base32Secret, DateTimeOffset? at = null);
    bool Verify(string base32Secret, string otp, int windowSteps = 1);
}

/// <summary>
/// RFC 6238 TOTP (Google Authenticator compatible) with Base32 secrets,
/// mirroring GOOGLE_AUTH_SECRET_KEY / IS_GOOGLE_AUTHENTICATED in TBL_CIP_EMPLOYEE_MASTER.
/// </summary>
public class TotpService : ITotpService
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const int StepSeconds = 30;

    public string GenerateSecret()
    {
        // 10 random bytes = 80 bits of entropy, which is what Google Authenticator uses.
        // Those must be encoded as 16 Base32 characters (5 bits per character). Taking one
        // character per byte instead - bytes[i] & 31 - would throw away 3 bits of each byte
        // and yield a 10-character / 50-bit secret, far weaker than it looks.
        var bytes = RandomNumberGenerator.GetBytes(10);
        return Base32Encode(bytes);
    }

    /// <summary>Standard Base32 (RFC 4648), no padding - matches the decoder below and
    /// the otpauth:// secrets that Google Authenticator itself produces.</summary>
    private static string Base32Encode(byte[] data)
    {
        var sb = new System.Text.StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                sb.Append(Base32Alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }
        if (bitsLeft > 0)
            sb.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 31]);
        return sb.ToString();
    }

    public string ComputeOtp(string base32Secret, DateTimeOffset? at = null)
    {
        var counter = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / StepSeconds;
        var key = Base32Decode(base32Secret);
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);

        using var hmac = new System.Security.Cryptography.HMACSHA1(key);
        var hash = hmac.ComputeHash(counterBytes);
        var offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    public bool Verify(string base32Secret, string otp, int windowSteps = 1)
    {
        if (string.IsNullOrWhiteSpace(otp) || otp.Trim().Length != 6) return false;
        var now = DateTimeOffset.UtcNow;
        for (int i = -windowSteps; i <= windowSteps; i++)
        {
            if (ComputeOtp(base32Secret, now.AddSeconds(i * StepSeconds)) == otp.Trim())
                return true;
        }
        return false;
    }

    private static byte[] Base32Decode(string input)
    {
        var clean = input.Trim().Replace(" ", "").ToUpperInvariant();
        int bitCount = 0, buffer = 0;
        var output = new List<byte>();
        foreach (var c in clean)
        {
            int val = Base32Alphabet.IndexOf(c);
            if (val < 0) continue;
            buffer = (buffer << 5) | val;
            bitCount += 5;
            if (bitCount >= 8)
            {
                output.Add((byte)(buffer >> (bitCount - 8)));
                bitCount -= 8;
            }
        }
        return output.ToArray();
    }
}
