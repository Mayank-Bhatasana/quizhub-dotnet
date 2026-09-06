
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuizHub.Utils;

public static class AuthHelper
{

    public const string GuestCookieName = "quiz_guest";

    public const string AuthCookieName = "quiz_auth";

    public const int RoomCodeLength = 6;

    public static readonly TimeSpan AuthTokenTtl = TimeSpan.FromDays(7);

    public static readonly TimeSpan AuthRateWindow = TimeSpan.FromMinutes(10);

    public const int AuthRateMaxLogin = 20;

    public const int AuthRateMaxRegister = 8;


    private static readonly Dictionary<string, (int Count, DateTime ResetAt)> RateStore = new();

    private static readonly object RateLock = new();


    public static string SigningSecret =>
        Environment.GetEnvironmentVariable("AUTH_SIGNING_SECRET") ?? "dev-change-me";


    public static Dictionary<string, string> ParseCookies(string? header)
    {
        var d = new Dictionary<string, string>();

        if (string.IsNullOrEmpty(header))
            return d;

        foreach (var part in header.Split(';'))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0)
                continue;

            var k = part[..idx].Trim();

            var v = Uri.UnescapeDataString(part[(idx + 1)..].Trim());

            if (!string.IsNullOrEmpty(k))
                d[k] = v;
        }
        return d;
    }


    public static string GenerateGuestToken() =>
        Convert
            .ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');

    public static string GenerateRoomCode()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        var sb = new StringBuilder(RoomCodeLength);

        for (var i = 0; i < RoomCodeLength; i++)
            sb.Append(chars[RandomNumberGenerator.GetInt32(chars.Length)]);

        return sb.ToString();
    }


    public static void SetCookie(HttpResponse res, string name, string value, int? maxAgeSec = null)
    {
        var isProd = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Production";

        var sb = new StringBuilder(
            $"{name}={Uri.EscapeDataString(value)}; Path=/; HttpOnly; SameSite=Lax"
        );

        if (maxAgeSec is not null)
            sb.Append($"; Max-Age={maxAgeSec}");

        if (isProd)
            sb.Append("; Secure");

        res.Headers.Append("Set-Cookie", sb.ToString());
    }


    public static (string Token, long ExpiresAtMs) CreateAuthToken(
        Guid userId,
        Guid profileId,
        string email
    )
    {
        var exp =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)AuthTokenTtl.TotalMilliseconds;

        var payload = Convert
            .ToBase64String(
                Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(
                        new
                        {
                            userId,
                            profileId,
                            email,
                            exp,
                        }
                    )
                )
            )
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SigningSecret));
        var sig = Convert
            .ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)))
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');

        return ($"{payload}.{sig}", exp);
    }


    public static (Guid UserId, Guid ProfileId, string Email)? VerifyAuthToken(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 2)
            return null;

        var (payload, sig) = (parts[0], parts[1]);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SigningSecret));
        var expected = Convert
            .ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)))
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');

        if (
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(sig)
            )
        )
            return null;

        try
        {
            var pad = payload.Length % 4;
            var b64 =
                payload.Replace("-", "+").Replace("_", "/")
                + new string('=', pad == 0 ? 0 : 4 - pad);

            var doc = JsonDocument.Parse(Convert.FromBase64String(b64));
            var r = doc.RootElement;

            var exp = r.GetProperty("exp").GetInt64();
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= exp)
                return null;

            return (
                r.GetProperty("userId").GetGuid(),
                r.GetProperty("profileId").GetGuid(),
                r.GetProperty("email").GetString()!
            );
        }
        catch
        {
            return null;
        }
    }


    public static readonly Regex EmailRegex = new(
        @"^[^\s@]+@[^\s@]+\.[^\s@]+$",
        RegexOptions.Compiled
    );

    public static bool HasStrongPassword(string p) =>
        p.Length >= 8
        && p.Any(char.IsUpper)
        && p.Any(char.IsLower)
        && p.Any(char.IsDigit)
        && p.Any(c => !char.IsLetterOrDigit(c));


    public static (bool Limited, int RetryAfterSec) CheckRateLimit(string ip, string key, int max)
    {
        lock (RateLock)
        {
            var now = DateTime.UtcNow;

            var k = $"{key}:{ip}";

            if (!RateStore.TryGetValue(k, out var e) || e.ResetAt <= now)
            {
                RateStore[k] = (1, now + AuthRateWindow);
                return (false, 0);
            }

            if (e.Count >= max)
                return (true, Math.Max(1, (int)(e.ResetAt - now).TotalSeconds));

            RateStore[k] = (e.Count + 1, e.ResetAt);
            return (false, 0);
        }
    }


    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);

        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32);

        return $"pbkdf2$210000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        try
        {
            var parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2")
                return false;

            var iters = int.Parse(parts[1]);
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);

            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iters,
                HashAlgorithmName.SHA256,
                expected.Length
            );

            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch
        {
            return false;
        }
    }
}


public static class AvatarHelper
{
    private static readonly string[] Emojis = ["🧠", "⚡", "🌟", "🎯", "🧩", "🚀", "🍀", "🔥"];

    private static readonly string[] Bgs =
    [
        "bg-violet-200",
        "bg-sky-200",
        "bg-emerald-200",
        "bg-orange-200",
        "bg-pink-200",
        "bg-indigo-200",
        "bg-cyan-200",
        "bg-amber-200",
        "bg-rose-200",
        "bg-lime-200",
        "bg-teal-200",
    ];

    public static (string Emoji, string Bg) ForName(string id, string name)
    {
        var s = id + name;

        int hash = 0;
        foreach (var c in s)
            hash = c + ((hash << 5) - hash);

        return (Emojis[Math.Abs(hash) % Emojis.Length], Bgs[Math.Abs(hash + 1) % Bgs.Length]);
    }
}
