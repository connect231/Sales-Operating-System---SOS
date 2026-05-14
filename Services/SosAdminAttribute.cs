using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace SOS.Services;

/// <summary>
/// Action / Controller'a uygulandığında, yalnızca TBLSOS_ADMIN_KULLANICI listesindeki
/// aktif email'lerin erişimine izin verir. Diğerleri 403 Forbidden döner.
/// Kullanım: [SosAdmin] sınıf veya metot üzerinde.
/// </summary>
public class SosAdminAttribute : TypeFilterAttribute
{
    public SosAdminAttribute() : base(typeof(SosAdminFilter)) { }
}

public class SosAdminFilter : IAsyncAuthorizationFilter
{
    private readonly IAdminAuthService _adminAuth;

    public SosAdminFilter(IAdminAuthService adminAuth)
    {
        _adminAuth = adminAuth;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            context.Result = new RedirectToActionResult("Login", "Account", new { returnUrl = context.HttpContext.Request.Path });
            return;
        }

        var email = user.Identity.Name;
        if (!await _adminAuth.IsAdminAsync(email))
        {
            context.Result = new ForbidResult();
        }
    }
}
