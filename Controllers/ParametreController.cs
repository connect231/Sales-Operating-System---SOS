using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using SOS.DbData;
using SOS.Models.MsK;
using SOS.Services;

namespace SOS.Controllers
{
    [Authorize]
    [SosAdmin]
    public class ParametreController : Controller
    {
        private readonly IDbContextFactory<MskDbContext> _contextFactory;
        private readonly IHedefService _hedef;
        private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache _cache;

        public ParametreController(
            IDbContextFactory<MskDbContext> contextFactory,
            IHedefService hedef,
            Microsoft.Extensions.Caching.Memory.IMemoryCache cache)
        {
            _contextFactory = contextFactory;
            _hedef = hedef;
            _cache = cache;
        }

        public async Task<IActionResult> Index(int? yil, int? senaryoId)
        {
            using var db = _contextFactory.CreateDbContext();
            var now = DateTime.Now;
            var selectedYil = yil ?? now.Year;

            // Senaryolar (yıl filtresine göre, yoksa tümü)
            var senaryolar = await db.TBLSOS_HEDEF_SENARYOs
                .AsNoTracking()
                .Where(s => s.Aktif)
                .OrderByDescending(s => s.Yil).ThenBy(s => s.Id)
                .ToListAsync();

            // Aktif senaryo: parametre → yıla göre ilki → genel ilki → 1 (default)
            var aktifSenaryoId = senaryoId
                ?? senaryolar.FirstOrDefault(s => s.Yil == selectedYil)?.Id
                ?? senaryolar.FirstOrDefault()?.Id
                ?? 1;

            // Hedef ürünleri (Karne ile aynı liste)
            var urunler = await db.TBLSOS_HEDEF_URUNs
                .AsNoTracking()
                .Where(u => u.Aktif)
                .OrderBy(u => u.SiraNo)
                .ToListAsync();

            // Aylık ürün hedefleri — 3 SatisTipi birden (UI 3 sub-tab kullanıyor: Toplam/YS/Yen)
            var aylikTum = await db.TBLSOS_HEDEF_URUN_AYLIKs
                .AsNoTracking()
                .Where(x => x.SenaryoId == aktifSenaryoId)
                .ToListAsync();

            // (UrunId, Ay) → tutar — 3 ayrı dictionary (matris = Toplam, default sub-tab)
            var matris = aylikTum.Where(x => x.SatisTipi == "Toplam")
                .ToDictionary(x => (x.UrunId, (int)x.Ay), x => x.HedefTutar);
            var matrisYS = aylikTum.Where(x => x.SatisTipi == "YeniSatis")
                .ToDictionary(x => (x.UrunId, (int)x.Ay), x => x.HedefTutar);
            var matrisYen = aylikTum.Where(x => x.SatisTipi == "Yenileme")
                .ToDictionary(x => (x.UrunId, (int)x.Ay), x => x.HedefTutar);

            // Tab 2 — Ürün eşleştirme: ana ürün listesi + her birinin stok kodu sayımı
            var anaUrunler = await db.TBLSOS_ANA_URUNs
                .AsNoTracking()
                .Where(u => u.Aktif)
                .OrderBy(u => u.Sira)
                .ToListAsync();

            // Ana ürün başına eşleşme sayısı (header rozetleri için — full liste AJAX ile gelir)
            var eslestirmeSayilari = await db.TBLSOS_URUN_ESLESTIRMEs
                .AsNoTracking()
                .GroupBy(e => e.AnaUrunId)
                .Select(g => new { AnaUrunId = g.Key, Adet = g.Count() })
                .ToDictionaryAsync(x => x.AnaUrunId, x => x.Adet);
            var toplamEslestirme = eslestirmeSayilari.Values.Sum();

            ViewBag.SelectedYil = selectedYil;
            ViewBag.SelectedSenaryoId = aktifSenaryoId;
            ViewBag.Senaryolar = senaryolar;
            ViewBag.Urunler = urunler;
            ViewBag.HedefMatris = matris;
            ViewBag.HedefMatrisYS = matrisYS;
            ViewBag.HedefMatrisYen = matrisYen;
            ViewBag.AnaUrunler = anaUrunler;
            ViewBag.EslestirmeSayilari = eslestirmeSayilari;
            ViewBag.ToplamEslestirme = toplamEslestirme;
            ViewBag.YillikToplam = matris.Values.Sum();

            return View();
        }

