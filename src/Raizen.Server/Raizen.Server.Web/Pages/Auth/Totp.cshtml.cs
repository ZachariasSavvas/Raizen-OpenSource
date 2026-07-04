using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Raizen.Server.Core.Services;
using System.Collections.Concurrent;
using System.Security.Claims;

namespace Raizen.Server.Web.Pages.Auth;

[AllowAnonymous]
public class TotpModel(IAdminAuthService auth, IAuditService audit, IHttpContextAccessor httpContextAccessor, ILoginLockoutService lockouts) : PageModel
{
    // Track failed TOTP attempts per user ID to limit brute-force
    private static readonly ConcurrentDictionary<Guid, (int Count, DateTime LastAttempt)> _attempts = new();
    private const int MaxTotpAttempts = 5;

    [BindProperty]
    public string Code { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var pending = await HttpContext.AuthenticateAsync("raizen-totp-pending");
        if (!pending.Succeeded)
            return RedirectToPage("/Auth/Login");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var pending = await HttpContext.AuthenticateAsync("raizen-totp-pending");
        if (!pending.Succeeded)
            return RedirectToPage("/Auth/Login");

        var userIdStr = pending.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var userId))
            return RedirectToPage("/Auth/Login");

        var ip = httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Cleanup stale entries from abandoned TOTP flows
        var staleThreshold = DateTime.UtcNow.AddMinutes(-15);
        foreach (var key in _attempts.Keys)
            if (_attempts.TryGetValue(key, out var stale) && stale.LastAttempt < staleThreshold)
                _attempts.TryRemove(key, out _);

        // IP-based lockout check (shared with login page, persists across restarts)
        if (lockouts.IsLocked(ip))
        {
            await HttpContext.SignOutAsync("raizen-totp-pending");
            return RedirectToPage("/Auth/Login", new { error = "totp_failed" });
        }

        var code = Code?.Trim().Replace(" ", "") ?? string.Empty;
        var valid = await auth.VerifyTotpAsync(userId, code, ct);

        if (!valid)
        {
            await lockouts.RecordFailureAsync(ip, ct);
            var entry = _attempts.AddOrUpdate(userId,
                _ => (1, DateTime.UtcNow),
                (_, old) => (old.Count + 1, DateTime.UtcNow));
            var attempts = entry.Count;
            if (attempts >= MaxTotpAttempts)
            {
                _attempts.TryRemove(userId, out _);
                await audit.LogAsync("auth.login.failed", userIdStr ?? "unknown",
                    detail: $"TOTP:max-attempts IP:{ip}", ct: ct);
                await HttpContext.SignOutAsync("raizen-totp-pending");
                return RedirectToPage("/Auth/Login", new { error = "totp_failed" });
            }
            ErrorMessage = $"Invalid code. {MaxTotpAttempts - attempts} attempt(s) remaining.";
            return Page();
        }

        _attempts.TryRemove(userId, out _);
        await lockouts.ClearAsync(ip, ct);

        // Load the full user and issue complete auth cookie
        var user = await auth.ListAsync(ct).ContinueWith(t => t.Result.FirstOrDefault(u => u.Id == userId), ct);
        if (user is null)
        {
            await HttpContext.SignOutAsync("raizen-totp-pending");
            return RedirectToPage("/Auth/Login");
        }

        await HttpContext.SignOutAsync("raizen-totp-pending");
        await audit.LogAsync("auth.login", user.Username, detail: $"TOTP IP:{ip}", ct: ct);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name,           user.Username),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        };
        claims.AddRange(IAdminAuthService.GetRoleClaims(user.Role));
        if (user.MustChangePassword)
            claims.Add(new("must_change_pwd", "true"));

        var identity  = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        if (user.MustChangePassword)
            return RedirectToPage("/Auth/ChangePassword");

        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
    }
}
