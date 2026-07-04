using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using QRCoder;
using Raizen.Server.Core.Services;
using System.Security.Claims;

namespace Raizen.Server.Web.Pages.Auth;

[Authorize]
public class MfaSetupModel(IAdminAuthService auth, IAuditService audit) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public string Code { get; set; } = string.Empty;

    public string? QrBase64    { get; private set; }
    public string  ManualKey   { get; private set; } = string.Empty;
    public string? ErrorMessage { get; private set; }
    public int?    DaysLeft    { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var userId = GetUserId();
        if (userId is null) return RedirectToPage("/Auth/Login");

        // Already enrolled — nothing to set up
        var users = await auth.ListAsync();
        var user  = users.FirstOrDefault(u => u.Id == userId);
        if (user is null) return RedirectToPage("/Auth/Login");
        if (user.TotpEnabled) return LocalRedirect(SafeReturn());

        ComputeDaysLeft(user.MfaDeadline);
        await LoadQrAsync(userId.Value, user.Username);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return RedirectToPage("/Auth/Login");

        var users = await auth.ListAsync(ct);
        var user  = users.FirstOrDefault(u => u.Id == userId);
        if (user is null) return RedirectToPage("/Auth/Login");

        var code = Code?.Trim().Replace(" ", "") ?? string.Empty;
        var ok   = await auth.ConfirmTotpAsync(userId.Value, code, ct);

        if (!ok)
        {
            ErrorMessage = "Code is invalid or expired. Check your authenticator app and try again.";
            ComputeDaysLeft(user.MfaDeadline);
            await LoadQrAsync(userId.Value, user.Username);
            return Page();
        }

        await audit.LogAsync("auth.totp.enrolled", user.Username, ct: ct);

        // Re-issue the cookie without the mfa_pending claim
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name,           user.Username),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        };
        claims.AddRange(IAdminAuthService.GetRoleClaims(user.Role));
        if (user.MustChangePassword)
            claims.Add(new("must_change_pwd", "true"));

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = true });

        return LocalRedirect(SafeReturn());
    }

    private Guid? GetUserId()
    {
        var str = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(str, out var id) ? id : null;
    }

    private string SafeReturn() =>
        Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/";

    private void ComputeDaysLeft(DateTimeOffset? deadline)
    {
        if (deadline.HasValue)
        {
            var days = (int)Math.Ceiling((deadline.Value - DateTimeOffset.UtcNow).TotalDays);
            DaysLeft = Math.Max(0, days);
        }
    }

    private async Task LoadQrAsync(Guid userId, string username)
    {
        var info = await auth.GenerateTotpSetupAsync(userId, username);

        ManualKey = string.Join(" ", Enumerable.Range(0, (info.Base32Secret.Length + 3) / 4)
            .Select(i => info.Base32Secret.Substring(i * 4, Math.Min(4, info.Base32Secret.Length - i * 4))));

        using var qrGen  = new QRCodeGenerator();
        var qrData        = qrGen.CreateQrCode(info.OtpAuthUri, QRCodeGenerator.ECCLevel.M);
        using var qrCode  = new PngByteQRCode(qrData);
        QrBase64 = Convert.ToBase64String(qrCode.GetGraphic(6));
    }
}