        // ── TAB 2: Ürün eşleştirme — paginated/searchable JSON ──
        [HttpGet]
        public async Task<IActionResult> EslestirmeListesi(string? q = null, int? anaUrunId = null, int sayfa = 1, int boyut = 50)
        {
            if (sayfa < 1) sayfa = 1;
            if (boyut < 10 || boyut > 200) boyut = 50;

            using var db = _contextFactory.CreateDbContext();

            var query = db.TBLSOS_URUN_ESLESTIRMEs.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(q))
            {
                var qLower = q.Trim();
                query = query.Where(e =>
                    e.StokKodu.Contains(qLower)
                    || (e.UrunAdi != null && e.UrunAdi.Contains(qLower))
                    || (e.Mask != null && e.Mask.Contains(qLower)));
            }
            if (anaUrunId.HasValue && anaUrunId.Value > 0)
            {
                query = query.Where(e => e.AnaUrunId == anaUrunId.Value);
            }

            var toplam = await query.CountAsync();
            var items = await query
                .OrderBy(e => e.StokKodu)
                .Skip((sayfa - 1) * boyut)
                .Take(boyut)
                .Select(e => new
                {
                    e.Id,
                    e.StokKodu,
                    e.UrunAdi,
                    e.Mask,
                    e.LisansTipi,
                    e.AnaUrunId
                })
                .ToListAsync();

            return Json(new { ok = true, toplam, sayfa, boyut, items });
        }

        // ── TAB 2: Tek satır eşleştirme update ──
        [HttpPost]
        public async Task<IActionResult> UpdateEslestirme([FromBody] UpdateEslestirmeModel model)
        {
            if (model == null || model.Id <= 0 || model.AnaUrunId <= 0)
                return BadRequest(new { error = "Geçersiz parametre" });

            using var db = _contextFactory.CreateDbContext();
            var row = await db.TBLSOS_URUN_ESLESTIRMEs.FirstOrDefaultAsync(x => x.Id == model.Id);
            if (row == null) return NotFound(new { error = "Eşleştirme bulunamadı" });

            // Ana ürün gerçekten var mı?
            var anaUrunExists = await db.TBLSOS_ANA_URUNs.AnyAsync(u => u.Id == model.AnaUrunId && u.Aktif);
            if (!anaUrunExists) return BadRequest(new { error = "Ana ürün geçersiz" });

            row.AnaUrunId = model.AnaUrunId;
            await db.SaveChangesAsync();

            // Cockpit ürün grup map cache'i etkilenir → invalidate
            _cache.Remove("cockpit_urun_grup_map");
            _cache.Remove("cockpit_urunler");

            return Json(new { success = true, id = row.Id, anaUrunId = row.AnaUrunId });
        }

        public class UpdateEslestirmeModel
        {
            public int Id { get; set; }
            public int AnaUrunId { get; set; }
        }

        // ── Ürün × Ay editör matrisi save ──
        // satisTipi: "Toplam" (default) → orantı koruyarak YS/Yen yeniden hesaplanır.
        //            "YeniSatis"        → sadece YS güncellenir, Toplam = yeniYS + mevcutYen.
        //            "Yenileme"         → sadece Yen güncellenir, Toplam = mevcutYS + yeniYen.
        [HttpPost]
        public async Task<IActionResult> SaveHedeflerUrunAy([FromBody] HedefUrunAySaveModel model)
        {
            if (model?.Hucreler == null || model.Hucreler.Count == 0)
                return BadRequest(new { error = "Veri bulunamadı" });
            if (model.SenaryoId <= 0)
                return BadRequest(new { error = "Geçerli senaryo seçilmedi" });

            var tip = string.IsNullOrWhiteSpace(model.SatisTipi) ? "Toplam" : model.SatisTipi;
            if (tip != "Toplam" && tip != "YeniSatis" && tip != "Yenileme")
                return BadRequest(new { error = "satisTipi 'Toplam' | 'YeniSatis' | 'Yenileme' olmalı" });

            using var db = _contextFactory.CreateDbContext();

            // Mevcut tüm satırları (3 SatisTipi) tek seferde çek
            var existing = await db.TBLSOS_HEDEF_URUN_AYLIKs
                .Where(x => x.SenaryoId == model.SenaryoId)
                .ToListAsync();

            var existingMap = existing.ToDictionary(
                x => (x.UrunId, (int)x.Ay, x.SatisTipi),
                x => x);

            foreach (var hucre in model.Hucreler)
            {
                var ay = (byte)hucre.Ay;
                var oldT   = existingMap.TryGetValue((hucre.UrunId, hucre.Ay, "Toplam"),    out var rT) ? rT.HedefTutar : 0m;
                var oldYS  = existingMap.TryGetValue((hucre.UrunId, hucre.Ay, "YeniSatis"), out var rY) ? rY.HedefTutar : 0m;
                var oldYen = existingMap.TryGetValue((hucre.UrunId, hucre.Ay, "Yenileme"),  out var rN) ? rN.HedefTutar : 0m;

                decimal yeniT, yeniYS, yeniYen;

                if (tip == "YeniSatis")
                {
                    // YS'yi direkt yaz; Yen sabit; Toplam = YS + Yen
                    yeniYS  = hucre.HedefTutar;
                    yeniYen = oldYen;
                    yeniT   = Math.Round(yeniYS + yeniYen, 2);
                }
                else if (tip == "Yenileme")
                {
                    // Yen'i direkt yaz; YS sabit; Toplam = YS + Yen
                    yeniYS  = oldYS;
                    yeniYen = hucre.HedefTutar;
                    yeniT   = Math.Round(yeniYS + yeniYen, 2);
                }
                else
                {
                    // "Toplam" — eski davranış: Toplam'ı yaz, YS/Yen orantısı korunur
                    yeniT = hucre.HedefTutar;
                    if (oldT > 0)
                    {
                        // Orantı koru
                        yeniYS  = Math.Round(yeniT * (oldYS / oldT), 2);
                        yeniYen = Math.Round(yeniT - yeniYS, 2);
                    }
                    else
                    {
                        // Eski veri yok → %50 / %50
                        yeniYS  = Math.Round(yeniT / 2m, 2);
                        yeniYen = yeniT - yeniYS;
                    }
                }

                UpsertHedefRow(db, existingMap, model.SenaryoId, hucre.UrunId, ay, "Toplam",    yeniT);
                UpsertHedefRow(db, existingMap, model.SenaryoId, hucre.UrunId, ay, "YeniSatis", yeniYS);
                UpsertHedefRow(db, existingMap, model.SenaryoId, hucre.UrunId, ay, "Yenileme",  yeniYen);
            }

            await db.SaveChangesAsync();

            // Cache invalidate — Cockpit, FA, Karne hepsi taze veri çekmeli
            _hedef.InvalidateAll();
            _cache.Remove("cockpit_hedefler");

            return Json(new
            {
                success = true,
                satisTipi = tip,
                yillikToplam = model.Hucreler.Sum(h => h.HedefTutar)
            });
        }

