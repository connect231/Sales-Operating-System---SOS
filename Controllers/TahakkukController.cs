using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SOS.DbData;
using SOS.Models.MsK;
using SOS.Services;

namespace SOS.Controllers;

/// <summary>
/// Fatura tahakkuk yönetimi — SAP referans no bazlı.
/// Matbu fatura no (SerialNumber) opsiyonel — sonradan atanabilir.
/// </summary>
[Authorize]
[SosAdmin]
public class TahakkukController : Controller
{
    private readonly IDbContextFactory<MskDbContext> _contextFactory;
    private readonly ITahakkukService _tahakkukService;

    public TahakkukController(IDbContextFactory<MskDbContext> contextFactory, ITahakkukService tahakkukService)
    {
        _contextFactory = contextFactory;
        _tahakkukService = tahakkukService;
    }

    [HttpGet]
    public IActionResult Index() => View();

    /// <summary>
    /// SAP no veya fatura no ile arama. UI'dan çağrılır.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Lookup(string? sapNo, string? faturaNo)
    {
        var aranan = (sapNo ?? faturaNo ?? "").Trim();
        if (string.IsNullOrWhiteSpace(aranan))
            return Json(new { success = false, message = "Arama terimi boş olamaz" });

        using var db = _contextFactory.CreateDbContext();

        // 1) Varuna'da SAP referans no ile ara
        var varunaSip = await db.TBL_VARUNA_SIPARIs.AsNoTracking()
            .Where(s => s.SAPOutReferenceCode != null && s.SAPOutReferenceCode.Trim() == aranan && s.DeletedOn == null)
            .Select(s => new { s.SerialNumber, s.SAPOutReferenceCode, s.InvoiceDate, s.AccountTitle, s.TotalNetAmount, s.OrderStatus })
            .FirstOrDefaultAsync();

        // 2) Bulunamadıysa SerialNumber (fatura no) ile dene
        if (varunaSip == null)
        {
            varunaSip = await db.TBL_VARUNA_SIPARIs.AsNoTracking()
                .Where(s => s.SerialNumber == aranan && s.DeletedOn == null)
                .Select(s => new { s.SerialNumber, s.SAPOutReferenceCode, s.InvoiceDate, s.AccountTitle, s.TotalNetAmount, s.OrderStatus })
                .FirstOrDefaultAsync();
        }

        if (varunaSip == null)
            return Json(new { success = false, message = $"Varuna'da bulunamadı: {aranan}" });

        var sapRef = varunaSip.SAPOutReferenceCode?.Trim() ?? "";
        var sn = varunaSip.SerialNumber;

        // Excel fatura bilgisi (varsa)
        decimal? excelTutar = null;
        string? excelMusteri = null;
        if (sn != null)
        {
            var excel = await db.VIEW_CP_EXCEL_FATURAs.AsNoTracking()
                .Where(f => f.Fatura_No == sn)
                .Select(f => new { f.Fatura_Toplam, f.Ilgili_Kisi })
                .FirstOrDefaultAsync();
            if (excel != null) { excelTutar = excel.Fatura_Toplam; excelMusteri = excel.Ilgili_Kisi; }
        }

        // Mevcut tahakkuk kaydı
        var tahakkuk = await db.TBLSOS_FATURA_TAHAKKUKs.AsNoTracking()
            .Where(t => t.Aktif && (t.SapReferansNo == sapRef || (sn != null && t.FaturaNo == sn)))
            .FirstOrDefaultAsync();

        var tutar = excelTutar ?? varunaSip.TotalNetAmount ?? 0;

        return Json(new
        {
            success = true,
            sapReferansNo = sapRef,
            faturaNo = sn,
            orijinalTarih = varunaSip.InvoiceDate?.ToString("dd.MM.yyyy") ?? "—",
            tutar = tutar.ToString("N0", new System.Globalization.CultureInfo("tr-TR")) + " TL",
            firma = varunaSip.AccountTitle ?? excelMusteri ?? "—",
            mevcutTahakkuk = tahakkuk?.TahakkukTarihi.ToString("yyyy-MM-dd")
        });
    }

