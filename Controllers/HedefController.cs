using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SOS.Models.ViewModels;
using SOS.Services;

namespace SOS.Controllers;

[Authorize]
public class HedefController : Controller
{
    private readonly IHedefService _hedef;
    private readonly ILogger<HedefController> _logger;

    public HedefController(IHedefService hedef, ILogger<HedefController> logger)
    {
        _hedef = hedef;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  KARNE SAYFASI (View) — /Hedef/Karne
    // ─────────────────────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult Karne()
    {
        return View();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  AJAX ENDPOINTS — Fırsat Analizi BANT + Karne tab'ları
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fırsat Analizi tepesindeki tek satır şerit verisi.
    /// donem: ay|lastmonth|q1|q2|q3|q4|ytd|all
    /// temsilciId: 1..9 veya null
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Bant(string? donem = null, int? temsilciId = null)
    {
        try
        {
            var (start, end, etiket) = ResolveDonem(donem);
            var yil = DateTime.Now.Year;
            var bant = await _hedef.GetBantAsync(yil, start, end, temsilciId, etiket);
            return Json(new { ok = true, data = bant });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bant endpoint hatası");
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> SirketOzeti(int? yil = null)
    {
        try
        {
            var y = yil ?? DateTime.Now.Year;
            var data = await _hedef.GetSirketOzetiAsync(y);
            return Json(new { ok = true, data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SirketOzeti endpoint hatası");
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> TemsilciListesi(int? yil = null)
    {
        try
        {
            var y = yil ?? DateTime.Now.Year;
            var data = await _hedef.GetTemsilciListesiAsync(y);
            return Json(new { ok = true, data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TemsilciListesi endpoint hatası");
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> UrunListesi(int? yil = null)
    {
        try
        {
            var y = yil ?? DateTime.Now.Year;
            var data = await _hedef.GetUrunListesiAsync(y);
            return Json(new { ok = true, data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UrunListesi endpoint hatası");
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> TemsilciUrunMatris(int? yil = null)
    {
        try
        {
            var y = yil ?? DateTime.Now.Year;
            var data = await _hedef.GetTemsilciUrunMatrisAsync(y);
            return Json(new { ok = true, data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TemsilciUrunMatris endpoint hatası");
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> TemsilciDetay(int temsilciId, int? yil = null)
    {
        try
        {
            var y = yil ?? DateTime.Now.Year;
            var data = await _hedef.GetTemsilciDetayAsync(y, temsilciId);
            if (data == null) return NotFound(new { ok = false, error = "Temsilci bulunamadı" });
            return Json(new { ok = true, data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TemsilciDetay endpoint hatası");
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> Temsilciler()
    {
        var list = await _hedef.GetTemsilcilerAsync();
        return Json(list.Select(t => new { id = t.Id, ad = t.Ad, kanal = t.Kanal }));
    }

    [HttpGet]
    public async Task<IActionResult> UrunDetay(int? urunId = null, string? urunAd = null, int? yil = null)
    {
        try
        {
            var y = yil ?? DateTime.Now.Year;
            HedefUrunDetayViewModel? data = null;
            if (urunId.HasValue) data = await _hedef.GetUrunDetayAsync(y, urunId.Value);
            else if (!string.IsNullOrEmpty(urunAd)) data = await _hedef.GetUrunDetayByAdAsync(y, urunAd);
            if (data == null) return NotFound(new { ok = false, error = "Ürün bulunamadı" });
            return Json(new { ok = true, data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UrunDetay endpoint hatası");
            return Json(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Fırsat Analizi'ndeki Satış Temsilcisi/Ürün listelerinden ⓘ ikonu ile çağrılır.
    /// Temsilci adı ile arama (CRM'den gelen ad).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> TemsilciByAd(string ad, int? yil = null)
    {
        try
        {
            var y = yil ?? DateTime.Now.Year;
            var list = await _hedef.GetTemsilcilerAsync();
            // Fuzzy match: CRM'deki ad ile hedef tablosundaki ad arasında uyumsuzluk olabilir
            var t = list.FirstOrDefault(x =>
                string.Equals(x.Ad, ad, StringComparison.OrdinalIgnoreCase) ||
                (ad != null && (x.Ad.Contains(ad, StringComparison.OrdinalIgnoreCase) ||
                                ad.Contains(x.Ad, StringComparison.OrdinalIgnoreCase))));
            if (t == null) return NotFound(new { ok = false, error = "Temsilci hedef tablosunda yok: " + ad });
            var data = await _hedef.GetTemsilciDetayAsync(y, t.Id);
            return Json(new { ok = true, data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TemsilciByAd endpoint hatası");
            return Json(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Fırsat Analizi → "Hedef Analizi Detay" panelinin tablo verisi.
    /// tab: yenileme | urun | satisci
    /// donem: month|lastmonth|q1|q2|q3|q4|ytd|all  (üst pill-nav'dan gelir)
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> AnalizDetay(string tab, string? donem = null)
    {
        try
        {
            var (start, end, etiket) = ResolveDonem(donem);
            var yil = DateTime.Now.Year;
            var rows = await _hedef.GetAnalizDetayAsync(tab ?? "yenileme", yil, start, end);
            return Json(new { ok = true, donem = etiket, rows });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AnalizDetay endpoint hatası");
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [HttpPost]
    public IActionResult InvalidateCache()
    {
        _hedef.InvalidateAll();
        return Json(new { ok = true });
    }

    // ─────────────────────────── Helper ───────────────────────────

    private static (DateTime start, DateTime end, string etiket) ResolveDonem(string? donem)
    {
        var now = DateTime.Now.Date;
        var yil = now.Year;
        var aylar = new[] { "", "Ocak", "Şubat", "Mart", "Nisan", "Mayıs", "Haziran", "Temmuz", "Ağustos", "Eylül", "Ekim", "Kasım", "Aralık" };

        switch ((donem ?? "month").ToLowerInvariant())
        {
            case "lastmonth":
                {
                    var d = now.AddMonths(-1);
                    var s = new DateTime(d.Year, d.Month, 1);
                    var e = s.AddMonths(1).AddDays(-1);
                    return (s, e, $"{aylar[d.Month]} {d.Year}");
                }
            case "q1": return (new DateTime(yil, 1, 1), new DateTime(yil, 3, 31), $"Q1 {yil}");
            case "q2": return (new DateTime(yil, 4, 1), new DateTime(yil, 6, 30), $"Q2 {yil}");
            case "q3": return (new DateTime(yil, 7, 1), new DateTime(yil, 9, 30), $"Q3 {yil}");
            case "q4": return (new DateTime(yil, 10, 1), new DateTime(yil, 12, 31), $"Q4 {yil}");
            case "ytd":
                {
                    // YTD = yıl başı → bulunduğumuz ayın SONU (tam ay).
                    // Gün-bazlı prorate yerine ay-bazlı toplam — Hedef ile gerçekleşen tutarlılığı için.
                    var ytdEnd = new DateTime(yil, now.Month, DateTime.DaysInMonth(yil, now.Month));
                    return (new DateTime(yil, 1, 1), ytdEnd, $"YTD {yil}");
                }
            case "all": return (new DateTime(yil, 1, 1), new DateTime(yil, 12, 31), $"{yil} Tüm Yıl");
            case "month":
            default:
                {
                    var s = new DateTime(yil, now.Month, 1);
                    var e = s.AddMonths(1).AddDays(-1);
                    return (s, e, $"{aylar[now.Month]} {yil}");
                }
        }
    }
}
