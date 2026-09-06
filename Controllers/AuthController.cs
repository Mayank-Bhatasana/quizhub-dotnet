
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuizHub.Data;
using QuizHub.Utils;

namespace QuizHub.Controllers;

[ApiController]
public class AuthController(AppDbContext db) : ControllerBase
{

    public record RegisterDto(string Email, string Username, string Password, string? AvatarUrl);

    public record LoginDto(string Email, string Password);

    public record GuestDto(string DisplayName, string? AvatarUrl);

    private static string Ip(HttpRequest r) =>
        r.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";


    [HttpPost("/api/auth/register")]
    public async Task<IActionResult> Register([FromBody] RegisterDto d)
    {
        var (limited, retry) = AuthHelper.CheckRateLimit(
            Ip(Request),
            "register",
            AuthHelper.AuthRateMaxRegister
        );
        if (limited)
        {
            Response.Headers.RetryAfter = retry.ToString();
            return StatusCode(
                429,
                new { error = "Too many register attempts. Please try again later." }
            );
        }

        var email = (d.Email ?? "").Trim().ToLowerInvariant();
        var username = (d.Username ?? "").Trim();
        var avatar = string.IsNullOrWhiteSpace(d.AvatarUrl) ? null : d.AvatarUrl.Trim();

        if (!AuthHelper.EmailRegex.IsMatch(email))
            return BadRequest(new { error = "Please enter a valid email" });

        if (username.Length < 2)
            return BadRequest(new { error = "Username must be at least 2 characters" });

        if (!AuthHelper.HasStrongPassword(d.Password ?? ""))
            return BadRequest(
                new
                {
                    error = "Password must have 8+ chars, uppercase, lowercase, number, and symbol",
                }
            );

        try
        {
            var user = new Models.User
            {
                Email = email,
                PasswordHash = AuthHelper.HashPassword(d.Password),
            };

            var profile = new Models.Profile
            {
                Username = username,
                AvatarUrl = avatar,
                IsTemporary = false,
                UserId = user.Id,
            };

            user.Profile = profile;

            db.Users.Add(user);
            db.Profiles.Add(profile);

            await db.SaveChangesAsync();

            var (token, exp) = AuthHelper.CreateAuthToken(user.Id, profile.Id, user.Email);

            AuthHelper.SetCookie(
                Response,
                AuthHelper.AuthCookieName,
                token,
                (int)((exp - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000)
            );

            return StatusCode(
                201,
                new { user = new { id = user.Id, email = user.Email }, profile }
            );
        }
        catch (DbUpdateException)
        {
            return Conflict(new { error = "Email already registered" });
        }
    }


    [HttpPost("/api/auth/login")]
    public async Task<IActionResult> Login([FromBody] LoginDto d)
    {
        var (limited, retry) = AuthHelper.CheckRateLimit(
            Ip(Request),
            "login",
            AuthHelper.AuthRateMaxLogin
        );
        if (limited)
        {
            Response.Headers.RetryAfter = retry.ToString();
            return StatusCode(
                429,
                new { error = "Too many login attempts. Please try again later." }
            );
        }

        var email = (d.Email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(d.Password))
            return BadRequest(new { error = "email and password are required" });

        var user = await db
            .Users.Include(u => u.Profile)
            .FirstOrDefaultAsync(u => u.Email == email);

        if (user is null || !AuthHelper.VerifyPassword(d.Password, user.PasswordHash))
            return Unauthorized(new { error = "Invalid email or password" });

        var profile = user.Profile ?? await EnsureProfileAsync(user.Id, user.Email);
        var (token, exp) = AuthHelper.CreateAuthToken(user.Id, profile.Id, user.Email);
        AuthHelper.SetCookie(
            Response,
            AuthHelper.AuthCookieName,
            token,
            (int)((exp - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000)
        );

        return Ok(new { user = new { id = user.Id, email = user.Email }, profile });
    }


    [HttpGet("/api/auth/me")]
    public async Task<IActionResult> Me()
    {
        var session = GetSession();
        if (session is null)
            return Unauthorized(new { error = "Unauthorized" });

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == session.Value.UserId);
        if (user is null)
        {
            AuthHelper.SetCookie(Response, AuthHelper.AuthCookieName, "", 0);
            return Unauthorized(new { error = "Unauthorized" });
        }

        var profile = await EnsureProfileAsync(user.Id, user.Email);
        return Ok(new { user = new { id = user.Id, email = user.Email }, profile });
    }


    [HttpPost("/api/auth/logout")]
    public IActionResult Logout()
    {
        AuthHelper.SetCookie(Response, AuthHelper.AuthCookieName, "", 0);
        return Ok(new { ok = true });
    }


    [HttpPost("/api/guest")]
    public async Task<IActionResult> Guest([FromBody] GuestDto d)
    {
        var cookies = AuthHelper.ParseCookies(Request.Headers.Cookie);

        if (cookies.TryGetValue(AuthHelper.GuestCookieName, out var tok))
        {
            var existing = await db.Profiles.FirstOrDefaultAsync(p => p.GuestToken == tok);
            if (existing is not null)
                return Ok(new { profile = existing });
        }

        var name = (d.DisplayName ?? "").Trim();
        if (string.IsNullOrEmpty(name))
            return BadRequest(new { error = "displayName is required" });

        var profile = new Models.Profile
        {
            Username = name,
            IsTemporary = true,
            GuestToken = AuthHelper.GenerateGuestToken(),
            AvatarUrl = string.IsNullOrWhiteSpace(d.AvatarUrl) ? null : d.AvatarUrl.Trim(),
        };
        db.Profiles.Add(profile);
        await db.SaveChangesAsync();

        AuthHelper.SetCookie(Response, AuthHelper.GuestCookieName, profile.GuestToken!);
        return Ok(new { profile });
    }


    private (Guid UserId, Guid ProfileId, string Email)? GetSession()
    {
        var cookies = AuthHelper.ParseCookies(Request.Headers.Cookie);

        if (!cookies.TryGetValue(AuthHelper.AuthCookieName, out var t))
            return null;

        return AuthHelper.VerifyAuthToken(t);
    }

    private async Task<Models.Profile> EnsureProfileAsync(Guid userId, string email)
    {
        var p = await db.Profiles.FirstOrDefaultAsync(x => x.UserId == userId);
        if (p is not null)
            return p;

        p = new Models.Profile
        {
            UserId = userId,
            Username = email.Split('@')[0],
            IsTemporary = false,
        };
        db.Profiles.Add(p);
        await db.SaveChangesAsync();
        return p;
    }
}
