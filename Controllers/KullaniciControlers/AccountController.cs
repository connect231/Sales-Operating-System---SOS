using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.VisualStudio.Web.CodeGenerators.Mvc.Templates.BlazorIdentity.Pages.Manage;
using NuGet.Common;
using System.Security.Claims;
using System.Threading.Tasks;
using SOS.DbData;
using SOS.Models;
using SOS.Models.Kullanici;
using SOS.Models.Kullanici.Account;
using SOS.Models.MsK;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory;

namespace SOS.Controllers;

public class AccountController : Controller
{
    private UserManager<AppUser> _userManager;
    private SignInManager<AppUser> _signInManager;
    private IEmailService _emailService;

    private readonly MskDbContext _mskDb;
    private readonly Services.ICompanyResolutionService _companyResolution;
    private readonly Services.IUrlEncryptionService _urlEncryption;
    private readonly Services.ILogService _logService;
    private readonly Services.ILoginAktiviteService _loginAktivite;
    private readonly ILogger<AccountController> _logger;

    public AccountController(UserManager<AppUser> userManager, SignInManager<AppUser> signInManager, IEmailService emailService, MskDbContext mskDb, Services.ICompanyResolutionService companyResolution, Services.IUrlEncryptionService urlEncryption, Services.ILogService logService, Services.ILoginAktiviteService loginAktivite, ILogger<AccountController> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _emailService = emailService;
        _mskDb = mskDb;
        _companyResolution = companyResolution;
        _urlEncryption = urlEncryption;
        _logService = logService;
        _loginAktivite = loginAktivite;
        _logger = logger;
    }
    public ActionResult Create()
    {
        return View();
    }

    [HttpPost]
    public async Task<ActionResult> Create(AccountCreateModel model)
    {
        if (ModelState.IsValid)
        {
            var user = new AppUser { UserName = model.Email, Email = model.Email, AdSoyad = model.AdSoyad };

            var result = await _userManager.CreateAsync(user, model.Password);

            if (result.Succeeded)
            {
                AppUser? user2 = await _userManager.FindByEmailAsync(model.Email);
                if (user2 != null)
                {
                   
                    var confirmToken = await _userManager.GenerateEmailConfirmationTokenAsync(user2);
                   
                   
                    var url = Url.Action("EmailConfirmedToken", "Account", new { userId = user2.Id, confirmToken });

                    //var link = $"<a href='http://localhost:5162{url}'>Mail Adresini Doğrula</a>";
                    MailBody mb = new MailBody();

                    string sunucu = _mskDb.PARAMETRELERs.Where(i => i.ParametreAdi == "UYGULAMAROOTMAP").Select(i => i.Deger).FirstOrDefault();

                    var link = mb.dogrulamamail(user2.UserName!, sunucu + url);



                    await _emailService.SendEmailAsync(user2.Email!, "Email Doğrulama", link);

                    TempData["Mesaj"] = "Email Doğrulama Maili Gönredildi Mail Hesabınızı Kontrol Edin";

                }

                return RedirectToAction("Login"); 
            }

            foreach (var error in result.Errors)
            {
                ModelState.AddModelError("", error.Description);
            }
        }
        model.Password = null;
        model.ConfirmPassword = null;
        return View(model);
    }

    public ActionResult EmailConfirmToken()
    {
        return View();
    }

    [HttpPost]
    public async Task<ActionResult> EmailConfirmToken(string email)
    {
       
        AppUser? user2 = await _userManager.FindByEmailAsync(email);
        if (user2 != null)
        {

            var confirmToken = await _userManager.GenerateEmailConfirmationTokenAsync(user2);


            var url = Url.Action("EmailConfirmedToken", "Account", new { userId = user2.Id, confirmToken });

            string sunucu = _mskDb.PARAMETRELERs.Where(i => i.ParametreAdi == "UYGULAMAROOTMAP").Select(i => i.Deger).FirstOrDefault();

            MailBody mb = new MailBody();

            var link = mb.dogrulamamail(user2.UserName!, sunucu + url); 
                //$"<a href='http://localhost:5162{url}'>Mail Adresini Doğrula</a>";

            await _emailService.SendEmailAsync(user2.Email!, "Email Doğrulama", link);

            TempData["Mesaj"] = "Email Doğrulama Maili Gönredildi Mail Hesabınızı Kontrol Edin";

        }
        else
        {
            TempData["Mesaj"] = "Email Adresine Bağlı Kullanıcı Bulunamadı";

            return RedirectToAction("Login");
        }

        return RedirectToAction("Login");
    }