    /// <summary>
    /// Tahakkuk kaydet/güncelle — SAP referans no zorunlu, fatura no opsiyonel.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Save(string sapReferansNo, string? faturaNo, string tahakkukTarihi, string? aciklama)
    {
        if (string.IsNullOrWhiteSpace(sapReferansNo))
            return Json(new { success = false, message = "SAP referans no zorunlu" });
        if (!DateTime.TryParse(tahakkukTarihi, out var th))
            return Json(new { success = false, message = "Geçerli bir tahakkuk tarihi girin" });

        sapReferansNo = sapReferansNo.Trim();
        faturaNo = faturaNo?.Trim();
        if (string.IsNullOrWhiteSpace(faturaNo)) faturaNo = null;

        using var db = _contextFactory.CreateDbContext();

        // Varuna'da doğrula
        var varunaSip = await db.TBL_VARUNA_SIPARIs.AsNoTracking()
            .Where(s => s.SAPOutReferenceCode != null && s.SAPOutReferenceCode.Trim() == sapReferansNo && s.DeletedOn == null)
            .Select(s => new { s.InvoiceDate, s.SerialNumber })
            .FirstOrDefaultAsync();

        // FaturaNo henüz null ise Varuna'dan güncelle
        if (faturaNo == null && varunaSip?.SerialNumber != null)
            faturaNo = varunaSip.SerialNumber;

        var kullanici = User.Identity?.Name ?? "anonim";
        var now = DateTime.Now;

        var mevcut = await db.TBLSOS_FATURA_TAHAKKUKs
            .Where(t => t.SapReferansNo == sapReferansNo && t.Aktif)
            .FirstOrDefaultAsync();

        if (mevcut == null)
        {
            db.TBLSOS_FATURA_TAHAKKUKs.Add(new TBLSOS_FATURA_TAHAKKUK
            {
                SapReferansNo = sapReferansNo,
                FaturaNo = faturaNo,
                TahakkukTarihi = th,
                OrijinalFaturaTarihi = varunaSip?.InvoiceDate,
                Aciklama = aciklama?.Trim(),
                OlusturulmaTarihi = now,
                OlusturanKullanici = kullanici,
                Aktif = true
            });
        }
        else
        {
            mevcut.TahakkukTarihi = th;
            mevcut.FaturaNo = faturaNo ?? mevcut.FaturaNo;
            mevcut.Aciklama = aciklama?.Trim();
        }

        await db.SaveChangesAsync();
        _tahakkukService.Invalidate();
        return Json(new { success = true, message = "Tahakkuk kaydedildi" });
    }

    /// <summary>
    /// Tahakkuk silme (soft delete) — SAP referans no ile.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Delete(string sapReferansNo)
    {
        if (string.IsNullOrWhiteSpace(sapReferansNo))
            return Json(new { success = false, message = "SAP referans no zorunlu" });

        using var db = _contextFactory.CreateDbContext();
        var mevcut = await db.TBLSOS_FATURA_TAHAKKUKs
            .Where(t => t.SapReferansNo == sapReferansNo.Trim() && t.Aktif)
            .FirstOrDefaultAsync();

        if (mevcut == null)
            return Json(new { success = false, message = "Aktif tahakkuk bulunamadı" });

        mevcut.Aktif = false;
        await db.SaveChangesAsync();
        _tahakkukService.Invalidate();
        return Json(new { success = true, message = "Tahakkuk silindi" });
    }

    public class BulkImportItem
    {
        public string Sid { get; set; } = "";
        public string Tarih { get; set; } = "";
        public string? SapNo { get; set; }
    }

