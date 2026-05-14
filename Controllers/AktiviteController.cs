using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SOS.DbData;
using SOS.Services;

namespace SOS.Controllers;

[Authorize]
[SosAdmin]
public class AktiviteController : Controller
{
    private readonly IDbContextFactory<MskDbContext> _contextFactory;
    private readonly ILoginAktiviteService _loginAktivite;

    public AktiviteController(IDbContextFactory<MskDbContext> contextFactory, ILoginAktiviteService loginAktivite)
    {
        _contextFactory = contextFactory;
        _loginAktivite = loginAktivite;
    }

    private bool IsAdmin() => User.FindFirst("UserType")?.Value == "1";

    [HttpGet]
    public IActionResult Index()
    {
        if (!IsAdmin()) return Forbid();
        return View();
    }

    /// <summary>
    /// Kullanıcı bazlı özet: kim, kaç giriş, toplam süre (saat), son giriş.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Ozet()
    {
        if (!IsAdmin()) return Forbid();

        // Önce 15 dakikadan eski açık oturumları kapat (rapor doğru çıksın)
        await _loginAktivite.SweepStaleSessionsAsync(15);

        await using var db = await _contextFactory.CreateDbContextAsync();

        var rows = await db.TBLSOS_LOGIN_AKTIVITEs
            .GroupBy(x => new { x.KullaniciId, x.Email, x.AdSoyad })
            .Select(g => new
            {
                kullaniciId = g.Key.KullaniciId,
                email = g.Key.Email,
                adSoyad = g.Key.AdSoyad,
                girisAdedi = g.Count(),
                toplamSaniye = g.Sum(x => (long)x.SureSaniye),
                sonGiris = g.Max(x => x.GirisZamani),
                aktifMi = g.Any(x => x.AktifMi)
            })
            .OrderByDescending(x => x.sonGiris)
            .ToListAsync();

        var data = rows.Select(r => new
        {
            r.kullaniciId,
            r.email,
            r.adSoyad,
            r.girisAdedi,
            toplamSure = FormatDuration(r.toplamSaniye),
            toplamSaniye = r.toplamSaniye,
            sonGiris = r.sonGiris.ToString("dd.MM.yyyy HH:mm"),
            sonGirisIso = r.sonGiris,
            r.aktifMi
        });

        return Json(new { ok = true, data });
    }

    /// <summary>
    /// Tüm kullanıcıların kronolojik giriş kaydı (son 200, en yeni üstte).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> SonGirisler()
    {
        if (!IsAdmin()) return Forbid();

        await using var db = await _contextFactory.CreateDbContextAsync();

        var rows = await db.TBLSOS_LOGIN_AKTIVITEs
            .OrderByDescending(x => x.GirisZamani)
            .Take(200)
            .Select(x => new
            {
                id = x.Id,
                email = x.Email,
                adSoyad = x.AdSoyad,
                giris = x.GirisZamani,
                cikis = x.CikisZamani,
                sureSaniye = x.SureSaniye,
                aktif = x.AktifMi,
                ip = x.IPAdresi
            })
            .ToListAsync();

        var data = rows.Select(r => new
        {
            r.id,
            r.email,
            r.adSoyad,
            tarih = r.giris.ToString("dd.MM.yyyy"),
            saat = r.giris.ToString("HH:mm:ss"),
            cikis = r.cikis?.ToString("HH:mm:ss"),
            sure = FormatDuration(r.sureSaniye),
            r.aktif,
            r.ip
        });

        return Json(new { ok = true, data });
    }

    /// <summary>
    /// Tek kullanıcı için login geçmişi (son 100 oturum).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Detay(int kullaniciId)
    {
        if (!IsAdmin()) return Forbid();

        await using var db = await _contextFactory.CreateDbContextAsync();

        var rows = await db.TBLSOS_LOGIN_AKTIVITEs
            .Where(x => x.KullaniciId == kullaniciId)
            .OrderByDescending(x => x.GirisZamani)
            .Take(100)
            .Select(x => new
            {
                id = x.Id,
                giris = x.GirisZamani,
                cikis = x.CikisZamani,
                sonAktivite = x.SonAktiviteZamani,
                sureSaniye = x.SureSaniye,
                aktif = x.AktifMi,
                ip = x.IPAdresi,
                userAgent = x.UserAgent
            })
            .ToListAsync();

        var data = rows.Select(r => new
        {
            r.id,
            giris = r.giris.ToString("dd.MM.yyyy HH:mm:ss"),
            cikis = r.cikis?.ToString("dd.MM.yyyy HH:mm:ss"),
            sonAktivite = r.sonAktivite.ToString("dd.MM.yyyy HH:mm:ss"),
            sure = FormatDuration(r.sureSaniye),
            r.aktif,
            r.ip,
            r.userAgent
        });

        return Json(new { ok = true, data });
    }

    private static string FormatDuration(long saniye)
    {
        if (saniye <= 0) return "0 dk";
        var ts = TimeSpan.FromSeconds(saniye);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours} sa {ts.Minutes} dk";
        if (ts.TotalMinutes >= 1)
            return $"{(int)ts.TotalMinutes} dk";
        return $"{(int)ts.TotalSeconds} sn";
    }
}
