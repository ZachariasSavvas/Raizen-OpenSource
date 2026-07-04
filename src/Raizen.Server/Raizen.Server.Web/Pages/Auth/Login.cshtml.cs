using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Raizen.Server.Core.Services;

namespace Raizen.Server.Web.Pages.Auth;

[AllowAnonymous]
public class LoginModel(IAdminAuthService auth, IHttpContextAccessor httpContextAccessor, IAuditService audit, ILoginLockoutService lockouts) : PageModel
{

    [BindProperty]
    [MaxLength(256)]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    [MaxLength(1024)]
    public string Password { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
            return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var ip = httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Check lockout
        if (lockouts.IsLocked(ip))
        {
            ErrorMessage = "Too many failed attempts. Please try again later.";
            return Page();
        }

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Please enter your username and password.";
            return Page();
        }

        // Reject oversized inputs before hashing — prevents CPU exhaustion via PBKDF2
        if (Username.Length > 256 || Password.Length > 1024)
        {
            ErrorMessage = "Invalid username or password.";
            return Page();
        }

        var user = await auth.ValidateAsync(Username.Trim(), Password, ct);
        if (user is null)
        {
            await lockouts.RecordFailureAsync(ip, ct);
            await audit.LogAsync("auth.login.failed", Username.Trim(), detail: $"IP:{ip}", ct: ct);
            ErrorMessage = "Invalid username or password.";
            return Page();
        }

        // Block login if the MFA enrolment deadline has passed
        if (user.RequiresMfa && !user.TotpEnabled
            && user.MfaDeadline.HasValue
            && DateTimeOffset.UtcNow > user.MfaDeadline.Value)
        {
            await lockouts.RecordFailureAsync(ip, ct);
            await audit.LogAsync("auth.login.blocked.mfa-deadline", user.Username, detail: $"IP:{ip}", ct: ct);
            ErrorMessage = $"Your account has been locked. MFA setup was required by " +
                           $"{user.MfaDeadline.Value.ToLocalTime():yyyy-MM-dd HH:mm} and was not completed. " +
                           $"Contact your administrator.";
            return Page();
        }

        // Force password change if password is older than 90 days
        if (!user.MustChangePassword && user.PasswordChangedAt.HasValue
            && (DateTimeOffset.UtcNow - user.PasswordChangedAt.Value).TotalDays > 90)
        {
            user.MustChangePassword = true;
            await audit.LogAsync("auth.password-expired", user.Username, detail: $"IP:{ip}", ct: ct);
        }

        // If TOTP is enabled, issue a short-lived pending cookie and redirect to TOTP step.
        // The full auth.login event is logged by Totp.cshtml.cs once 2FA completes.
        if (user.TotpEnabled)
        {
            var pendingIdentity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())],
                "raizen-totp-pending");
            await HttpContext.SignInAsync("raizen-totp-pending", new ClaimsPrincipal(pendingIdentity));
            return RedirectToPage("/Auth/Totp", new { ReturnUrl });
        }

        // No TOTP — session is fully established here; clear lockout now
        await lockouts.ClearAsync(ip, ct);
        await audit.LogAsync("auth.login", user.Username, detail: $"IP:{ip}", ct: ct);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name,           user.Username),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        };
        claims.AddRange(IAdminAuthService.GetRoleClaims(user.Role));

        if (user.MustChangePassword)
            claims.Add(new("must_change_pwd", "true"));

        // Flag that MFA enrolment is pending so the portal can show a banner
        if (user.RequiresMfa && !user.TotpEnabled && user.MfaDeadline.HasValue)
        {
            claims.Add(new("mfa_pending", "true"));
            claims.Add(new("mfa_deadline", user.MfaDeadline.Value.ToString("O")));
        }

        var identity  = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties { IsPersistent = true });

        if (user.MustChangePassword)
            return RedirectToPage("/Auth/ChangePassword");

        // MFA enrolment required — redirect to inline setup page (Microsoft-style)
        if (user.RequiresMfa && !user.TotpEnabled)
            return RedirectToPage("/Auth/MfaSetup", new { ReturnUrl = Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/" });

        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
    }
}