    /// <summary>
    /// Bulk import — SAP bazlı eşleşme primary, SipID prefix fallback.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> BulkImport([FromBody] List<BulkImportItem> items)
    {
        if (items == null || items.Count == 0)
            return Json(new { ok = false, error = "Liste boş" });

        using var db = _contextFactory.CreateDbContext();

        var siparisler = await db.TBL_VARUNA_SIPARIs.AsNoTracking()
            .Where(s => s.OrderId != null && s.DeletedOn == null)
            .Select(s => new { s.OrderId, s.SerialNumber, s.InvoiceDate, s.SAPOutReferenceCode })
            .ToListAsync();

        var prefixMap = siparisler
            .Where(s => s.OrderId!.Length >= 8)
            .GroupBy(s => s.OrderId!.Substring(0, 8).ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.ToList());

        var sapMap = siparisler
            .Where(s => !string.IsNullOrEmpty(s.SAPOutReferenceCode))
            .GroupBy(s => s.SAPOutReferenceCode!.Trim())
            .ToDictionary(g => g.Key, g => g.ToList());

        // Mevcut tahakkuklar — SapReferansNo bazlı
        var mevcutTahakkuklar = (await db.TBLSOS_FATURA_TAHAKKUKs
            .Where(t => t.Aktif).ToListAsync())
            .GroupBy(t => t.SapReferansNo)
            .ToDictionary(g => g.Key, g => g.First());

        var kullanici = User.Identity?.Name ?? "bulk-import";
        var now = DateTime.Now;
        int eslesen = 0, yeniTahakkuk = 0, guncellenen = 0, atlanan = 0,
            eslesemeyen = 0, hatali = 0, sapFallback = 0;
        var eslesemeyenler = new List<string>();

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Sid) || string.IsNullOrWhiteSpace(item.Tarih)) { hatali++; continue; }
            if (!DateTime.TryParse(item.Tarih, out var excelTarih)) { hatali++; continue; }

            // SAP no varsa önce SAP ile bul
            var sapKey = !string.IsNullOrEmpty(item.SapNo) ? item.SapNo.Trim() : null;
            var prefix = item.Sid.Trim().ToLowerInvariant();
            var usedSap = false;

            // 1) SAP no ile ara (primary)
            var matches = sapKey != null && sapMap.TryGetValue(sapKey, out var sapMatches) ? sapMatches : null;
            if (matches != null) { usedSap = true; }
            else
            {
                // 2) OrderId prefix fallback
                prefixMap.TryGetValue(prefix, out matches);
            }

            if (matches == null || matches.Count == 0)
            {
                eslesemeyen++;
                if (eslesemeyenler.Count < 50) eslesemeyenler.Add(item.Sid + (sapKey != null ? $" (SAP:{sapKey})" : ""));
                continue;
            }
            if (usedSap) sapFallback++;

            var sip = matches.First();
            eslesen++;

            var sapRef = sip.SAPOutReferenceCode?.Trim();
            if (string.IsNullOrEmpty(sapRef)) { eslesemeyen++; continue; }

            var varunaTarih = sip.InvoiceDate;
            if (varunaTarih.HasValue && varunaTarih.Value.Date == excelTarih.Date) { atlanan++; continue; }

            if (mevcutTahakkuklar.TryGetValue(sapRef, out var existing))
            {
                if (existing.TahakkukTarihi.Date == excelTarih.Date) { atlanan++; continue; }
                existing.TahakkukTarihi = excelTarih;
                existing.FaturaNo = sip.SerialNumber ?? existing.FaturaNo;
                existing.Aciklama = $"Excel bulk import: {excelTarih:dd.MM.yyyy}";
                guncellenen++;
            }
            else
            {
                db.TBLSOS_FATURA_TAHAKKUKs.Add(new TBLSOS_FATURA_TAHAKKUK
                {
                    SapReferansNo = sapRef,
                    FaturaNo = sip.SerialNumber,
                    TahakkukTarihi = excelTarih,
                    OrijinalFaturaTarihi = varunaTarih,
                    Aciklama = $"Excel bulk import: {excelTarih:dd.MM.yyyy}",
                    OlusturulmaTarihi = now,
                    OlusturanKullanici = kullanici,
                    Aktif = true
                });
                yeniTahakkuk++;
            }
        }

        await db.SaveChangesAsync();
        _tahakkukService.Invalidate();

        return Json(new
        {
            ok = true,
            ozet = new { toplam = items.Count, eslesen, yeniTahakkuk, guncellenen, atlanan, eslesemeyen, hatali, sapFallback },
            eslesemeyenOrnek = eslesemeyenler
        });
    }

    /// <summary>
    /// Tüm aktif tahakkukları listeler.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        using var db = _contextFactory.CreateDbContext();

        var rows = await db.TBLSOS_FATURA_TAHAKKUKs.AsNoTracking()
            .Where(t => t.Aktif)
            .OrderByDescending(t => t.OlusturulmaTarihi)
            .Select(t => new { t.Id, t.SapReferansNo, t.FaturaNo, t.TahakkukTarihi, t.OrijinalFaturaTarihi, t.OlusturulmaTarihi })
            .ToListAsync();

        // Müşteri lookup: SAP no → Varuna AccountTitle
        var sapNos = rows.Select(r => r.SapReferansNo).Distinct().ToList();
        var musteriMap = (await db.TBL_VARUNA_SIPARIs.AsNoTracking()
            .Where(s => s.SAPOutReferenceCode != null && sapNos.Contains(s.SAPOutReferenceCode) && s.DeletedOn == null)
            .Select(s => new { s.SAPOutReferenceCode, s.AccountTitle, s.TotalNetAmount })
            .ToListAsync())
            .GroupBy(s => s.SAPOutReferenceCode!.Trim())
            .ToDictionary(g => g.Key, g => g.First());

        var result = rows.Select(r =>
        {
            musteriMap.TryGetValue(r.SapReferansNo, out var m);
            return new
            {
                sapReferansNo = r.SapReferansNo,
                faturaNo = r.FaturaNo,
                firma = m?.AccountTitle,
                tutar = m?.TotalNetAmount,
                tahakkukTarihi = r.TahakkukTarihi.ToString("dd.MM.yyyy"),
                orijinalTarih = r.OrijinalFaturaTarihi?.ToString("dd.MM.yyyy") ?? "—",
                kayitTarihi = r.OlusturulmaTarihi.ToString("dd.MM.yyyy")
            };
        }).ToList();

        return Json(result);
    }
}