    public async Task<ActionResult> EmailConfirmedToken(string userId, string confirmToken)
    {
        AppUser? user = await _userManager.FindByIdAsync(userId);
        if (user != null)
        {
           
            IdentityResult result = await _userManager.ConfirmEmailAsync(user, confirmToken);
            if (result.Succeeded)
            {

                await _userManager.UpdateSecurityStampAsync(user);
                TempData["Mesaj"] = "Email Adresiniz Doğrulandı";
            }
                
            
        }
        return RedirectToAction("Login");
    }

    [HttpPost]
    public async Task<IActionResult> PreLoginCheck([FromBody] AccountLoginModel model)
    {
        try
        {
            // Şifresiz email-only — sadece email doğrulanır.
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null) return Json(new { success = false, message = "Bu e-posta sistemde kayıtlı değil." });

            // Get User Type
            var dbUser = await _mskDb.TBL_KULLANICIs.AsNoTracking().FirstOrDefaultAsync(u => u.LNGIDENTITYKOD == user.Id);
            int type = dbUser?.LNGKULLANICITIPI ?? 0;

            // Check for ForcePasswordChange claim
            var userClaims = await _userManager.GetClaimsAsync(user);
            if (userClaims.Any(c => c.Type == "ForcePasswordChange" && c.Value == "true"))
            {
                // Force change password BEFORE company selection
                return Json(new { success = true, forceChange = true });
            }

            if (type == 1) // Admin - can select from all companies
            {
                var projects = await _mskDb.VIEW_ORTAK_PROJE_ISIMLERIs
                                        .OrderBy(x => x.TXTORTAKPROJEADI)
                                        .Select(x => new { id = x.LNGKOD, name = x.TXTORTAKPROJEADI })
                                        .ToListAsync();

                // [FIX] Add "All Companies" Option
                var allOption = new { id = -1, name = "Tüm Firmalar", encryptedId = _urlEncryption.EncryptId(-1) }; 
                var projectList = new List<object> { allOption };
                
                // Add Encrypted ID
                var mappedProjects = projects.Select(x => new { id = x.id, name = x.name, encryptedId = _urlEncryption.EncryptId(x.id) }).ToList();
                projectList.AddRange(mappedProjects);
                
                return Json(new { success = true, type = 1, projects = projectList });
            }
            else if (type == 3 || type == 4) // Univera Internal/Customer - Select from Authorized Companies
            {
                var authorizedIndices = await _mskDb.TBL_KULLANICI_FIRMAs
                                        .Where(f => f.LNGKULLANICIKOD == dbUser.LNGKOD)
                                        .Select(f => f.LNGFIRMAKOD)
                                        .ToListAsync();

                var projects = await _mskDb.VIEW_ORTAK_PROJE_ISIMLERIs
                                        .Where(f => authorizedIndices.Contains(f.LNGKOD))
                                        .OrderBy(x => x.TXTORTAKPROJEADI)
                                        .Select(x => new { id = x.LNGKOD, name = x.TXTORTAKPROJEADI })
                                        .ToListAsync();
                 
                 var mappedProjects = projects.Select(x => new { id = x.id, name = x.name, encryptedId = _urlEncryption.EncryptId(x.id) }).ToList();
                 
                 // Add "All Companies" Option
                 var allOption = new { id = -1, name = "Tüm Firmalar", encryptedId = _urlEncryption.EncryptId(-1) }; 
                 var projectList = new List<object> { allOption };
                 projectList.AddRange(mappedProjects);
                 
                 return Json(new { success = true, type = type, projects = projectList });
            }

            // Customer (Type 2 or others) - direct login
            return Json(new { success = true, type = type });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PreLoginCheck sırasında hata oluştu");
            return Json(new { success = false, message = "İşlem sırasında bir hata oluştu. Lütfen daha sonra tekrar deneyiniz." });
        }
    }

    public async Task<ActionResult> Login()
    {
        if (User?.Identity?.IsAuthenticated ?? false)
        {
             var user = await _userManager.GetUserAsync(User);
             if (user != null)
                 return await RedirectToAuthorizedPage(user);
        }

        return View();
    }

    [HttpPost]
    public async Task<ActionResult> Login(AccountLoginModel model, string? returnUrl, int? projectCode)
    {
        // Şifresiz email-only login — Password kontrol yok.
        if (string.IsNullOrWhiteSpace(model?.Email))
        {
            TempData["Mesaj"] = "E-posta adresinizi giriniz";
            return View(model);
        }

        var email = model.Email.Trim();
        var user = await _userManager.FindByEmailAsync(email)
                   ?? await _userManager.FindByNameAsync(email);

        if (user == null)
        {
            // TBL_KULLANICI'da TXTEMAIL ile aramayı da dene
            var dbHit = await _mskDb.TBL_KULLANICIs.AsNoTracking()
                .Where(x => x.TXTEMAIL == email && x.LNGIDENTITYKOD.HasValue)
                .Select(x => x.LNGIDENTITYKOD!.Value)
                .FirstOrDefaultAsync();
            if (dbHit > 0)
            {
                user = await _userManager.FindByIdAsync(dbHit.ToString());
            }
        }

        // Fallback: TBL_VARUNA_PERSON.Email ile eşleşme — varsa AspNetUser otomatik oluştur
        TBL_VARUNA_PERSON? varunaPerson = null;
        if (user == null)
        {
            varunaPerson = await _mskDb.TBL_VARUNA_PERSONs.AsNoTracking()
                .Where(p => p.Email == email && p.DeletedOn == null)
                .OrderByDescending(p => p.ModifiedOn)
                .FirstOrDefaultAsync();

            if (varunaPerson != null)
            {
                var fullName = !string.IsNullOrWhiteSpace(varunaPerson.PersonNameSurname)
                    ? varunaPerson.PersonNameSurname!
                    : ($"{varunaPerson.Name} {varunaPerson.SurName}").Trim();

                var newUser = new AppUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    AdSoyad = string.IsNullOrWhiteSpace(fullName) ? email : fullName
                };
                var createResult = await _userManager.CreateAsync(newUser);
                if (createResult.Succeeded)
                {
                    user = newUser;

                    // TBL_KULLANICI'da yoksa varsayılan tip (0) ile oluştur — Varuna Person üzerinden ilk giriş
                    var alreadyDb = await _mskDb.TBL_KULLANICIs.AnyAsync(x => x.LNGIDENTITYKOD == newUser.Id);
                    if (!alreadyDb)
                    {
                        _mskDb.TBL_KULLANICIs.Add(new TBL_KULLANICI
                        {
                            LNGIDENTITYKOD = newUser.Id,
                            TXTEMAIL = email,
                            TXTADSOYAD = newUser.AdSoyad,
                            LNGKULLANICITIPI = 0
                        });
                        await _mskDb.SaveChangesAsync();
                    }

                    await _logService.LogAsync("USER_AUTOPROVISION", $"Varuna Person'dan otomatik kullanıcı oluşturuldu: {email}", "ACCOUNT");
                }
            }
        }

        if (user == null)
        {
            TempData["Mesaj"] = "Bu e-posta sistemde kayıtlı değil.";
            await _logService.LogAsync("LOGIN_FAILED", $"Kayıtsız email denemesi: {email}", "ACCOUNT");
            return View(model);
        }

        var dbUser = await _mskDb.TBL_KULLANICIs.AsNoTracking()
            .Where(x => x.LNGIDENTITYKOD == user.Id)
            .Select(x => new { x.LNGKOD, x.TXTADSOYAD, x.TXTFIRMAADI, x.LNGKULLANICITIPI })
            .FirstOrDefaultAsync();

        var claims = new List<Claim>();
        if (dbUser != null)
        {
            if (!string.IsNullOrEmpty(dbUser.TXTFIRMAADI))
                claims.Add(new Claim("FirmaAdi", dbUser.TXTFIRMAADI));
            claims.Add(new Claim("UserType", dbUser.LNGKULLANICITIPI?.ToString() ?? "0"));
            if (!string.IsNullOrEmpty(dbUser.TXTADSOYAD))
                claims.Add(new Claim("AdSoyad", dbUser.TXTADSOYAD));
        }
        else
        {
            claims.Add(new Claim("UserType", "0"));
        }

        await _signInManager.SignInWithClaimsAsync(user, false, claims);
        await _userManager.ResetAccessFailedCountAsync(user);
        await _userManager.SetLockoutEndDateAsync(user, null);

        // Aktivite kaydı — login event
        await _loginAktivite.RecordLoginAsync(
            kullaniciId: user.Id,
            email: user.Email ?? email,
            adSoyad: dbUser?.TXTADSOYAD ?? user.UserName,
            ipAdresi: HttpContext.Connection.RemoteIpAddress?.ToString(),
            userAgent: Request.Headers["User-Agent"].ToString());

        await _logService.LogAsync("LOGIN_SUCCESS", $"Kullanıcı giriş yaptı: {user.UserName}", "ACCOUNT");

        // Satışçı giriş uyarısı popup'ı her başarılı login'de tetiklensin diye
        // _Layout, TempData["FreshLogin"] dolu olduğunda popup dedupe flag'ini (localStorage) temizler.
        // Yan etki: doğal logout sonrası Clear-Site-Data zaten localStorage temizliyor; ama session
        // timeout → otomatik /Account/Login akışı için gerekli (logout tetiklenmiyor).
        TempData["FreshLogin"] = "1";

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        if (projectCode.HasValue)
        {
            return await RedirectToAuthorizedPage(user, projectCode.Value);
        }

        return await RedirectToAuthorizedPage(user);
    }

    public async Task<ActionResult> LogOut()
    {
        // Aktivite kaydı — logout event (Authorize kaldırıldı, expired session da çıkış yapabilsin)
        if (User?.Identity?.IsAuthenticated ?? false)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (int.TryParse(userIdStr, out var uid))
            {
                try { await _loginAktivite.RecordLogoutAsync(uid); } catch { /* yutulur */ }
            }
            try { await _logService.LogAsync("LOGOUT", "Kullanıcı çıkış yaptı.", "ACCOUNT"); } catch { }
        }

        // 1) Identity SignOut — cookie max-age=0 ile süresiz expire eder
        await _signInManager.SignOutAsync();

        // 2) Bilinen cookie'leri açıkça temizle (bazı reverse proxy/cache senaryolarında SignOut yetmez)
        var allCookies = Request.Cookies.Keys.ToList();
        var cookieOpts = new Microsoft.AspNetCore.Http.CookieOptions
        {
            Expires = DateTimeOffset.UnixEpoch,
            Path = "/",
            SameSite = SameSiteMode.Lax
        };
        foreach (var c in allCookies)
        {
            // Identity, antiforgery, session ve uygulama özel cookie'leri
            if (c.StartsWith(".AspNetCore.", StringComparison.OrdinalIgnoreCase)
                || c.StartsWith(".AspNet.", StringComparison.OrdinalIgnoreCase)
                || c.Equals("duyuru_goruldu", StringComparison.OrdinalIgnoreCase))
            {
                Response.Cookies.Delete(c, cookieOpts);
            }
        }

        // 3) Tarayıcı tarafında storage + cache temizliği
        Response.Headers["Clear-Site-Data"] = "\"cache\", \"cookies\", \"storage\"";
        Response.Headers["Cache-Control"]   = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["Pragma"]          = "no-cache";

        return RedirectToAction("Login", "Account");
    }

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Heartbeat()
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdStr, out var uid)) return Json(new { ok = false });
        await _loginAktivite.RecordHeartbeatAsync(uid);
        return Json(new { ok = true, t = DateTime.Now.ToString("HH:mm:ss") });
    }

    [Authorize]
    public ActionResult Settings()
    {
        return View();
    }

    [Authorize]
    public async Task<ActionResult> EditUser()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = await _userManager.FindByIdAsync(userId!);

        if (user == null)
        {
            return RedirectToAction("Login", "Account");
        }

        return View(new AccountEditUserModel
        {
            AdSoyad = user.AdSoyad,
            Email = user.Email!
        });
    }

    [HttpPost]
    [Authorize]
    public async Task<ActionResult> EditUser(AccountEditUserModel model)
    {
        if (ModelState.IsValid)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var user = await _userManager.FindByIdAsync(userId!);

            if (user != null)
            {
                user.Email = model.Email;
                user.AdSoyad = model.AdSoyad;

                var result = await _userManager.UpdateAsync(user);

                if (result.Succeeded)
                {
                    TempData["Mesaj"] = "Bilgileriniz güncellendi";
                }

                foreach (var error in result.Errors)
                {
                    ModelState.AddModelError("", error.Description);
                }
            }
        }
        return View(model);
    }

    [Authorize]
    public ActionResult ChangePassword()
    {
        return View();
    }

    [Authorize]
    [HttpPost]
    public async Task<ActionResult> ChangePassword(AccountChangePasswordModel model)
    {
        if (ModelState.IsValid)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var user = await _userManager.FindByIdAsync(userId!);

            if (user != null)
            {
                var result = await _userManager.ChangePasswordAsync(user, model.OldPassword, model.Password);

                if (result.Succeeded)
                {
                    // Remove ForcePasswordChange claim if exists
                    var claims = await _userManager.GetClaimsAsync(user);
                    var forceClaim = claims.FirstOrDefault(c => c.Type == "ForcePasswordChange");
                    if (forceClaim != null)
                    {
                        await _userManager.RemoveClaimAsync(user, forceClaim);
                    }

                    TempData["Mesaj"] = "Parolanız güncellendi. Lütfen yeni şifrenizle giriş yapınız.";

                    // Sign out to force re-login with new password
                    await _signInManager.SignOutAsync();

                    return RedirectToAction("Login", "Account");

                }

                foreach (var error in result.Errors)
                {
                    ModelState.AddModelError("", error.Description);
                }
            }
        }
        return View(model);
    }

    public ActionResult AccessDenied()
    {
        return View();
    }
    public ActionResult ForgotPassword()
    {
        return View();
    }

    [HttpPost]
    public async Task<ActionResult> ForgotPassword(string email)
    {
        if (string.IsNullOrEmpty(email))
        {
            TempData["Mesaj"] = "Eposta adresinizi giriniz";
            return View();
        }

        // 1. Try to find in AspNetUsers by Email
        var user = await _userManager.FindByEmailAsync(email);

        if (user == null)
        {
            // 2. Try to find in AspNetUsers by UserName
            user = await _userManager.FindByNameAsync(email);
        }

        if (user == null)
        {
            // 3. Try to find in TBL_KULLANICI (Business Table)
            var dbUser = await _mskDb.TBL_KULLANICIs.AsNoTracking().FirstOrDefaultAsync(x => x.TXTEMAIL == email);
            if (dbUser != null && dbUser.LNGIDENTITYKOD.HasValue)
            {
                user = await _userManager.FindByIdAsync(dbUser.LNGIDENTITYKOD.Value.ToString());
            }
        }

        if (user == null)
        {
            TempData["Mesaj"] = "Girdiğiniz mail adresi sistemde kayıtlı değil. Lütfen geçerli bir mail adresi giriniz."; 
            return View(); // Stay on the same page to allow retry
        }

        // Generate Random Password
        string randomPassword = GenerateRandomPassword(8);
        
        // Reset Password to Random Password
        // Bypass token logic for forced administrative reset
        // string randomPassword = GenerateRandomPassword(8); // ALREADY DEFINED ABOVE

        // Remove password if exists
        if (await _userManager.HasPasswordAsync(user))
        {
             await _userManager.RemovePasswordAsync(user);
        }
        
        // Add new random password
        var result = await _userManager.AddPasswordAsync(user, randomPassword);

        if (result.Succeeded)
        {
            // Remove ForcePasswordChange claim if exists
            // Remove ForcePasswordChange claim if exists (Cleanup old state)
            var claims = await _userManager.GetClaimsAsync(user);
            var existingClaim = claims.FirstOrDefault(c => c.Type == "ForcePasswordChange");
            if (existingClaim != null)
            {
                await _userManager.RemoveClaimAsync(user, existingClaim);
            }

            // Add new ForcePasswordChange claim
            await _userManager.AddClaimAsync(user, new Claim("ForcePasswordChange", "true"));

            // Critical: Invalidate old sessions/cookies
            await _userManager.UpdateSecurityStampAsync(user);

            try 
            {
                // Send Email
                // Use the input 'email' because we verified it belongs to this user (either via Identity or TBL_KULLANICI)
                // and user.Email might be different or outdated in Identity.
                MailBody mb = new MailBody();
                var body = mb.TemporaryPasswordEmail(user.UserName!, randomPassword);
                
                // Use input 'email' explicitly
                await _emailService.SendEmailAsync(email, "Geçici Şifreniz", body);
                
                TempData["Mesaj"] = $"Geçici şifreniz '{email}' adresine gönderildi. (Lütfen Spam/Gereksiz kutusunu da kontrol ediniz)";
                return RedirectToAction("Login");
            }
            catch (Exception ex)
            {
                 _logger.LogError(ex, "Şifre sıfırlama maili gönderilemedi");
                 TempData["Mesaj"] = "Şifre oluşturuldu ancak mail gönderilemedi. Lütfen daha sonra tekrar deneyiniz.";
                 return RedirectToAction("Login");
            }
        }
        else 
        {
             string errors = string.Join("; ", result.Errors.Select(e => e.Description));
             TempData["Mesaj"] = "Şifre sıfırlama hatası: " + errors;
             return View();
        }
    }

    private string GenerateRandomPassword(int length)
    {
        const string valid = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890!@#$%^&*";
        System.Text.StringBuilder res = new System.Text.StringBuilder();
        
        // Use cryptographically secure random number generator
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        byte[] randomBytes = new byte[length];
        rng.GetBytes(randomBytes);
        
        for (int i = 0; i < length; i++)
        {
            res.Append(valid[randomBytes[i] % valid.Length]);
        }
        
        // Ensure complexity requirements
        res.Append("A1!"); 
        return res.ToString();
    }

    public async Task<ActionResult> ResetPassword(string userId, string token)
    {
        if (userId == null || token == null)
        {
            return RedirectToAction("Login");
        }

        var user = await _userManager.FindByIdAsync(userId);

        if (user == null)
        {
            return RedirectToAction("Login");
        }

        var model = new AccountResetPasswordModel
        {
            Token = token,
            Email = user.Email!
        };

        return View(model);
    }

    [HttpPost]
    public async Task<ActionResult> ResetPassword(AccountResetPasswordModel model)
    {
        if (ModelState.IsValid)
        {
            var user = await _userManager.FindByEmailAsync(model.Email);

            if (user == null)
            {
                return RedirectToAction("Login");
            }

            var result = await _userManager.ResetPasswordAsync(user, model.Token, model.Password);

            if (result.Succeeded)
            {
                TempData["Mesaj"] = "Şifreniz güncellendi";
                return RedirectToAction("Login");
            }

            foreach (var error in result.Errors)
            {
                ModelState.AddModelError("", error.Description);
            }
        }
        return View(model);
    }

    private async Task<ActionResult> RedirectToAuthorizedPage(AppUser user, int? companyId = null)
    {
        // Giriş sonrası Cockpit dashboard'a yönlendir
        return RedirectToAction("Index", "Cockpit");
    }

    private async Task<bool> IsUserAuthorizedForUrl(AppUser user, string url)
    {
        // SOS'ta rol bazlı URL gating yok — tüm giriş yapmış kullanıcılar Cockpit/FirsatAnaliz'e erişebilir
        await Task.CompletedTask;
        return true;
    }

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> GetAdminProjects()
    {
        var userTypeClaim = User.FindFirst("UserType")?.Value;
        
        if (userTypeClaim == "1") // Admin
        {
            var projects = await _mskDb.VIEW_ORTAK_PROJE_ISIMLERIs
                                    .OrderBy(x => x.TXTORTAKPROJEADI)
                                    .Select(x => new { id = x.LNGKOD, name = x.TXTORTAKPROJEADI })
                                    .ToListAsync();
            
            var mapped = projects.Select(x => new { id = x.id, name = x.name, encryptedId = _urlEncryption.EncryptId(x.id) }).ToList();
            
            // Add "All Companies" option at the top
            var allOption = new { id = -1, name = "Tüm Firmalar", encryptedId = _urlEncryption.EncryptId(-1) };
            var projectList = new List<object> { allOption };
            projectList.AddRange(mapped);
                                    
            return Json(new { success = true, projects = projectList });
        }
        else if (userTypeClaim == "3") // Univera
        {
             var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
             if (string.IsNullOrEmpty(userId)) return Json(new { success = false, message = "Kullanıcı bilgisi eksik" });

             // Parse userId carefully
             if (!int.TryParse(userId, out int uid)) return Json(new { success = false, message = "Kullanıcı ID hatası" });

             var user = await _mskDb.TBL_KULLANICIs.FirstOrDefaultAsync(u => u.LNGIDENTITYKOD == uid);
             if (user == null) return Json(new { success = false, message = "Kullanıcı bulunamadı" });

             var authorizedIndices = await _mskDb.TBL_KULLANICI_FIRMAs
                                     .Where(f => f.LNGKULLANICIKOD == user.LNGKOD)
                                     .Select(f => f.LNGFIRMAKOD)
                                     .ToListAsync();

             var projects = await _mskDb.VIEW_ORTAK_PROJE_ISIMLERIs
                                     .Where(f => authorizedIndices.Contains(f.LNGKOD))
                                     .OrderBy(x => x.TXTORTAKPROJEADI)
                                     .Select(x => new { id = x.LNGKOD, name = x.TXTORTAKPROJEADI })
                                     .ToListAsync();
             
             var mapped = projects.Select(x => new { id = x.id, name = x.name, encryptedId = _urlEncryption.EncryptId(x.id) }).ToList();

             // Add "All Companies" Option (Standardizing with Type 1)
             // We need an object that matches the anonymous type structure
             var allOption = new { id = -1, name = "Tüm Firmalar", encryptedId = _urlEncryption.EncryptId(-1) };
             var projectList = new List<object> { allOption };
             projectList.AddRange(mapped);
             
             return Json(new { success = true, projects = projectList });
        }
        else if (userTypeClaim == "4") // Univera Customer
        {
             var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
             if (string.IsNullOrEmpty(userId)) return Json(new { success = false, message = "Kullanıcı bilgisi eksik" });

             if (!int.TryParse(userId, out int uid)) return Json(new { success = false, message = "Kullanıcı ID hatası" });

             var user = await _mskDb.TBL_KULLANICIs.FirstOrDefaultAsync(u => u.LNGIDENTITYKOD == uid);
             if (user == null) return Json(new { success = false, message = "Kullanıcı bulunamadı" });

             var authorizedIndices = await _mskDb.TBL_KULLANICI_FIRMAs
                                     .Where(f => f.LNGKULLANICIKOD == user.LNGKOD)
                                     .Select(f => f.LNGFIRMAKOD)
                                     .ToListAsync();

             var projects = await _mskDb.VIEW_ORTAK_PROJE_ISIMLERIs
                                     .Where(f => authorizedIndices.Contains(f.LNGKOD))
                                     .OrderBy(x => x.TXTORTAKPROJEADI)
                                     .Select(x => new { id = x.LNGKOD, name = x.TXTORTAKPROJEADI })
                                     .ToListAsync();
             
             var mapped = projects.Select(x => new { id = x.id, name = x.name, encryptedId = _urlEncryption.EncryptId(x.id) }).ToList();

             // Add "All Companies" Option
             var allOption = new { id = -1, name = "Tüm Firmalar", encryptedId = _urlEncryption.EncryptId(-1) };
             var projectList = new List<object> { allOption };
             projectList.AddRange(mapped);
             
             return Json(new { success = true, projects = projectList });
        }

        return Json(new { success = false, message = "Yetkisiz erişim" });
    }

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> ChangeCompany(string companyId, string returnUrl)
    {
        try
        {
            // Decrypt Company ID
            int? decryptedCompanyId = _urlEncryption.DecryptId(companyId);
            
            // If passed 0 or empty, it might mean "Clear Filter" / "All Companies"
            // _urlEncryption.DecryptId returns null for invalid input.
            // If input was explicitly meant to be "All Companies", let's assume we handle that.
            // But usually we pass an ID. If we want "All", we might pass an encrypted -1 or 0?
            // Sidebar passes -1 for "All". Let's assume -1 or 0 is "All".
            
            // If decryption failed, and input string was not empty, it's a security violation.
            if (decryptedCompanyId == null && !string.IsNullOrEmpty(companyId))
            {
                 // Check if it's "0" or "-1" in string (legacy?) No, we expect encrypted.
                 // Treat as unauthorized
                 return Json(new { success = false, message = "Geçersiz Firma ID" });
            }
            
            int targetCompanyId = decryptedCompanyId ?? -1;

            // 1. Authorization Check
            var userTypeClaim = User.FindFirst("UserType")?.Value;
            if (userTypeClaim != "1" && userTypeClaim != "3" && userTypeClaim != "4") return Json(new { success = false, message = "Yetkisiz işlem" });

            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Json(new { success = false, message = "Kullanıcı bulunamadı" });

            // 2. Fetch User Base Info
            var dbUser = await _mskDb.TBL_KULLANICIs
                        .Where(x => x.LNGIDENTITYKOD == user.Id)
                        .Select(x => new { x.LNGKOD, x.TXTFIRMAADI, x.LNGKULLANICITIPI })
                        .FirstOrDefaultAsync();

            if (dbUser == null) return Json(new { success = false, message = "Kullanıcı detayları bulunamadı" });
            
            // 2.1 Additional Authorization Check for Type 3 and 4
             if ((userTypeClaim == "3" || userTypeClaim == "4") && targetCompanyId > 0)
             {
                 var isAuthorized = await _mskDb.TBL_KULLANICI_FIRMAs.AnyAsync(f => f.LNGKULLANICIKOD == dbUser.LNGKOD && f.LNGFIRMAKOD == targetCompanyId);
                 if (!isAuthorized) return Json(new { success = false, message = "Bu firmaya geçiş yetkiniz yok." });
             }

            if (dbUser == null) return Json(new { success = false, message = "Kullanıcı detayları bulunamadı" });

            // 3. Re-Construct Claims
            var claims = new List<Claim>();
            
            // Base Claims
            if (!string.IsNullOrEmpty(dbUser.TXTFIRMAADI))
            {
                claims.Add(new Claim("FirmaAdi", dbUser.TXTFIRMAADI));
            }
            claims.Add(new Claim("UserType", dbUser.LNGKULLANICITIPI?.ToString() ?? "0"));

            // New Project Selection Claims (Only if companyId > 0)
            if (targetCompanyId > 0)
            {
                claims.Add(new Claim("ProjectCode", targetCompanyId.ToString()));
                
                var projectName = await _mskDb.VIEW_ORTAK_PROJE_ISIMLERIs
                                    .Where(p => p.LNGKOD == targetCompanyId)
                                    .Select(p => p.TXTORTAKPROJEADI)
                                    .FirstOrDefaultAsync();
                    
                if (!string.IsNullOrEmpty(projectName))
                {
                    claims.Add(new Claim("ProjectName", projectName));
                }
            }

            // 4. Refresh Sign In
            await _signInManager.SignOutAsync();
            // Force isPersistent to false
            await _signInManager.SignInWithClaimsAsync(user, false, claims);

            // [FIX] Set Cookie for robust persistence
            // Skip cookie for Admin (1) only - they should use URL params
            var userType = dbUser?.LNGKULLANICITIPI ?? 0;
            if (userType != 1)
            {
                if (targetCompanyId <= 0)
                {
                    _companyResolution.ClearCompanyCookie(HttpContext);
                }
                else
                {
                    _companyResolution.SetCompanyCookie(HttpContext, targetCompanyId);
                }
            }
            else
            {
                // Admin: Always delete cookie
                _companyResolution.ClearCompanyCookie(HttpContext);
            }
            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Şirket değiştirme sırasında hata oluştu");
            return Json(new { success = false, message = "İşlem sırasında bir hata oluştu." });
        }
    }

    // ───────────────────────────────────────────────────────────────
    // GET /Account/SatisciGirisUyarisi
    // Login sonrası popup için: kullanıcı email'i TBL_VARUNA_PERSON ile eşleşip
    // TBLSOS_HEDEF_TEMSILCI'de aktif kaydı varsa ve "bu ay başından önce kapanması
    // gereken" açık fırsatları varsa, sayıları döndürür.
    // ───────────────────────────────────────────────────────────────
    [Authorize]
    [HttpGet]
    public async Task<IActionResult> SatisciGirisUyarisi()
    {
        var email = User.Identity?.Name;
        if (string.IsNullOrEmpty(email)) return Json(new { eligible = false });

        var person = await _mskDb.TBL_VARUNA_PERSONs.AsNoTracking()
            .Where(p => p.Email == email && p.DeletedOn == null)
            .Select(p => new { p.Id, p.PersonNameSurname, p.Name, p.SurName })
            .FirstOrDefaultAsync();
        if (person == null) return Json(new { eligible = false });

        var hedefVar = await _mskDb.TBLSOS_HEDEF_TEMSILCIs.AsNoTracking()
            .AnyAsync(h => h.CrmPersonId == person.Id && h.Aktif);
        if (!hedefVar) return Json(new { eligible = false });

        var now = DateTime.Today;
        // Popup eşiği: BUGÜN. Tahmini kapanış tarihi bugünden önce olan açık fırsatlar
        // satışçıya hatırlatılır (Rapor sayfasındaki "geçti" rozeti ay başı eşiklidir;
        // popup daha agresif: kullanıcı her gün uyarılsın).
        var personIdLower = person.Id.ToLower();

        var staleFirsatlar = await _mskDb.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking()
            .Where(o => o.DeletedOn == null
                && o.OwnerId != null && o.OwnerId.ToLower() == personIdLower
                && o.OpportunityStageName != "Lost" && o.OpportunityStageName != "Won"
                && o.CloseDate.HasValue && o.CloseDate.Value < now)
            .Select(o => new { o.Id, o.OpportunityStageName, o.AmountAmount })
            .ToListAsync();

        var acik = staleFirsatlar
            .Where(o => o.OpportunityStageName == null
                || !o.OpportunityStageName.Contains("Closed", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var oppIds = acik.Select(o => o.Id.ToLower()).ToList();
        var teklifOppIds = await _mskDb.TBL_VARUNA_TEKLIFs.AsNoTracking()
            .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue
                && oppIds.Contains(t.OpportunityId!.Value.ToString().ToLower()))
            .Select(t => t.OpportunityId!.Value.ToString().ToLower())
            .Distinct()
            .ToListAsync();
        var teklifSet = teklifOppIds.ToHashSet();

        var staleAdet = acik.Count;
        var teklifVarAdet = acik.Count(o => teklifSet.Contains(o.Id.ToLower()));
        var staleTutar = acik.Sum(o => o.AmountAmount ?? 0m);
        var personName = !string.IsNullOrWhiteSpace(person.PersonNameSurname)
            ? person.PersonNameSurname!
            : ($"{person.Name} {person.SurName}").Trim();

        return Json(new
        {
            eligible = staleAdet > 0,
            personName,
            staleAdet,
            staleTutar,
            teklifVarAdet,
            bugun        = now.ToString("yyyy-MM-dd"),
            // Detaylar linki için: yarın itibarıyla CloseDate < <yarın> → bugün dahil tüm geçmişler
            raporOnceTarihi = now.AddDays(1).ToString("yyyy-MM-dd")
        });
    }
}

