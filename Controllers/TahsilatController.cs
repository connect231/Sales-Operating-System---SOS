using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SOS.DbData;

namespace SOS.Controllers;

[Authorize]
public class TahsilatController : Controller
{
    private readonly IDbContextFactory<MskDbContext> _contextFactory;
    private readonly IMemoryCache _cache;

    public TahsilatController(IDbContextFactory<MskDbContext> contextFactory, IMemoryCache cache)
    {
        _contextFactory = contextFactory;
        _cache = cache;
    }

    [HttpGet]
    public IActionResult Index() => View();

    // Tek payload — bu ay 4 KPI + temsilci bazlı aynı 4 metrik + aylık trend + fatura detay
    [HttpGet]
    public async Task<IActionResult> Data()
    {
        const string CK = "tahsilat_data_v2";
        if (_cache.TryGetValue(CK, out object? cached) && cached != null)
            return Json(cached);

        using var db = _contextFactory.CreateDbContext();
        db.Database.SetCommandTimeout(60);

        var bugun  = DateTime.Now.Date;
        var aybas  = new DateTime(bugun.Year, bugun.Month, 1);
        var aysonu = aybas.AddMonths(1).AddDays(-1);
        var ay12   = aybas.AddMonths(-11);

        // ── 1) Tüm faturalar (iade/ret hariç, hukuki dahil — analiz için) ──
        var faturalar = await db.Database.SqlQueryRaw<FaturaRow>(@"
            SELECT
              Fatura_No        AS FaturaNo,
              ISNULL(Fatura_Toplam,0)   AS FaturaToplam,
              ISNULL(Tahsil_Edilen,0)   AS TahsilEdilen,
              ISNULL(Bekleyen_Bakiye,0) AS BekleyenBakiye,
              Fatura_Vade_Tarihi        AS VadeTarihi,
              Tahsil_Tarihi             AS TahsilTarihi,
              Odeme_Sozu_Tarihi         AS SozTarihi,
              ISNULL(LTRIM(RTRIM(Proje)),'') AS Proje,
              CASE WHEN ISNULL(LTRIM(RTRIM(Hukuki_Durum)),'') <> '' THEN 1 ELSE 0 END AS Hukuki
            FROM VeriOkumaDonusum.dbo.TBL_FINANS_FATURA
            WHERE LTRIM(RTRIM(ISNULL(Durum,''))) NOT IN (N'İADE',N'IADE',N'İPTAL',N'IPTAL',N'RET',N'İade Fatura',N'Iade Fatura')
        ").ToListAsync();

        // ── 2) Temsilci map ──
        var temsilciMap = await CockpitController.GetTemsilciMapAsync(_contextFactory, _cache);

        // ── 3) Aggregation ──
        decimal pTahsil = 0, pGecmis = 0, pSoz = 0, pToplam = 0;
        int aTahsil = 0, aGecmis = 0, aSoz = 0, aToplam = 0;

        var byRep = new Dictionary<string, RepAgg>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in faturalar)
        {
            var vade = f.VadeTarihi?.Date;
            var tahsilT = f.TahsilTarihi?.Date;
            var sozT = f.SozTarihi?.Date;

            bool tahsilBuAy   = tahsilT.HasValue && tahsilT >= aybas && tahsilT <= aysonu;
            bool gecmisAcik   = vade.HasValue && vade < aybas && f.BekleyenBakiye > 0;
            bool sozBuAy      = sozT.HasValue && sozT >= aybas && sozT <= aysonu && f.BekleyenBakiye > 0;
            // Toplam tahsilat hedefi: PAYDA = Tahsil edilen (bu ay) + Bekleyen bakiye (vade ≤ ay sonu)
            bool toplamMatch  = tahsilBuAy || (vade.HasValue && vade <= aysonu && f.BekleyenBakiye > 0);

            decimal addedTahsil  = tahsilBuAy ? f.TahsilEdilen : 0m;
            decimal addedGecmis  = gecmisAcik ? f.BekleyenBakiye : 0m;
            decimal addedSoz     = sozBuAy ? f.BekleyenBakiye : 0m;
            decimal addedToplam  = (tahsilBuAy ? f.TahsilEdilen : 0m)
                                 + ((vade.HasValue && vade <= aysonu && f.BekleyenBakiye > 0) ? f.BekleyenBakiye : 0m);

            if (tahsilBuAy) { pTahsil += addedTahsil; aTahsil++; }
            if (gecmisAcik) { pGecmis += addedGecmis; aGecmis++; }
            if (sozBuAy)    { pSoz    += addedSoz;    aSoz++; }
            if (toplamMatch) { pToplam += addedToplam; aToplam++; }

            // Temsilci bazlı
            string rep = "(Atanmamış)";
            if (!string.IsNullOrEmpty(f.FaturaNo) && temsilciMap.TryGetValue(f.FaturaNo, out var t))
                rep = t;
            if (!byRep.TryGetValue(rep, out var agg))
            {
                agg = new RepAgg { Ad = rep };
                byRep[rep] = agg;
            }
            agg.Tahsil  += addedTahsil;
            agg.Gecmis  += addedGecmis;
            agg.Soz     += addedSoz;
            agg.Toplam  += addedToplam;
            if (toplamMatch || gecmisAcik || sozBuAy || tahsilBuAy) agg.Adet++;
        }

        // ── 4) Aylık trend (son 12 ay) — Vade-bazlı matched + geç tahsilat ayrımı ──
        // Kullanıcı kuralı (2026-05-14): "Tahsil_Tarihi ∈ ay" toplamı yanıltıcı, çünkü
        // geçmiş vadeli faturaların bu ay tahsilatı hedefe eklenmiş gibi görünüyor.
        // Doğru kıyaslama: o ay VADELİ olan faturaların tahsilatı (matched).
        // Ek: bu ay tahsil edilen ama vadesi başka ay olan = geç tahsilat (bonus).
        // Her CTE Fatura_No bazında — bir fatura aynı ayda hem vade hem tahsil olabilir.
        var trend = await db.Database.SqlQueryRaw<TrendRow>(@"
            WITH base AS (
              SELECT Fatura_No,
                     Fatura_Toplam,
                     Tahsil_Edilen,
                     Fatura_Vade_Tarihi,
                     Tahsil_Tarihi,
                     CONVERT(varchar(7), Fatura_Vade_Tarihi, 120) AS VadeAy,
                     CONVERT(varchar(7), Tahsil_Tarihi, 120)      AS TahsilAy
                FROM VeriOkumaDonusum.dbo.TBL_FINANS_FATURA
               WHERE LTRIM(RTRIM(ISNULL(Durum,''))) NOT IN (N'İADE',N'IADE',N'İPTAL',N'IPTAL',N'RET',N'İade Fatura',N'Iade Fatura')
                 AND (Fatura_Vade_Tarihi >= {0} OR Tahsil_Tarihi >= {0})
            ),
            vade AS (
              -- O ay vadeli toplam (hedef)
              SELECT VadeAy AS Ay, ISNULL(SUM(Fatura_Toplam),0) AS Hedef
                FROM base WHERE VadeAy IS NOT NULL
               GROUP BY VadeAy
            ),
            matched AS (
              -- O ay vadeli + tahsil edilmiş (vadeli tahsilat)
              SELECT VadeAy AS Ay,
                     ISNULL(SUM(Tahsil_Edilen),0) AS Matched
                FROM base
               WHERE VadeAy IS NOT NULL AND ISNULL(Tahsil_Edilen,0) > 0
               GROUP BY VadeAy
            ),
            gec AS (
              -- O ay tahsil ama vadesi BAŞKA ay (geç tahsilat — geçmişten gelen)
              SELECT TahsilAy AS Ay,
                     ISNULL(SUM(Tahsil_Edilen),0) AS Gec,
                     COUNT(*) AS Adet
                FROM base
               WHERE TahsilAy IS NOT NULL AND ISNULL(Tahsil_Edilen,0) > 0
                 AND (VadeAy IS NULL OR VadeAy <> TahsilAy)
               GROUP BY TahsilAy
            )
            SELECT COALESCE(v.Ay, m.Ay, g.Ay) AS Ay,
                   ISNULL(m.Matched, 0)        AS Tutar,
                   ISNULL(g.Adet, 0)           AS Adet,
                   ISNULL(v.Hedef, 0)          AS Hedef,
                   ISNULL(g.Gec, 0)            AS GecTahsilat
              FROM vade v
              FULL OUTER JOIN matched m ON v.Ay = m.Ay
              FULL OUTER JOIN gec     g ON COALESCE(v.Ay, m.Ay) = g.Ay
              WHERE COALESCE(v.Ay, m.Ay, g.Ay) >= CONVERT(varchar(7), {0}, 120)
              ORDER BY Ay", ay12).ToListAsync();

        // Türetilen metrikler
        decimal pKalan = Math.Max(0m, pToplam - pTahsil - pSoz); // söze de bağlı değil, henüz tahsil de yok
        decimal pAySonuTahmin = pTahsil + pSoz; // söz tutulursa tahsil olacak + zaten tahsil olan
        decimal Pct(decimal x) => pToplam > 0 ? Math.Round(x / pToplam * 100m, 1) : 0m;

        var result = new
        {
            pulse = new
            {
                toplamTahsilat = pToplam, toplamAdet = aToplam,
                gecmisTahsilat = pGecmis, gecmisAdet = aGecmis, gecmisPct = Pct(pGecmis),
                sozAlinan      = pSoz,    sozAdet    = aSoz,    sozPct    = Pct(pSoz),
                tahsilEdilen   = pTahsil, tahsilAdet = aTahsil, tahsilPct = Pct(pTahsil),
                kalan          = pKalan,                         kalanPct  = Pct(pKalan),
                aySonuTahmin   = pAySonuTahmin,                  aySonuPct = Pct(pAySonuTahmin)
            },
            temsilciler = byRep.Values
                .OrderByDescending(x => x.Toplam)
                .Select(x => new {
                    ad = x.Ad,
                    toplam = x.Toplam,
                    gecmis = x.Gecmis,
                    soz = x.Soz,
                    tahsil = x.Tahsil,
                    kalan = Math.Max(0m, x.Toplam - x.Tahsil - x.Soz),
                    adet = x.Adet
                }),
            trend
        };

        _cache.Set(CK, result, TimeSpan.FromMinutes(2));
        return Json(result);
    }

    public class FaturaRow
    {
        public string?   FaturaNo { get; set; }
        public decimal   FaturaToplam { get; set; }
        public decimal   TahsilEdilen { get; set; }
        public decimal   BekleyenBakiye { get; set; }
        public DateTime? VadeTarihi { get; set; }
        public DateTime? TahsilTarihi { get; set; }
        public DateTime? SozTarihi { get; set; }
        public string?   Proje { get; set; }
        public int       Hukuki { get; set; }
    }
    private class RepAgg
    {
        public string  Ad { get; set; } = "";
        public decimal Tahsil { get; set; }
        public decimal Gecmis { get; set; }
        public decimal Soz    { get; set; }
        public decimal Toplam { get; set; }
        public int     Adet   { get; set; }
    }
    public class TrendRow { public string? Ay { get; set; } public decimal Tutar { get; set; } public int Adet { get; set; } public decimal Hedef { get; set; } public decimal GecTahsilat { get; set; } }
}