        private static void UpsertHedefRow(
            MskDbContext db,
            Dictionary<(int UrunId, int Ay, string SatisTipi), TBLSOS_HEDEF_URUN_AYLIK> map,
            int senaryoId, int urunId, byte ay, string satisTipi, decimal tutar)
        {
            if (map.TryGetValue((urunId, ay, satisTipi), out var existing))
            {
                existing.HedefTutar = tutar;
            }
            else
            {
                db.TBLSOS_HEDEF_URUN_AYLIKs.Add(new TBLSOS_HEDEF_URUN_AYLIK
                {
                    SenaryoId = senaryoId,
                    UrunId = urunId,
                    Ay = ay,
                    SatisTipi = satisTipi,
                    HedefTutar = tutar
                });
            }
        }

        // ── ESKI: Tek-tutar GENEL hedef save (geri uyumluluk — TBLSOS_HEDEF_AYLIK fallback için) ──
        [HttpPost]
        public async Task<IActionResult> SaveHedefler([FromBody] HedefSaveModel model)
        {
            if (model?.Hedefler == null || model.Hedefler.Count == 0)
                return BadRequest(new { error = "Veri bulunamadı" });

            using var db = _contextFactory.CreateDbContext();

            foreach (var h in model.Hedefler)
            {
                var existing = await db.TBLSOS_HEDEF_AYLIKs
                    .FirstOrDefaultAsync(x => x.Yil == h.Yil && x.Ay == h.Ay && x.Tip == "GENEL" && x.AnaUrunId == null);

                if (existing != null)
                {
                    existing.HedefTutar = h.HedefTutar;
                }
                else
                {
                    db.TBLSOS_HEDEF_AYLIKs.Add(new TBLSOS_HEDEF_AYLIK
                    {
                        Yil = h.Yil,
                        Ay = h.Ay,
                        Tip = "GENEL",
                        AnaUrunId = null,
                        HedefTutar = h.HedefTutar,
                        Aktif = true
                    });
                }
            }

            await db.SaveChangesAsync();
            _hedef.InvalidateAll();
            _cache.Remove("cockpit_hedefler");
            return Json(new { success = true, toplam = model.Hedefler.Sum(h => h.HedefTutar) });
        }

        public class HedefUrunAySaveModel
        {
            public int SenaryoId { get; set; }
            // "Toplam" (default) | "YeniSatis" | "Yenileme"
            public string? SatisTipi { get; set; }
            public List<HedefUrunAyHucre> Hucreler { get; set; } = new();
        }

        public class HedefUrunAyHucre
        {
            public int UrunId { get; set; }
            public int Ay { get; set; }
            public decimal HedefTutar { get; set; }
        }

        public class HedefSaveModel
        {
            public List<HedefItem> Hedefler { get; set; } = new();
        }

        public class HedefItem
        {
            public int Yil { get; set; }
            public int Ay { get; set; }
            public decimal HedefTutar { get; set; }
        }
    }
}
