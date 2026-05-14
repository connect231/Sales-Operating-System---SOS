using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SOS.Services;

namespace SOS.Controllers;

[Authorize]
[SosAdmin]  // Sadece TBLSOS_ADMIN_KULLANICI listesindeki kullanıcılar
public class YonetimController : Controller
{
    private readonly IAdminAuthService _adminAuth;
    private readonly ILogger<YonetimController> _logger;

    public YonetimController(IAdminAuthService adminAuth, ILogger<YonetimController> logger)
    {
        _adminAuth = adminAuth;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var list = await _adminAuth.GetAllAsync();
        return View(list);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Ekle(string email, string? adSoyad)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            TempData["Hata"] = "E-posta adresi zorunlu.";
            return RedirectToAction(nameof(Index));
        }
        var ekleyen = User.Identity?.Name;
        var added = await _adminAuth.AddAsync(email, adSoyad, ekleyen);
        if (added != null)
            TempData["Mesaj"] = $"{added.Email} admin yetkisine eklendi.";
        else
            TempData["Hata"] = "Eklenemedi.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Sil(int id)
    {
        var ok = await _adminAuth.RemoveAsync(id);
        TempData[ok ? "Mesaj" : "Hata"] = ok ? "Admin silindi." : "Silme başarısız.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AktiflikDegistir(int id, bool aktif)
    {
        var ok = await _adminAuth.SetAktifAsync(id, aktif);
        TempData[ok ? "Mesaj" : "Hata"] = ok
            ? (aktif ? "Admin aktifleştirildi." : "Admin pasifleştirildi.")
            : "Güncelleme başarısız.";
        return RedirectToAction(nameof(Index));
    }
}
