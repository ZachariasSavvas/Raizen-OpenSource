using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Raizen.Server.Core.Services;
using System.Security.Claims;

namespace Raizen.Server.Web.Pages.Auth;

[Authorize]
public class ChangePasswordModel(IAdminAuthService auth) : PageModel
{
    [BindProperty]
    public string NewPassword { get; set; } = string.Empty;

    [BindProperty]
    public string ConfirmPassword { get; set; } = string.Empty;

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var (valid, policyError) = IAdminAuthService.ValidatePasswordPolicy(NewPassword);
        if (!valid)
        {
            ModelState.AddModelError("", policyError!);
            return Page();
        }

        if (NewPassword != ConfirmPassword)
        {
            ModelState.AddModelError("", "Passwords do not match.");
            return Page();
        }

        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var userId))
            return RedirectToPage("/Auth/Login");

        try
        {
            await auth.ChangePasswordAsync(userId, NewPassword, ct);
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return Page();
        }

        // Re-issue the cookie without the must_change_pwd claim
        // Look up the user's role from DB to issue correct claims
        var users = await auth.ListAsync(ct);
        var currentUser = users.FirstOrDefault(u => u.Id == userId);
        var role = currentUser?.Role ?? "Admin";

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name,           User.Identity!.Name!),
            new(ClaimTypes.NameIdentifier, userIdStr!),
        };
        claims.AddRange(IAdminAuthService.GetRoleClaims(role));

        var identity  = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties { IsPersistent = true });

        return Redirect("/");
    }
}
