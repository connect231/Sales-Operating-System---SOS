using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;
using SOS.DbData;
using SOS.Models.MsK;
using SOS.Models.ViewModels;

namespace SOS.Services;

public interface IHedefService
{
    Task<HedefBantViewModel> GetBantAsync(int yil, DateTime donemStart, DateTime donemEnd, int? temsilciId, string donemEtiket);
    Task<HedefSirketOzetiViewModel> GetSirketOzetiAsync(int yil);
    Task<List<HedefTemsilciSatirViewModel>> GetTemsilciListesiAsync(int yil);
    Task<HedefTemsilciDetayViewModel?> GetTemsilciDetayAsync(int yil, int temsilciId);
    Task<List<HedefUrunSatirViewModel>> GetUrunListesiAsync(int yil);
    Task<HedefUrunDetayViewModel?> GetUrunDetayAsync(int yil, int urunId);
    Task<HedefUrunDetayViewModel?> GetUrunDetayByAdAsync(int yil, string urunAd);
    Task<List<TBLSOS_HEDEF_TEMSILCI>> GetTemsilcilerAsync();
    Task<List<HedefAnalizDetaySatir>> GetAnalizDetayAsync(string tab, int yil, DateTime start, DateTime end);

    /// <summary>
    /// Tab 2 alt panel için Excel benzeri matris: temsilci × ürün × yıllık (Toplam/YS/Yen).
    /// Tek sorgu (TBLSOS_HEDEF_TEMSILCI_AYLIK), in-memory aggregate.
    /// </summary>
    Task<HedefTemsilciUrunMatrisViewModel> GetTemsilciUrunMatrisAsync(int yil);

    /// <summary>
    /// Şirket geneli aylık hedef sözlüğü (Ay → Tutar). Cockpit dashboard tüm hesaplarda bunu kullanır.
    /// Kaynak: TBLSOS_HEDEF_URUN_AYLIK (SENARYO_ID=1, SatisTipi=Toplam). Yoksa YS+Yenileme toplamı.
    /// Yine boşsa eski TBLSOS_HEDEF_AYLIK (Tip=GENEL) fallback.
    /// </summary>
    Task<Dictionary<int, decimal>> GetGenelAylikSozlukAsync(int yil);

    /// <summary>
    /// Tarih aralığındaki şirket geneli toplam hedef (full-ay kabul — Cockpit/FA tutarlılığı için).
    /// </summary>
    Task<decimal> GetGenelHedefRangeAsync(int yil, DateTime start, DateTime end);
    void InvalidateAll();
}

public class HedefAnalizDetaySatir
{
    public string Isim { get; set; } = "";
    public decimal Hedef { get; set; }
    public decimal Gerceklesen { get; set; }
    public decimal AcikTeklif { get; set; }

    // Ürün tab'ında alt satırlar (Yeni Satış / Yenileme) için kırılım.
    // Dönem filtresinde aynı aralık kullanılır; AcikTeklif kırılımı yok (alt satırda gösterilmez).
    public decimal HedefYS { get; set; }
    public decimal HedefYen { get; set; }
    public decimal GerceklesenYS { get; set; }
    public decimal GerceklesenYen { get; set; }
}

public class HedefService : IHedefService
{
    private readonly IDbContextFactory<MskDbContext> _contextFactory;
    private readonly IMemoryCache _cache;
    private readonly ICockpitDataService _cockpit;
    private readonly ILogger<HedefService> _logger;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new();
    private static readonly TimeSpan TTL = TimeSpan.FromMinutes(5);
    private static readonly HashSet<string> _keys = new();

    private const int SENARYO_ID = 1;  // 600M (tek senaryo şimdilik)

    public HedefService(
        IDbContextFactory<MskDbContext> contextFactory,
        IMemoryCache cache,
        ICockpitDataService cockpit,
        ILogger<HedefService> logger)
    {
        _contextFactory = contextFactory;
        _cache = cache;
        _cockpit = cockpit;
        _logger = logger;
    }

    // ── BANT (Fırsat Analizi tepesindeki tek satır şerit) ──

    public async Task<HedefBantViewModel> GetBantAsync(int yil, DateTime donemStart, DateTime donemEnd, int? temsilciId, string donemEtiket)
    {
        var key = $"hedef_bant_{yil}_{donemStart:yyyyMMdd}_{donemEnd:yyyyMMdd}_{temsilciId?.ToString() ?? "all"}";
        return await CachedAsync(key, async () =>
        {
            var (donemHedef, hedefYS, hedefYen) = await ComputeHedefForRangeAsync(yil, donemStart, donemEnd, temsilciId);
            var yillikHedef = await ComputeHedefForRangeAsync(yil, new DateTime(yil, 1, 1), new DateTime(yil, 12, 31), temsilciId);

            // Account-bazlı gerçekleşen (ProposalOwnerId güvenilmez, AccountId-Temsilci eşleşmesi kullanılır)
            var (gercTotal, gercYS, gercYen) = await GetGerceklesenAsync(donemStart, donemEnd, temsilciId);

            var (ytdHedef, _, _) = await ComputeHedefForRangeAsync(yil, new DateTime(yil, 1, 1), DateTime.Now.Date, temsilciId);
            var (ytdGerc, _, _) = await GetGerceklesenAsync(new DateTime(yil, 1, 1), DateTime.Now.Date, temsilciId);
            var gunGecti = (DateTime.Now.Date - new DateTime(yil, 1, 1)).Days + 1;
            var runRate = gunGecti > 0 ? ytdGerc * (365m / gunGecti) : 0m;

            var attainmentRaw = donemHedef > 0 ? gercTotal / donemHedef * 100m : 0m;
            var attainmentTimeAdj = ytdHedef > 0 ? ytdGerc / ytdHedef * 100m : 0m;

            var temsilciAd = "Tüm Şirket";
            if (temsilciId.HasValue)
            {
                using var db = _contextFactory.CreateDbContext();
                var t = await db.TBLSOS_HEDEF_TEMSILCIs.FirstOrDefaultAsync(x => x.Id == temsilciId.Value);
                temsilciAd = t?.Ad ?? "Tüm Şirket";
            }

            return new HedefBantViewModel
            {
                TemsilciAd = temsilciAd,
                Donem = donemEtiket,
                YillikHedef = yillikHedef.toplam,
                DonemHedef = donemHedef,
                Gerceklesen = gercTotal,
                HedefYS = hedefYS,
                HedefYen = hedefYen,
                GerceklesenYS = gercYS,
                GerceklesenYen = gercYen,
                RunRate = runRate,
                AttainmentTimeAdj = attainmentTimeAdj,
                AttainmentRaw = attainmentRaw,
                RenkSinifi = ResolveRenk(attainmentTimeAdj)
            };
        });
    }

    // ── ŞİRKET ÖZETİ (Tab 1) ──

    public async Task<HedefSirketOzetiViewModel> GetSirketOzetiAsync(int yil)
    {
        var key = $"hedef_sirket_ozet_{yil}";
        return await CachedAsync(key, async () =>
        {
            // Gerçekleşen: tüm yıl, account-bazlı (şirket geneli — temsilci filtresi yok)
            // YTD = yıl başı → bulunduğumuz ayın SONU (tam ay) — Panel ile bire bir tutar.
            var ytdStart = new DateTime(yil, 1, 1);
            var nowDate = DateTime.Now.Date;
            var ytdEnd = new DateTime(yil, nowDate.Month, DateTime.DaysInMonth(yil, nowDate.Month));

            // ── 5 bağımsız sorgu paralel (her biri kendi DbContext'i) ──
            // Eski: 6 ardışık sorgu, ~600-800ms cold path. Yeni: en yavaş sorgu kadar (~150-250ms).
            var yillikTask = Task.Run(async () =>
            {
                using var db1 = _contextFactory.CreateDbContext();
                return await db1.TBLSOS_HEDEF_URUN_YILLIKs.AsNoTracking()
                    .Where(x => x.SenaryoId == SENARYO_ID)
                    .Include(x => x.Urun)
                    .ToListAsync();
            });

            // Tüm SatisTipi'leri tek sorguda al (Toplam + YeniSatis + Yenileme); in-memory filtrele.
            var aylikTumTask = Task.Run(async () =>
            {
                using var db2 = _contextFactory.CreateDbContext();
                return await db2.TBLSOS_HEDEF_URUN_AYLIKs.AsNoTracking()
                    .Where(x => x.SenaryoId == SENARYO_ID)
                    .Include(x => x.Urun)
                    .ToListAsync();
            });

            var ytdGercTask = GetGerceklesenAsync(ytdStart, ytdEnd, null);
            var urunMatrisTask = GetGerceklesenUrunMatrisAsync(yil, null);
            var ayGercTask = GetGerceklesenByAyAsync(yil, null);

            await Task.WhenAll(yillikTask, aylikTumTask, ytdGercTask, urunMatrisTask, ayGercTask);
            var yillik = yillikTask.Result;
            var aylikTum = aylikTumTask.Result;
            var aylik = aylikTum.Where(x => x.SatisTipi == "Toplam").ToList();
            var aylikDetay = aylikTum.Where(x => x.SatisTipi == "YeniSatis" || x.SatisTipi == "Yenileme").ToList();
            var (ytdGerc, ytdGercYS, ytdGercYen) = ytdGercTask.Result;
            var (urunMatris, _) = urunMatrisTask.Result;
            var ayGercSirket = ayGercTask.Result;

            var yillikToplam = yillik.Sum(x => x.HedefToplam);
            var yillikYS = yillik.Sum(x => x.HedefYeniSatis);
            var yillikYen = yillik.Sum(x => x.HedefYenileme);

            // YTD hedef = ay tamamlanmış aylar + içinde olduğumuz ayın oranı
            var ytdHedef = ComputeYtdHedef(aylik, yil);
            // YS / Yen ayrı YTD hedef
            var aylikYS  = aylikTum.Where(x => x.SatisTipi == "YeniSatis").ToList();
            var aylikYen = aylikTum.Where(x => x.SatisTipi == "Yenileme").ToList();
            var ytdHedefYS  = ComputeYtdHedef(aylikYS, yil);
            var ytdHedefYen = ComputeYtdHedef(aylikYen, yil);

            // Run-Rate hesabı (kullanıcı kararı 2026-05-06):
            //   gunGecti = bugüne kadar geçen takvim günü (sektör standart "Annualized Run Rate")
            //   YS = linear (cap yok) — açık varsa burada görünür
            //   Yen = MIN(linear, yıllık_hedef) — yenileme sözleşmelerin tekrarı, yıllık tavanı var
            var gunGecti = (nowDate - ytdStart).Days + 1;
            var runRateYSRaw = gunGecti > 0 ? ytdGercYS * (365m / gunGecti) : 0m;
            var runRateYenRaw = gunGecti > 0 ? ytdGercYen * (365m / gunGecti) : 0m;
            var runRateYen = yillikYen > 0 ? Math.Min(runRateYenRaw, yillikYen) : runRateYenRaw;
            var yenCappe = runRateYenRaw >= yillikYen && yillikYen > 0;
            var runRate = runRateYSRaw + runRateYen;
            var ysAcik = Math.Max(yillikYS - runRateYSRaw, 0m);

            // YS hız oranı: mevcut günlük hız / hedef günlük hız (0.29 = %29 → 3.4× artış lazım)
            var ysGunlukHedef = (365 - gunGecti) > 0
                ? Math.Max(yillikYS - ytdGercYS, 0m) / (365 - gunGecti)
                : 0m;
            var ysGunlukMevcut = gunGecti > 0 ? ytdGercYS / gunGecti : 0m;
            var ysHizOrani = ysGunlukHedef > 0 ? ysGunlukMevcut / ysGunlukHedef : 0m;

            var attainment = ytdHedef > 0 ? ytdGerc / ytdHedef * 100m : 0m;

            // YS/Yen lookup: (UrunId, Ay) → HedefTutar
            var ysMap = aylikDetay.Where(x => x.SatisTipi == "YeniSatis")
                .ToDictionary(x => (x.UrunId, (int)x.Ay), x => x.HedefTutar);
            var yenMap = aylikDetay.Where(x => x.SatisTipi == "Yenileme")
                .ToDictionary(x => (x.UrunId, (int)x.Ay), x => x.HedefTutar);

            // Heatmap: 8 ürün × 12 ay (her hücrede Toplam + YS + Yen)
            var heatmap = aylik.Select(x =>
            {
                var gerc = urunMatris.TryGetValue((x.UrunId, x.Ay), out var v) ? v : (toplam: 0m, ys: 0m, yen: 0m);
                return new HedefHeatmapHucre
                {
                    UrunId = x.UrunId,
                    UrunAd = x.Urun?.Ad ?? "",
                    Ay = x.Ay,
                    Hedef = x.HedefTutar,
                    HedefYS = ysMap.TryGetValue((x.UrunId, x.Ay), out var ys) ? ys : 0m,
                    HedefYen = yenMap.TryGetValue((x.UrunId, x.Ay), out var yen) ? yen : 0m,
                    Gerceklesen = gerc.toplam,
                    GerceklesenYS = gerc.ys,
                    GerceklesenYen = gerc.yen,
                    Attainment = x.HedefTutar > 0 ? gerc.toplam / x.HedefTutar * 100m : 0m
                };
            }).ToList();

            // ayGercSirket ve aylikDetay artık yukarıdaki paralel batch'te yüklendi.

            var ayToplam = aylik.GroupBy(x => x.Ay).Select(g =>
            {
                var hedefYS = aylikDetay.Where(x => x.Ay == g.Key && x.SatisTipi == "YeniSatis").Sum(x => x.HedefTutar);
                var hedefYen = aylikDetay.Where(x => x.Ay == g.Key && x.SatisTipi == "Yenileme").Sum(x => x.HedefTutar);
                var hedef = g.Sum(x => x.HedefTutar);
                var gerc = ayGercSirket.TryGetValue(g.Key, out var gv) ? gv : (0m, 0m, 0m);
                return new HedefAyToplam
                {
                    Ay = g.Key,
                    Hedef = hedef,
                    HedefYS = hedefYS,
                    HedefYen = hedefYen,
                    Gerceklesen = gerc.Item1,
                    GerceklesenYS = gerc.Item2,
                    GerceklesenYen = gerc.Item3,
                    Attainment = hedef > 0 ? gerc.Item1 / hedef * 100m : 0m
                };
            }).OrderBy(x => x.Ay).ToList();

            // Ürün toplam gerçekleşeni (12 ay × YS/Yen)
            var urunToplam = yillik.OrderBy(x => x.Urun?.SiraNo).Select(x =>
            {
                decimal gercTop = 0m, gercYS = 0m, gercYen = 0m;
                for (byte ay = 1; ay <= 12; ay++)
                {
                    if (urunMatris.TryGetValue((x.UrunId, ay), out var v))
                    {
                        gercTop += v.toplam;
                        gercYS += v.ys;
                        gercYen += v.yen;
                    }
                }
                return new HedefUrunToplam
                {
                    UrunId = x.UrunId,
                    UrunAd = x.Urun?.Ad ?? "",
                    HedefYS = x.HedefYeniSatis,
                    HedefYen = x.HedefYenileme,
                    HedefToplam = x.HedefToplam,
                    GerceklesenYS = gercYS,
                    GerceklesenYen = gercYen,
                    GerceklesenToplam = gercTop,
                    Attainment = x.HedefToplam > 0 ? gercTop / x.HedefToplam * 100m : 0m
                };
            }).ToList();

            return new HedefSirketOzetiViewModel
            {
                Yil = yil,
                YillikHedef = yillikToplam,
                YtdHedef = ytdHedef,
                YtdGerceklesen = ytdGerc,
                RunRate = runRate,
                RunRateYS = runRateYSRaw,
                RunRateYen = runRateYen,
                YSAcik = ysAcik,
                YenCappe = yenCappe,
                YSHizOrani = ysHizOrani,
                Attainment = attainment,
                RenkSinifi = ResolveRenk(attainment),
                YillikHedefYS = yillikYS,
                YillikHedefYen = yillikYen,
                YtdHedefYS = ytdHedefYS,
                YtdHedefYen = ytdHedefYen,
                GerceklesenYS = ytdGercYS,
                GerceklesenYen = ytdGercYen,
                Heatmap = heatmap,
                AyToplamlari = ayToplam,
                UrunToplamlari = urunToplam
            };
        });
    }

    // ── TEMSİLCİ LİSTESİ (Tab 2) ──

    public async Task<List<HedefTemsilciSatirViewModel>> GetTemsilciListesiAsync(int yil)
    {
        var key = $"hedef_temsilci_liste_{yil}";
        return await CachedAsync(key, async () =>
        {
            var ytdStart = new DateTime(yil, 1, 1);
            // YTD = yıl başı → bulunduğumuz ayın SONU (tam ay) — Panel/AnalizDetay ile bire bir tutar.
            var nowDate = DateTime.Now.Date;
            var ytdEnd = new DateTime(yil, nowDate.Month, DateTime.DaysInMonth(yil, nowDate.Month));
            var ytdEndMonth = (byte)ytdEnd.Month;

            // Paralel: 3 bağımsız sorgu, her biri kendi DbContext'inde (DbContext concurrent-safe değil).
            // Önceki implementasyon her temsilci için 2 ayrı sorgu (9×2=18 round-trip) atıyordu.
            var temsilcilerTask = Task.Run(async () =>
            {
                using var db1 = _contextFactory.CreateDbContext();
                return await db1.TBLSOS_HEDEF_TEMSILCIs
                    .AsNoTracking()
                    .Where(t => t.Aktif).OrderBy(t => t.SiraNo).ToListAsync();
            });

            var aylikTumTask = Task.Run(async () =>
            {
                using var db2 = _contextFactory.CreateDbContext();
                return await db2.TBLSOS_HEDEF_TEMSILCI_AYLIKs
                    .AsNoTracking()
                    .Where(x => x.SenaryoId == SENARYO_ID)
                    .Select(x => new { x.TemsilciId, x.Ay, x.SatisTipi, x.HedefTutar })
                    .ToListAsync();
            });

            var matrisTask = GetGerceklesenTemsilciAyMatrisAsync(yil);

            await Task.WhenAll(temsilcilerTask, aylikTumTask, matrisTask);
            var temsilciler = temsilcilerTask.Result;
            var aylikTum = aylikTumTask.Result;
            var matris = matrisTask.Result;

            // TemsilciId × Ay × SatisTipi → tutar (in-memory aggregate)
            var byTemsilci = aylikTum.GroupBy(x => x.TemsilciId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var sonuc = new List<HedefTemsilciSatirViewModel>();
            foreach (var t in temsilciler)
            {
                var rows = byTemsilci.TryGetValue(t.Id, out var rs) ? rs : new();

                // Yıllık hedef = tüm aylar toplamı
                var yillikYS = rows.Where(x => x.SatisTipi == "YeniSatis").Sum(x => x.HedefTutar);
                var yillikYen = rows.Where(x => x.SatisTipi == "Yenileme").Sum(x => x.HedefTutar);
                var yillikToplam = yillikYS + yillikYen;

                // YTD hedef = gün-orantılı (mevcut ComputeHedefForRangeAsync mantığıyla aynı)
                decimal ytdYS = 0m, ytdYen = 0m;
                for (byte ay = 1; ay <= 12; ay++)
                {
                    var oran = AyOrani(yil, ay, ytdStart, ytdEnd);
                    if (oran <= 0) continue;
                    ytdYS += rows.Where(x => x.Ay == ay && x.SatisTipi == "YeniSatis").Sum(x => x.HedefTutar) * oran;
                    ytdYen += rows.Where(x => x.Ay == ay && x.SatisTipi == "Yenileme").Sum(x => x.HedefTutar) * oran;
                }
                var ytdHedefToplam = ytdYS + ytdYen;

                // Gerçekleşen — matris üzerinden tek pass
                decimal ytdGerc = 0m, ytdGercYS = 0m, ytdGercYen = 0m;
                for (byte ay = 1; ay <= ytdEndMonth; ay++)
                {
                    if (matris.TryGetValue((t.Id, ay), out var v))
                    {
                        ytdGerc += v.toplam;
                        ytdGercYS += v.ys;
                        ytdGercYen += v.yen;
                    }
                }

                // Tahmini kapanış: Şirket Özeti ile aynı formül — gün = bugüne kadar geçen takvim günü
                // (ay sonu DEĞİL); YS linear + Yen MIN(linear, yıllık_hedef).
                var gun = (nowDate - ytdStart).Days + 1;
                var runRateYSRaw = gun > 0 ? ytdGercYS * (365m / gun) : 0m;
                var runRateYenRaw = gun > 0 ? ytdGercYen * (365m / gun) : 0m;
                var runRateYen = yillikYen > 0 ? Math.Min(runRateYenRaw, yillikYen) : runRateYenRaw;
                var runRate = runRateYSRaw + runRateYen;
                var attTimeAdj = ytdHedefToplam > 0 ? ytdGerc / ytdHedefToplam * 100m : 0m;
                var attRaw = yillikToplam > 0 ? ytdGerc / yillikToplam * 100m : 0m;

                sonuc.Add(new HedefTemsilciSatirViewModel
                {
                    TemsilciId = t.Id,
                    Ad = t.Ad,
                    Kanal = t.Kanal,
                    CrmPersonId = t.CrmPersonId,
                    YillikHedef = yillikToplam,
                    YtdHedef = ytdHedefToplam,
                    YtdGerceklesen = ytdGerc,
                    RunRate = runRate,
                    AttainmentTimeAdj = attTimeAdj,
                    Attainment = attRaw,
                    HedefYS = yillikYS,
                    HedefYen = yillikYen,
                    YtdHedefYS = ytdYS,
                    YtdHedefYen = ytdYen,
                    GerceklesenYS = ytdGercYS,
                    GerceklesenYen = ytdGercYen,
                    RenkSinifi = ResolveRenk(attTimeAdj)
                });
            }

            // Sıralama: yıllık hedefe göre büyükten küçüğe
            return sonuc.OrderByDescending(x => x.YillikHedef).ToList();
        });
    }

    // ── TEMSİLCİ DETAY (Tab 3) ──

    public async Task<HedefTemsilciDetayViewModel?> GetTemsilciDetayAsync(int yil, int temsilciId)
    {
        var key = $"hedef_temsilci_detay_{yil}_{temsilciId}";
        return await CachedAsync(key, async () =>
        {
            // 4 bağımsız sorgu paralel — her biri kendi DbContext'i.
            // Eski: temsilci+aylik tek context, sonra 3 ardışık helper = 5 sequential round-trip.
            // Yeni: temsilci+aylik tek context (sıralı), 3 helper paralel.
            using var db = _contextFactory.CreateDbContext();
            var t = await db.TBLSOS_HEDEF_TEMSILCIs.FirstOrDefaultAsync(x => x.Id == temsilciId);
            if (t == null) return null;

            var aylik = await db.TBLSOS_HEDEF_TEMSILCI_AYLIKs
                .AsNoTracking()
                .Where(x => x.SenaryoId == SENARYO_ID && x.TemsilciId == temsilciId)
                .Include(x => x.Urun).ToListAsync();

            var yillikYS = aylik.Where(x => x.SatisTipi == "YeniSatis").Sum(x => x.HedefTutar);
            var yillikYen = aylik.Where(x => x.SatisTipi == "Yenileme").Sum(x => x.HedefTutar);
            var yillikToplam = yillikYS + yillikYen;

            var ytdStart = new DateTime(yil, 1, 1);
            // YTD = yıl başı → bulunduğumuz ayın SONU (tam ay)
            var nowDate2 = DateTime.Now.Date;
            var ytdEnd = new DateTime(yil, nowDate2.Month, DateTime.DaysInMonth(yil, nowDate2.Month));
            var ytdEndMonth = (byte)ytdEnd.Month;

            // YTD hedef artık aylık'tan in-memory hesaplanır (önceden ayrı bir DB sorgusu vardı)
            var ytdHedefToplam = aylik.Where(x => x.Ay <= ytdEndMonth).Sum(x => x.HedefTutar);

            // BUG #2 fix: TemsilciListesi ile aynı kaynak kullan — matris tek doğru yer.
            // Eski yol GetGerceklesenAsync (account-bazlı) ile matris (kalem-bazlı) farklı sonuç
            // veriyordu; özellikle Yenileme tarafında ₺10M+ sapma. Artık ikisi de matristen besleniyor.
            var matrisTask = GetGerceklesenTemsilciAyMatrisAsync(yil);
            var urunMatrisTask = GetGerceklesenUrunMatrisAsync(yil, temsilciId);
            await Task.WhenAll(matrisTask, urunMatrisTask);
            var temsilciMatris = matrisTask.Result;
            var (urunMatrisT, _) = urunMatrisTask.Result;

            // YTD gerç ve ay-bazlı toplam: temsilci matrisinden tek pass
            decimal ytdGerc = 0m, ytdGercYS = 0m, ytdGercYen = 0m;
            var ayGerc = new Dictionary<int, (decimal toplam, decimal ys, decimal yen)>();
            for (byte ay = 1; ay <= 12; ay++)
            {
                if (temsilciMatris.TryGetValue((temsilciId, ay), out var v))
                {
                    ayGerc[ay] = (v.toplam, v.ys, v.yen);
                    if (ay <= ytdEndMonth)
                    {
                        ytdGerc += v.toplam;
                        ytdGercYS += v.ys;
                        ytdGercYen += v.yen;
                    }
                }
            }

            // Tahmini kapanış: Şirket Özeti ile aynı formül — gün = bugüne kadar geçen takvim günü.
            var gun = (nowDate2 - ytdStart).Days + 1;
            var runRateYSRawT = gun > 0 ? ytdGercYS * (365m / gun) : 0m;
            var runRateYenRawT = gun > 0 ? ytdGercYen * (365m / gun) : 0m;
            var runRateYenT = yillikYen > 0 ? Math.Min(runRateYenRawT, yillikYen) : runRateYenRawT;
            var runRate = runRateYSRawT + runRateYenT;
            var attTimeAdj = ytdHedefToplam > 0 ? ytdGerc / ytdHedefToplam * 100m : 0m;

            // Temsilci aylık tablosunda "Toplam" SatisTipi YOK → Toplam = YS + Yen (in-memory)
            var matris = aylik.GroupBy(x => new { x.UrunId, x.Ay })
                .Select(g =>
                {
                    var hedefYS  = g.Where(x => x.SatisTipi == "YeniSatis").Sum(x => x.HedefTutar);
                    var hedefYen = g.Where(x => x.SatisTipi == "Yenileme").Sum(x => x.HedefTutar);
                    var hedef = hedefYS + hedefYen;
                    var gerc = urunMatrisT.TryGetValue((g.Key.UrunId, g.Key.Ay), out var v) ? v : (toplam: 0m, ys: 0m, yen: 0m);
                    return new HedefHeatmapHucre
                    {
                        UrunId = g.Key.UrunId,
                        UrunAd = g.First().Urun?.Ad ?? "",
                        Ay = g.Key.Ay,
                        Hedef = hedef,
                        HedefYS = hedefYS,
                        HedefYen = hedefYen,
                        Gerceklesen = gerc.toplam,
                        GerceklesenYS = gerc.ys,
                        GerceklesenYen = gerc.yen,
                        Attainment = hedef > 0 ? gerc.toplam / hedef * 100m : 0m
                    };
                }).OrderBy(x => x.UrunId).ThenBy(x => x.Ay).ToList();

            var ayTop = aylik.GroupBy(x => x.Ay).Select(g =>
            {
                var hedefYS = g.Where(x => x.SatisTipi == "YeniSatis").Sum(x => x.HedefTutar);
                var hedefYen = g.Where(x => x.SatisTipi == "Yenileme").Sum(x => x.HedefTutar);
                var hedef = hedefYS + hedefYen;
                var gerc = ayGerc.TryGetValue(g.Key, out var gv) ? gv : (0m, 0m, 0m);
                return new HedefAyToplam
                {
                    Ay = g.Key,
                    Hedef = hedef,
                    HedefYS = hedefYS,
                    HedefYen = hedefYen,
                    Gerceklesen = gerc.Item1,
                    GerceklesenYS = gerc.Item2,
                    GerceklesenYen = gerc.Item3,
                    Attainment = hedef > 0 ? gerc.Item1 / hedef * 100m : 0m
                };
            }).OrderBy(x => x.Ay).ToList();

            var urunTop = aylik.GroupBy(x => x.UrunId)
                .Select(g =>
                {
                    decimal gercTop = 0m, gercYS = 0m, gercYen = 0m;
                    for (byte ay = 1; ay <= 12; ay++)
                    {
                        if (urunMatrisT.TryGetValue((g.Key, ay), out var v))
                        {
                            gercTop += v.toplam;
                            gercYS += v.ys;
                            gercYen += v.yen;
                        }
                    }
                    var hedefToplam = g.Sum(x => x.HedefTutar);
                    return new HedefUrunToplam
                    {
                        UrunId = g.Key,
                        UrunAd = g.First().Urun?.Ad ?? "",
                        HedefYS = g.Where(x => x.SatisTipi == "YeniSatis").Sum(x => x.HedefTutar),
                        HedefYen = g.Where(x => x.SatisTipi == "Yenileme").Sum(x => x.HedefTutar),
                        HedefToplam = hedefToplam,
                        GerceklesenYS = gercYS,
                        GerceklesenYen = gercYen,
                        GerceklesenToplam = gercTop,
                        Attainment = hedefToplam > 0 ? gercTop / hedefToplam * 100m : 0m
                    };
                }).OrderBy(x => x.UrunId).ToList();

            // Pipeline coverage (basit: SP_FIRSAT_PIPELINE_V2 yerine doğrudan teklif/fırsat üzerinden)
            // İleride detaylandırılabilir; şimdilik 0 bırakıyoruz
            var kalanHedef = Math.Max(0m, yillikToplam - ytdGerc);
            decimal acikPipeline = 0m;
            decimal coverage = kalanHedef > 0 ? acikPipeline / kalanHedef : 0m;

            return new HedefTemsilciDetayViewModel
            {
                TemsilciId = t.Id,
                Ad = t.Ad,
                Kanal = t.Kanal,
                CrmPersonId = t.CrmPersonId,
                YillikHedef = yillikToplam,
                YillikHedefYS = yillikYS,
                YillikHedefYen = yillikYen,
                YtdHedef = ytdHedefToplam,
                YtdGerceklesen = ytdGerc,
                RunRate = runRate,
                AttainmentTimeAdj = attTimeAdj,
                GerceklesenYS = ytdGercYS,
                GerceklesenYen = ytdGercYen,
                UrunAyMatris = matris,
                AyToplamlari = ayTop,
                UrunToplamlari = urunTop,
                AcikPipelineTutar = acikPipeline,
                KalanHedef = kalanHedef,
                PipelineCoverage = coverage
            };
        });
    }

    // ── ÜRÜN LİSTESİ (Şirket Özeti — kart grid) ──
    // Temsilci kartının ürün eksenli karşılığı; aynı VM şablonu, aynı renk/run-rate mantığı.

    public async Task<List<HedefUrunSatirViewModel>> GetUrunListesiAsync(int yil)
    {
        var key = $"hedef_urun_liste_{yil}";
        return await CachedAsync(key, async () =>
        {
            var ytdStart = new DateTime(yil, 1, 1);
            var nowDate = DateTime.Now.Date;
            var ytdEnd = new DateTime(yil, nowDate.Month, DateTime.DaysInMonth(yil, nowDate.Month));
            var ytdEndMonth = (byte)ytdEnd.Month;

            var urunlerTask = Task.Run(async () =>
            {
                using var db = _contextFactory.CreateDbContext();
                return await db.TBLSOS_HEDEF_URUNs.AsNoTracking()
                    .Where(u => u.Aktif).OrderBy(u => u.SiraNo).ToListAsync();
            });

            var yillikTask = Task.Run(async () =>
            {
                using var db = _contextFactory.CreateDbContext();
                return await db.TBLSOS_HEDEF_URUN_YILLIKs.AsNoTracking()
                    .Where(x => x.SenaryoId == SENARYO_ID).ToListAsync();
            });

            var aylikTask = Task.Run(async () =>
            {
                using var db = _contextFactory.CreateDbContext();
                return await db.TBLSOS_HEDEF_URUN_AYLIKs.AsNoTracking()
                    .Where(x => x.SenaryoId == SENARYO_ID)
                    .Select(x => new { x.UrunId, x.Ay, x.SatisTipi, x.HedefTutar })
                    .ToListAsync();
            });

            var matrisTask = GetGerceklesenUrunMatrisAsync(yil, null);

            await Task.WhenAll(urunlerTask, yillikTask, aylikTask, matrisTask);
            var urunler = urunlerTask.Result;
            var yillikMap = yillikTask.Result.ToDictionary(x => x.UrunId);
            var aylik = aylikTask.Result;
            var (urunMatris, _) = matrisTask.Result;

            // UrunId → Ay → (Toplam, YS, Yen) hedef sözlüğü
            var aylikByUrun = aylik.GroupBy(x => x.UrunId).ToDictionary(g => g.Key, g => g.ToList());

            var sonuc = new List<HedefUrunSatirViewModel>();
            foreach (var u in urunler)
            {
                var rows = aylikByUrun.TryGetValue(u.Id, out var rs) ? rs : new();
                yillikMap.TryGetValue(u.Id, out var yillik);

                var yillikYS  = yillik?.HedefYeniSatis ?? rows.Where(x => x.SatisTipi == "YeniSatis").Sum(x => x.HedefTutar);
                var yillikYen = yillik?.HedefYenileme  ?? rows.Where(x => x.SatisTipi == "Yenileme").Sum(x => x.HedefTutar);
                var yillikToplam = yillik?.HedefToplam ?? (yillikYS + yillikYen);

                if (yillikToplam <= 0) continue; // hedefsiz ürünleri kart gridinde gösterme

                // YTD hedef = gün-orantılı (Toplam ay bazlı)
                decimal ytdHedef = 0m, ytdYS = 0m, ytdYen = 0m;
                for (byte ay = 1; ay <= 12; ay++)
                {
                    var oran = AyOrani(yil, ay, ytdStart, ytdEnd);
                    if (oran <= 0) continue;
                    ytdHedef += rows.Where(x => x.Ay == ay && x.SatisTipi == "Toplam").Sum(x => x.HedefTutar) * oran;
                    ytdYS    += rows.Where(x => x.Ay == ay && x.SatisTipi == "YeniSatis").Sum(x => x.HedefTutar) * oran;
                    ytdYen   += rows.Where(x => x.Ay == ay && x.SatisTipi == "Yenileme").Sum(x => x.HedefTutar) * oran;
                }
                if (ytdHedef <= 0 && (ytdYS + ytdYen) > 0) ytdHedef = ytdYS + ytdYen;

                // Gerçekleşen — ürün × ay matrisinden tek pass
                decimal ytdGerc = 0m, ytdGercYS = 0m, ytdGercYen = 0m;
                for (byte ay = 1; ay <= ytdEndMonth; ay++)
                {
                    if (urunMatris.TryGetValue((u.Id, ay), out var v))
                    {
                        ytdGerc += v.toplam;
                        ytdGercYS += v.ys;
                        ytdGercYen += v.yen;
                    }
                }

                // Tahmini kapanış: Şirket Özeti ile aynı formül — gün = bugüne kadar geçen takvim günü.
                var gun = (nowDate - ytdStart).Days + 1;
                var runRateYSRawU = gun > 0 ? ytdGercYS * (365m / gun) : 0m;
                var runRateYenRawU = gun > 0 ? ytdGercYen * (365m / gun) : 0m;
                var runRateYenU = yillikYen > 0 ? Math.Min(runRateYenRawU, yillikYen) : runRateYenRawU;
                var runRate = runRateYSRawU + runRateYenU;
                var attTimeAdj = ytdHedef > 0 ? ytdGerc / ytdHedef * 100m : 0m;
                var attRaw = yillikToplam > 0 ? ytdGerc / yillikToplam * 100m : 0m;

                sonuc.Add(new HedefUrunSatirViewModel
                {
                    UrunId = u.Id,
                    UrunAd = u.Ad,
                    SiraNo = u.SiraNo,
                    YillikHedef = yillikToplam,
                    YillikHedefYS = yillikYS,
                    YillikHedefYen = yillikYen,
                    YtdHedef = ytdHedef,
                    YtdHedefYS = ytdYS,
                    YtdHedefYen = ytdYen,
                    YtdGerceklesen = ytdGerc,
                    RunRate = runRate,
                    AttainmentTimeAdj = attTimeAdj,
                    Attainment = attRaw,
                    GerceklesenYS = ytdGercYS,
                    GerceklesenYen = ytdGercYen,
                    RenkSinifi = ResolveRenk(attTimeAdj)
                });
            }

            // Sıralama: yıllık hedefe göre büyükten küçüğe
            return sonuc.OrderByDescending(x => x.YillikHedef).ToList();
        });
    }

    public async Task<HedefUrunDetayViewModel?> GetUrunDetayAsync(int yil, int urunId)
    {
        var key = $"hedef_urun_detay_{yil}_{urunId}";
        return await CachedAsync(key, async () =>
        {
            using var db = _contextFactory.CreateDbContext();
            var u = await db.TBLSOS_HEDEF_URUNs.FirstOrDefaultAsync(x => x.Id == urunId);
            if (u == null) return null;

            var yillik = await db.TBLSOS_HEDEF_URUN_YILLIKs
                .FirstOrDefaultAsync(x => x.SenaryoId == SENARYO_ID && x.UrunId == urunId);
            var aylikTumTipler = await db.TBLSOS_HEDEF_URUN_AYLIKs
                .Where(x => x.SenaryoId == SENARYO_ID && x.UrunId == urunId)
                .ToListAsync();
            var aylik = aylikTumTipler.Where(x => x.SatisTipi == "Toplam").OrderBy(x => x.Ay).ToList();

            var ytdHedef = ComputeYtdHedef(aylik, yil);

            // Ay × YS/Yen kırılımı (yan panelde "Seçili Dönem · YS/Yen" için)
            var ayYS  = aylikTumTipler.Where(x => x.SatisTipi == "YeniSatis").GroupBy(x => x.Ay).ToDictionary(g => g.Key, g => g.Sum(x => x.HedefTutar));
            var ayYen = aylikTumTipler.Where(x => x.SatisTipi == "Yenileme").GroupBy(x => x.Ay).ToDictionary(g => g.Key, g => g.Sum(x => x.HedefTutar));

            // Bu ürün için temsilci dağılımı
            var temsilciler = await db.TBLSOS_HEDEF_TEMSILCIs.Where(t => t.Aktif).OrderBy(t => t.SiraNo).ToListAsync();
            var temsilciAylik = await db.TBLSOS_HEDEF_TEMSILCI_AYLIKs
                .Where(x => x.SenaryoId == SENARYO_ID && x.UrunId == urunId)
                .ToListAsync();

            var dagilim = new List<HedefTemsilciSatirViewModel>();
            foreach (var t in temsilciler)
            {
                var trows = temsilciAylik.Where(x => x.TemsilciId == t.Id).ToList();
                var ys = trows.Where(x => x.SatisTipi == "YeniSatis").Sum(x => x.HedefTutar);
                var yen = trows.Where(x => x.SatisTipi == "Yenileme").Sum(x => x.HedefTutar);
                var top = ys + yen;
                if (top == 0) continue;
                dagilim.Add(new HedefTemsilciSatirViewModel
                {
                    TemsilciId = t.Id,
                    Ad = t.Ad,
                    Kanal = t.Kanal,
                    YillikHedef = top,
                    HedefYS = ys,
                    HedefYen = yen
                });
            }

            // Ürün × ay × YS/Yen gerçekleşen — Cockpit fatura listesi (EfektifTarih + İade/Ret hariç) + kalem dağıtımı
            var (urunMatrisAll, _) = await GetGerceklesenUrunMatrisAsync(yil, null);
            var ayGercMap = new Dictionary<int, (decimal top, decimal ys, decimal yen)>();
            foreach (var kv in urunMatrisAll)
            {
                if (kv.Key.urunId == urunId)
                    ayGercMap[(int)kv.Key.ay] = kv.Value;
            }

            var now = DateTime.Now;
            decimal ytdGerc = 0, yilGercToplam = 0, yilGercYS = 0, yilGercYen = 0;
            foreach (var kv in ayGercMap)
            {
                yilGercToplam += kv.Value.top;
                yilGercYS     += kv.Value.ys;
                yilGercYen    += kv.Value.yen;
                if (yil < now.Year || (yil == now.Year && kv.Key <= now.Month))
                    ytdGerc += kv.Value.top;
            }

            return new HedefUrunDetayViewModel
            {
                UrunId = urunId,
                UrunAd = u.Ad,
                YillikHedef = yillik?.HedefToplam ?? 0,
                YillikHedefYS = yillik?.HedefYeniSatis ?? 0,
                YillikHedefYen = yillik?.HedefYenileme ?? 0,
                YtdHedef = ytdHedef,
                YtdGerceklesen = ytdGerc,
                Attainment = ytdHedef > 0 ? Math.Round(ytdGerc / ytdHedef * 100m, 2) : 0,
                RenkSinifi = ResolveRenk(ytdHedef > 0 ? ytdGerc / ytdHedef * 100m : 0),
                AyToplamlari = aylik.Select(a => {
                    ayGercMap.TryGetValue(a.Ay, out var g);
                    return new HedefAyToplam {
                        Ay = a.Ay,
                        Hedef = a.HedefTutar,
                        HedefYS = ayYS.TryGetValue(a.Ay, out var ysH) ? ysH : 0,
                        HedefYen = ayYen.TryGetValue(a.Ay, out var yenH) ? yenH : 0,
                        Gerceklesen = g.top,
                        GerceklesenYS = g.ys,
                        GerceklesenYen = g.yen,
                        Attainment = a.HedefTutar > 0 ? Math.Round(g.top / a.HedefTutar * 100m, 2) : 0
                    };
                }).ToList(),
                TemsilciDagilimi = dagilim.OrderByDescending(x => x.YillikHedef).ToList()
            };
        });
    }

    /// <summary>
    /// Ürün × ay × (toplam, YS, Yen) gerçekleşen — kalem-bazlı dağıtım.
    /// Cockpit fatura kartı ürün dağılımıyla aynı algoritma:
    ///   tlTutar = (kalem.Total / toplamDoviz) * siparis.TotalNetAmount
    /// YS/Yen ayrımı: SalesDocumentTypeSap.Code == "ZZ08" → Yenileme, diğer → Yeni Satış
    /// </summary>
    private async Task<Dictionary<int, Dictionary<int, (decimal top, decimal ys, decimal yen)>>>
        GetUrunGerceklesenByAyAsync(int yil)
    {
        var key = $"hedef_urun_gerc_ay_{yil}";
        return await CachedAsync(key, async () =>
        {
            using var db = _contextFactory.CreateDbContext();
            db.Database.SetCommandTimeout(120);
            var start = new DateTime(yil, 1, 1);
            var end = new DateTime(yil, 12, 31);

            // 1) StockCode → AnaUrunId
            var eslestirmeler = (await db.TBLSOS_URUN_ESLESTIRMEs.AsNoTracking()
                    .Where(e => e.StokKodu != null)
                    .Select(e => new { e.StokKodu, e.AnaUrunId })
                    .ToListAsync())
                .GroupBy(e => e.StokKodu!)
                .ToDictionary(g => g.Key, g => g.First().AnaUrunId);

            // 2) Closed siparişler dönemde (DocType code ile join)
            var siparisler = await (
                from s in db.TBL_VARUNA_SIPARIs.AsNoTracking()
                join dt in db.TBL_VARUNA_SALESDOCUMENTTYPESAPs on s.SalesDocumentTypeSapId equals dt.Id into dtj
                from dt in dtj.DefaultIfEmpty()
                where s.OrderStatus == "Closed"
                      && s.TotalNetAmount > 0
                      && s.OrderId != null
                      && s.DeletedOn == null
                      && s.InvoiceDate >= start && s.InvoiceDate <= end
                select new {
                    s.OrderId,
                    s.TotalNetAmount,
                    s.InvoiceDate,
                    DocCode = dt.Code
                }).ToListAsync();

            if (siparisler.Count == 0)
                return new Dictionary<int, Dictionary<int, (decimal, decimal, decimal)>>();

            // 3) Kalemler — siparişlerin OrderId'sine göre, CrmOrderId+StockCode dedupe
            var orderIds = siparisler.Select(s => s.OrderId!).ToHashSet();
            var kalemler = (await db.TBL_VARUNA_SIPARIS_URUNLERIs.AsNoTracking()
                    .Where(u => u.CrmOrderId != null && u.StockCode != null && orderIds.Contains(u.CrmOrderId))
                    .Select(u => new { u.CrmOrderId, u.StockCode, u.Total })
                    .ToListAsync())
                .GroupBy(u => new { u.CrmOrderId, u.StockCode })
                .Select(g => new {
                    CrmOrderId = g.Key.CrmOrderId!,
                    StockCode = g.Key.StockCode!,
                    Total = g.Sum(x => x.Total ?? 0m)
                })
                .GroupBy(u => u.CrmOrderId)
                .ToDictionary(g => g.Key, g => g.ToList());

            // 4) Hesap
            var result = new Dictionary<int, Dictionary<int, (decimal top, decimal ys, decimal yen)>>();
            foreach (var s in siparisler)
            {
                if (!kalemler.TryGetValue(s.OrderId!, out var kls)) continue;
                var toplamDoviz = kls.Sum(k => k.Total);
                if (toplamDoviz <= 0) continue;
                if (!s.InvoiceDate.HasValue) continue;

                var ay = s.InvoiceDate.Value.Month;
                var isYen = string.Equals(s.DocCode, "ZZ08", StringComparison.OrdinalIgnoreCase);

                foreach (var k in kls)
                {
                    if (!eslestirmeler.TryGetValue(k.StockCode, out var urunId)) continue;
                    var tl = k.Total / toplamDoviz * (s.TotalNetAmount ?? 0m);
                    if (tl <= 0) continue;

                    if (!result.TryGetValue(urunId, out var ayMap))
                    {
                        ayMap = new Dictionary<int, (decimal, decimal, decimal)>();
                        result[urunId] = ayMap;
                    }
                    ayMap.TryGetValue(ay, out var cur);
                    if (isYen) ayMap[ay] = (cur.Item1 + tl, cur.Item2,        cur.Item3 + tl);
                    else       ayMap[ay] = (cur.Item1 + tl, cur.Item2 + tl,   cur.Item3);
                }
            }
            return result;
        });
    }

    public async Task<HedefUrunDetayViewModel?> GetUrunDetayByAdAsync(int yil, string urunAd)
    {
        using var db = _contextFactory.CreateDbContext();
        // Fuzzy match: Excel'de ürün adları aynen değil ("CallDesk"→"ServiceCore", "Unidox"→"E-Dönüşüm" alias)
        var alias = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CallDesk"] = "ServiceCore",
            ["Unidox"] = "E-Dönüşüm",
            ["BFH"] = "BFG"
        };
        var lookupAd = alias.TryGetValue(urunAd ?? "", out var x) ? x : urunAd;
        var u = await db.TBLSOS_HEDEF_URUNs.FirstOrDefaultAsync(z => z.Ad == lookupAd);
        if (u == null) return null;
        return await GetUrunDetayAsync(yil, u.Id);
    }

    public async Task<List<TBLSOS_HEDEF_TEMSILCI>> GetTemsilcilerAsync()
    {
        return await CachedAsync("hedef_temsilciler", async () =>
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.TBLSOS_HEDEF_TEMSILCIs.Where(t => t.Aktif).OrderBy(t => t.SiraNo).ToListAsync();
        });
    }

    public void InvalidateAll()
    {
        lock (_keys)
        {
            foreach (var k in _keys.ToList()) _cache.Remove(k);
            _keys.Clear();
        }
        _logger.LogInformation("HedefService cache invalidated");
    }

    // ── Tab 2 alt panel: Temsilci × Ürün × Yıllık (Toplam/YS/Yen) — tek sorgu, in-memory aggregate ──
    public async Task<HedefTemsilciUrunMatrisViewModel> GetTemsilciUrunMatrisAsync(int yil)
    {
        var key = $"hedef_temsilci_urun_matris_{yil}";
        return await CachedAsync(key, async () =>
        {
            // 2 paralel sorgu: temsilciler + tüm aylık satırlar (Include Urun)
            var temsilcilerTask = Task.Run(async () =>
            {
                using var db1 = _contextFactory.CreateDbContext();
                return await db1.TBLSOS_HEDEF_TEMSILCIs.AsNoTracking()
                    .Where(t => t.Aktif).OrderBy(t => t.SiraNo).ToListAsync();
            });

            var aylikTask = Task.Run(async () =>
            {
                using var db2 = _contextFactory.CreateDbContext();
                return await db2.TBLSOS_HEDEF_TEMSILCI_AYLIKs.AsNoTracking()
                    .Where(x => x.SenaryoId == SENARYO_ID)
                    .Include(x => x.Urun)
                    .Select(x => new { x.TemsilciId, UrunAd = x.Urun!.Ad, UrunSira = x.Urun.SiraNo, x.SatisTipi, x.HedefTutar })
                    .ToListAsync();
            });

            await Task.WhenAll(temsilcilerTask, aylikTask);
            var temsilciler = temsilcilerTask.Result;
            var aylik = aylikTask.Result;

            var urunSirasi = aylik
                .GroupBy(x => new { x.UrunAd, x.UrunSira })
                .OrderBy(g => g.Key.UrunSira).ThenBy(g => g.Key.UrunAd)
                .Select(g => g.Key.UrunAd)
                .ToList();

            // Aggregate: (TemsilciId, UrunAd) → Toplam/YS/Yen yıllık
            // NOT: TBLSOS_HEDEF_TEMSILCI_AYLIK'ta "Toplam" SatisTipi yok (sadece YS + Yen).
            // Toplam = YS + Yen olarak hesaplanır.
            var pivot = aylik
                .GroupBy(x => new { x.TemsilciId, x.UrunAd })
                .ToDictionary(
                    g => (g.Key.TemsilciId, g.Key.UrunAd),
                    g =>
                    {
                        var ys  = g.Where(x => x.SatisTipi == "YeniSatis").Sum(x => x.HedefTutar);
                        var yen = g.Where(x => x.SatisTipi == "Yenileme").Sum(x => x.HedefTutar);
                        return new { Toplam = ys + yen, YS = ys, Yen = yen };
                    });

            var satirlar = new List<HedefTemsilciUrunMatrisRow>();
            foreach (var t in temsilciler)
            {
                var row = new HedefTemsilciUrunMatrisRow
                {
                    TemsilciId = t.Id,
                    TemsilciAd = t.Ad,
                    Kanal = t.Kanal
                };
                foreach (var u in urunSirasi)
                {
                    if (pivot.TryGetValue((t.Id, u), out var v))
                    {
                        row.UrunToplam[u] = v.Toplam;
                        row.UrunYS[u]     = v.YS;
                        row.UrunYen[u]    = v.Yen;
                        row.SatirToplam += v.Toplam;
                        row.SatirYS     += v.YS;
                        row.SatirYen    += v.Yen;
                    }
                    else
                    {
                        row.UrunToplam[u] = 0m;
                        row.UrunYS[u]     = 0m;
                        row.UrunYen[u]    = 0m;
                    }
                }
                satirlar.Add(row);
            }

            // Sütun (ürün) toplamları + grand
            var urunTop = new Dictionary<string, decimal>();
            var urunYS  = new Dictionary<string, decimal>();
            var urunYen = new Dictionary<string, decimal>();
            foreach (var u in urunSirasi)
            {
                urunTop[u] = satirlar.Sum(r => r.UrunToplam.GetValueOrDefault(u, 0m));
                urunYS[u]  = satirlar.Sum(r => r.UrunYS.GetValueOrDefault(u, 0m));
                urunYen[u] = satirlar.Sum(r => r.UrunYen.GetValueOrDefault(u, 0m));
            }

            return new HedefTemsilciUrunMatrisViewModel
            {
                Satirlar = satirlar,
                UrunSirasi = urunSirasi,
                UrunToplamTop = urunTop,
                UrunToplamYS = urunYS,
                UrunToplamYen = urunYen,
                GrandToplam = satirlar.Sum(r => r.SatirToplam),
                GrandYS = satirlar.Sum(r => r.SatirYS),
                GrandYen = satirlar.Sum(r => r.SatirYen)
            };
        });
    }

    // ── Şirket geneli aylık hedef (Cockpit + FA tek kaynak) ──

    public async Task<Dictionary<int, decimal>> GetGenelAylikSozlukAsync(int yil)
    {
        var key = $"hedef_genel_aylik_{yil}";
        return await CachedAsync(key, async () =>
        {
            using var db = _contextFactory.CreateDbContext();

            // 1) Birincil kaynak: TBLSOS_HEDEF_URUN_AYLIK
            var aylik = await db.TBLSOS_HEDEF_URUN_AYLIKs
                .AsNoTracking()
                .Where(x => x.SenaryoId == SENARYO_ID)
                .Select(x => new { x.Ay, x.SatisTipi, x.HedefTutar })
                .ToListAsync();

            var result = new Dictionary<int, decimal>();

            if (aylik.Count > 0)
            {
                var byAy = aylik.GroupBy(x => (int)x.Ay);
                foreach (var g in byAy)
                {
                    var toplam = g.Where(x => x.SatisTipi == "Toplam").Sum(x => x.HedefTutar);
                    if (toplam == 0)
                        toplam = g.Where(x => x.SatisTipi == "YeniSatis" || x.SatisTipi == "Yenileme")
                                  .Sum(x => x.HedefTutar);
                    if (toplam > 0) result[g.Key] = toplam;
                }
            }

            // 2) Fallback: yeni tabloda kayıt yoksa eski TBLSOS_HEDEF_AYLIK
            if (result.Count == 0)
            {
                var legacy = await db.TBLSOS_HEDEF_AYLIKs
                    .AsNoTracking()
                    .Where(h => h.Yil == yil && h.Tip == "GENEL" && h.Aktif)
                    .ToListAsync();
                foreach (var h in legacy)
                    if (h.HedefTutar > 0) result[h.Ay] = h.HedefTutar;
            }

            // Eksik aylar için 0 ile doldur — Cockpit'in GetValueOrDefault davranışı zaten bunu çözüyor,
            // ama dictionary'i deterministik tut.
            for (int ay = 1; ay <= 12; ay++)
                if (!result.ContainsKey(ay)) result[ay] = 0m;

            return result;
        });
    }

    public async Task<decimal> GetGenelHedefRangeAsync(int yil, DateTime start, DateTime end)
    {
        // Aralık aylarını tam-ay olarak topla — Cockpit/FA mevcut davranışıyla aynı.
        // (Karne'deki gün-orantılı hesap yalnızca BANT/şirket-özeti içinde, ayrı bir helper.)
        var aylik = await GetGenelAylikSozlukAsync(yil);
        var startKey = start.Year * 100 + start.Month;
        var endKey   = end.Year * 100 + end.Month;
        decimal toplam = 0m;
        for (int ay = 1; ay <= 12; ay++)
        {
            var key = yil * 100 + ay;
            if (key >= startKey && key <= endKey)
                toplam += aylik.GetValueOrDefault(ay, 0m);
        }
        return toplam;
    }

    // ─────────────────────────── Helper'lar ───────────────────────────

    /// <summary>
    /// Tarih aralığı için hedef toplamını hesaplar (gün-orantılı).
    /// temsilciId NULL → şirket geneli (URUN_AYLIK)
    /// temsilciId dolu → o temsilci (TEMSILCI_AYLIK)
    /// </summary>
    private async Task<(decimal toplam, decimal ys, decimal yen)> ComputeHedefForRangeAsync(int yil, DateTime start, DateTime end, int? temsilciId)
    {
        using var db = _contextFactory.CreateDbContext();
        decimal ys = 0, yen = 0;

        // Aylık hedefleri al
        if (temsilciId.HasValue)
        {
            var aylik = await db.TBLSOS_HEDEF_TEMSILCI_AYLIKs
                .Where(x => x.SenaryoId == SENARYO_ID && x.TemsilciId == temsilciId.Value)
                .Select(x => new { x.Ay, x.SatisTipi, x.HedefTutar }).ToListAsync();

            for (byte ay = 1; ay <= 12; ay++)
            {
                var oran = AyOrani(yil, ay, start, end);
                if (oran <= 0) continue;
                ys += aylik.Where(x => x.Ay == ay && x.SatisTipi == "YeniSatis").Sum(x => x.HedefTutar) * oran;
                yen += aylik.Where(x => x.Ay == ay && x.SatisTipi == "Yenileme").Sum(x => x.HedefTutar) * oran;
            }
        }
        else
        {
            var aylik = await db.TBLSOS_HEDEF_URUN_AYLIKs
                .Where(x => x.SenaryoId == SENARYO_ID && (x.SatisTipi == "YeniSatis" || x.SatisTipi == "Yenileme"))
                .Select(x => new { x.Ay, x.SatisTipi, x.HedefTutar }).ToListAsync();

            for (byte ay = 1; ay <= 12; ay++)
            {
                var oran = AyOrani(yil, ay, start, end);
                if (oran <= 0) continue;
                ys += aylik.Where(x => x.Ay == ay && x.SatisTipi == "YeniSatis").Sum(x => x.HedefTutar) * oran;
                yen += aylik.Where(x => x.Ay == ay && x.SatisTipi == "Yenileme").Sum(x => x.HedefTutar) * oran;
            }
        }
        return (ys + yen, ys, yen);
    }

    /// <summary>
    /// Verilen aydaki gün sayısının [start..end] aralığına düşen oranı (0..1)
    /// </summary>
    private static decimal AyOrani(int yil, byte ay, DateTime start, DateTime end)
    {
        var ayBaslangic = new DateTime(yil, ay, 1);
        var ayBitis = ayBaslangic.AddMonths(1).AddDays(-1);
        if (end < ayBaslangic || start > ayBitis) return 0m;
        var kesisimBas = start > ayBaslangic ? start : ayBaslangic;
        var kesisimBit = end < ayBitis ? end : ayBitis;
        var ayGun = (decimal)(ayBitis - ayBaslangic).Days + 1m;
        var kesisimGun = (decimal)(kesisimBit - kesisimBas).Days + 1m;
        return Math.Max(0m, Math.Min(1m, kesisimGun / ayGun));
    }

    /// <summary>
    /// YTD hedefi: yıl başı → bulunduğumuz ayın SONU (TAM AY).
    /// Kanonik tanım: Mayıs ay-içi prorate YOK; içinde olduğumuz ay tam sayılır.
    /// Temsilci/Ürün listesi (AyOrani+ytdEnd=ay sonu) ile birebir hizalı.
    /// </summary>
    private decimal ComputeYtdHedef(List<TBLSOS_HEDEF_URUN_AYLIK> aylik, int yil)
    {
        var bugun = DateTime.Now.Date;
        if (bugun.Year != yil) return aylik.Sum(x => x.HedefTutar);
        var ytdEndMonth = (byte)bugun.Month;
        return aylik.Where(x => x.Ay <= ytdEndMonth).Sum(x => x.HedefTutar);
    }

    /// <summary>
    /// Faturaları sipariş üzerinden YS/Yenileme'ye ayırır.
    /// SalesDocumentTypeSapId → Code = ZZ08 → Yenileme
    /// </summary>
    private async Task<(decimal toplam, decimal ys, decimal yen)> SplitYsYenilemeAsync(List<FaturaRow> faturalar)
    {
        if (faturalar.Count == 0) return (0m, 0m, 0m);
        var faturaNos = faturalar.Where(f => !string.IsNullOrEmpty(f.FaturaNo)).Select(f => f.FaturaNo).Distinct().ToList();
        if (faturaNos.Count == 0) return (faturalar.Sum(f => f.NetTutar), faturalar.Sum(f => f.NetTutar), 0m);

        using var db = _contextFactory.CreateDbContext();
        // Yenileme = SalesDocumentTypeSapId üzerinden ZZ08 koduna eşleşen faturalar
        var yenilemeFaturaNos = await (
            from s in db.TBL_VARUNA_SIPARIs
            join dt in db.TBL_VARUNA_SALESDOCUMENTTYPESAPs on s.SalesDocumentTypeSapId equals dt.Id
            where dt.Code == "ZZ08" && faturaNos.Contains(s.SerialNumber!) && s.DeletedOn == null
            select s.SerialNumber!).Distinct().ToListAsync();

        var setYen = new HashSet<string>(yenilemeFaturaNos, StringComparer.OrdinalIgnoreCase);
        decimal toplam = faturalar.Sum(f => f.NetTutar);
        decimal yen = faturalar.Where(f => f.FaturaNo != null && setYen.Contains(f.FaturaNo)).Sum(f => f.NetTutar);
        decimal ys = toplam - yen;
        return (toplam, ys, yen);
    }

    /// <summary>
    /// TemsilciId → CrmPersonId çözümleme.
    /// NOT: Sipariş tablosunda ProposalOwnerId çoğunlukla NULL — bu yüzden owner filtresini SP'ye iletmek
    /// veri kaybına yol açar. Bunun yerine GetGerceklesenAsync() account-bazlı eşleşme kullanır.
    /// Bu metod sadece şirket-geneli sorgular için gerekli (null dönüş = filtresiz).
    /// </summary>
    private async Task<string?> ResolveOwnerIdAsync(int? temsilciId)
    {
        if (!temsilciId.HasValue) return null;
        using var db = _contextFactory.CreateDbContext();
        return (await db.TBLSOS_HEDEF_TEMSILCIs.FirstOrDefaultAsync(x => x.Id == temsilciId.Value))?.CrmPersonId;
    }

    /// <summary>
    /// Cockpit fatura listesi (otorite) + VARUNA meta (DocCode/Account/OrderId) zenginleştirme.
    /// İade/Ret/İptal otomatik hariç (Cockpit SP_COCKPIT_FATURA çıktısı zaten temiz).
    /// Tarih = EfektifFaturaTarihi (tahakkuk override). Sentetik faturalar dahil.
    /// </summary>
    private class GercFaturaSatir
    {
        public string FaturaNo { get; set; } = "";
        public DateTime EfektifTarih { get; set; }
        public decimal NetTutar { get; set; }
        public string? OrderId { get; set; }
        public string? AccountId { get; set; }
        public string? DocCode { get; set; }
    }

    private async Task<List<GercFaturaSatir>> ResolveGercSatirlarAsync(DateTime start, DateTime end, int? temsilciId)
    {
        var cockpitFat = await _cockpit.GetFaturalarAsync(start, end);
        if (cockpitFat.Count == 0) return new List<GercFaturaSatir>();

        using var db = _contextFactory.CreateDbContext();
        db.Database.SetCommandTimeout(60);

        // Temsilci → AccountId set (varsa). Kanonik kaynak: TBL_VARUNA_ACCOUNT_REPRESENTATIVES (State=Active).
        // CrmPersonId varsa ID-bazlı eşleşme önce; yoksa Person.PersonNameSurname ↔ Hedef.Ad ad-bazlı.
        HashSet<string>? temsilciAccs = null;
        if (temsilciId.HasValue)
        {
            var temsilci = await db.TBLSOS_HEDEF_TEMSILCIs.AsNoTracking()
                .Where(x => x.Id == temsilciId.Value)
                .Select(x => new { x.Ad, x.CrmPersonId })
                .FirstOrDefaultAsync();
            if (temsilci == null || string.IsNullOrEmpty(temsilci.Ad)) return new List<GercFaturaSatir>();

            var reps = await db.TBL_VARUNA_ACCOUNT_REPRESENTATIVESs.AsNoTracking()
                .Where(r => r.AccountId.HasValue && r.AccountOwnerId.HasValue && r.State == "Active")
                .Select(r => new { r.AccountId, r.AccountOwnerId })
                .ToListAsync();

            // Hedef temsilcinin sahip olduğu PersonNameSurname (ad fallback için)
            HashSet<string>? matchOwnerIds = null;
            if (!string.IsNullOrWhiteSpace(temsilci.CrmPersonId))
            {
                matchOwnerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { temsilci.CrmPersonId.Trim() };
            }
            else
            {
                var personIds = await db.TBL_VARUNA_PERSONs.AsNoTracking()
                    .Where(p => p.PersonNameSurname == temsilci.Ad && p.DeletedOn == null)
                    .Select(p => p.Id)
                    .ToListAsync();
                matchOwnerIds = personIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            var accs = reps
                .Where(r => matchOwnerIds.Contains(r.AccountOwnerId!.Value.ToString()))
                .Select(r => r.AccountId!.Value.ToString().Trim())
                .ToList();
            temsilciAccs = accs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var faturaNos = cockpitFat
            .Where(f => !string.IsNullOrEmpty(f.FaturaNo))
            .Select(f => f.FaturaNo)
            .Distinct()
            .ToList();

        // VARUNA meta: SerialNumber → DocCode + AccountId + OrderId
        var meta = await (
            from s in db.TBL_VARUNA_SIPARIs
            join dt in db.TBL_VARUNA_SALESDOCUMENTTYPESAPs on s.SalesDocumentTypeSapId equals dt.Id into dtj
            from dt in dtj.DefaultIfEmpty()
            where faturaNos.Contains(s.SerialNumber!) && s.DeletedOn == null
            select new { SerialNumber = s.SerialNumber!, s.OrderId, s.AccountId, DocCode = dt.Code }
        ).ToListAsync();

        var metaMap = meta
            .GroupBy(m => m.SerialNumber)
            .ToDictionary(g => g.Key, g => g.First());

        var result = new List<GercFaturaSatir>(cockpitFat.Count);
        foreach (var f in cockpitFat)
        {
            if (string.IsNullOrEmpty(f.FaturaNo)) continue;
            metaMap.TryGetValue(f.FaturaNo, out var m);

            if (temsilciAccs != null)
            {
                // Temsilci filtresi: VARUNA'da yoksa veya AccountId set'te yoksa hariç
                if (m == null || string.IsNullOrEmpty(m.AccountId) || !temsilciAccs.Contains(m.AccountId.Trim()))
                    continue;
            }

            result.Add(new GercFaturaSatir
            {
                FaturaNo = f.FaturaNo,
                EfektifTarih = f.EfektifTarih,
                NetTutar = f.NetTutar,
                OrderId = m?.OrderId,
                AccountId = m?.AccountId,
                DocCode = m?.DocCode
            });
        }
        return result;
    }

    /// <summary>
    /// Ürün × Ay × (Toplam, YS, Yen) gerçekleşen matrisi.
    /// SP_HEDEF_URUN_AY_MATRIS server-side join + grup yapar (tek round-trip).
    /// temsilciId == null durumunda SP'den gelir (~200ms).
    /// temsilciId != null durumunda eski LINQ pattern kullanılır (nadir kullanım).
    /// </summary>
    private class UrunAyMatrisRow
    {
        public int UrunId { get; set; }
        public byte Ay { get; set; }
        public decimal Toplam { get; set; }
        public decimal YeniSatis { get; set; }
        public decimal Yenileme { get; set; }
    }

    private async Task<(Dictionary<(int urunId, byte ay), (decimal toplam, decimal ys, decimal yen)> matris,
                       (decimal toplam, decimal ys, decimal yen) diger)>
        GetGerceklesenUrunMatrisAsync(int yil, int? temsilciId)
    {
        var key = $"hedef_gerc_urunay_{yil}_{temsilciId?.ToString() ?? "all"}";
        return await CachedAsync(key, async () =>
        {
            // SP yolu — sadece tüm şirket (temsilci filtresi yok) için
            if (!temsilciId.HasValue)
            {
                using var db0 = _contextFactory.CreateDbContext();
                db0.Database.SetCommandTimeout(60);
                var spRows = await db0.Database
                    .SqlQueryRaw<UrunAyMatrisRow>("EXEC SP_HEDEF_URUN_AY_MATRIS @p0", yil)
                    .ToListAsync();

                var sp_matris = new Dictionary<(int, byte), (decimal, decimal, decimal)>();
                foreach (var r in spRows)
                    sp_matris[(r.UrunId, r.Ay)] = (r.Toplam, r.YeniSatis, r.Yenileme);
                return (sp_matris, (0m, 0m, 0m));
            }

            // LINQ yolu — temsilci filtresi var (eski path)
            var start = new DateTime(yil, 1, 1);
            var end = new DateTime(yil, 12, 31, 23, 59, 59);

            var rows = await ResolveGercSatirlarAsync(start, end, temsilciId);
            if (rows.Count == 0)
                return (new Dictionary<(int, byte), (decimal, decimal, decimal)>(), (0m, 0m, 0m));

            using var db = _contextFactory.CreateDbContext();
            db.Database.SetCommandTimeout(60);

            var orderIds = rows
                .Where(r => !string.IsNullOrEmpty(r.OrderId))
                .Select(r => r.OrderId!)
                .Distinct()
                .ToList();

            var kalemler = orderIds.Count == 0
                ? new List<(string OrderId, string? StockCode, decimal Total)>()
                : await (
                    from k in db.TBL_VARUNA_SIPARIS_URUNLERIs
                    where k.CrmOrderId != null && orderIds.Contains(k.CrmOrderId)
                    select new { OrderId = k.CrmOrderId!, k.StockCode, Total = k.Total ?? 0m }
                ).ToListAsync()
                  .ContinueWith(t => t.Result.Select(x => (x.OrderId, x.StockCode, x.Total)).ToList());

            var kalemByOrder = kalemler
                .GroupBy(k => k.OrderId)
                .ToDictionary(g => g.Key, g => g.ToList());

            // ESLESTIRME.AnaUrunId → ANA_URUN.Ad → HEDEF_URUN.Id bridge (panel UrunId namespace)
            var eslesme = (await (
                from e in db.TBLSOS_URUN_ESLESTIRMEs.AsNoTracking()
                join au in db.TBLSOS_ANA_URUNs on e.AnaUrunId equals au.Id
                join hu in db.TBLSOS_HEDEF_URUNs on au.Ad.Trim() equals hu.Ad.Trim()
                select new { e.StokKodu, HedefUrunId = hu.Id }
            ).ToListAsync())
                .GroupBy(x => x.StokKodu)
                .ToDictionary(g => g.Key, g => g.First().HedefUrunId, StringComparer.OrdinalIgnoreCase);

            var matris = new Dictionary<(int, byte), (decimal, decimal, decimal)>();
            decimal digerTop = 0m, digerYS = 0m, digerYen = 0m;

            foreach (var f in rows)
            {
                byte ay = (byte)f.EfektifTarih.Month;
                bool isYen = f.DocCode == "ZZ08";

                List<(string OrderId, string? StockCode, decimal Total)>? fKalemler = null;
                if (!string.IsNullOrEmpty(f.OrderId))
                    kalemByOrder.TryGetValue(f.OrderId, out fKalemler);

                if (fKalemler == null || fKalemler.Count == 0)
                {
                    digerTop += f.NetTutar;
                    if (isYen) digerYen += f.NetTutar; else digerYS += f.NetTutar;
                    continue;
                }

                decimal toplamDoviz = fKalemler.Sum(k => k.Total);
                if (toplamDoviz <= 0m)
                {
                    digerTop += f.NetTutar;
                    if (isYen) digerYen += f.NetTutar; else digerYS += f.NetTutar;
                    continue;
                }

                foreach (var k in fKalemler)
                {
                    decimal kalemTL = k.Total / toplamDoviz * f.NetTutar;
                    if (!string.IsNullOrEmpty(k.StockCode) && eslesme.TryGetValue(k.StockCode, out var urunId))
                    {
                        var mk = (urunId, ay);
                        var cur = matris.TryGetValue(mk, out var c) ? c : (0m, 0m, 0m);
                        matris[mk] = (cur.Item1 + kalemTL,
                                       cur.Item2 + (isYen ? 0m : kalemTL),
                                       cur.Item3 + (isYen ? kalemTL : 0m));
                    }
                    else
                    {
                        digerTop += kalemTL;
                        if (isYen) digerYen += kalemTL; else digerYS += kalemTL;
                    }
                }
            }

            return (matris, (digerTop, digerYS, digerYen));
        });
    }

    /// <summary>
    /// Satış temsilcisi × Ay × (Toplam, YS, Yen) gerçekleşen matrisi — SP TEK PASS.
    /// SP_HEDEF_TEMSILCI_AY_MATRIS server-side join: AccountId → ACCOUNT_REPRESENTATIVES → TemsilciId.
    /// 9 temsilci için 9× round-trip yerine 1× round-trip (~30× hız kazancı).
    /// </summary>
    private class TemsilciAyMatrisRow
    {
        public int TemsilciId { get; set; }
        public byte Ay { get; set; }
        public decimal Toplam { get; set; }
        public decimal YeniSatis { get; set; }
        public decimal Yenileme { get; set; }
    }

    private async Task<Dictionary<(int temsilciId, byte ay), (decimal toplam, decimal ys, decimal yen)>>
        GetGerceklesenTemsilciAyMatrisAsync(int yil)
    {
        var key = $"hedef_gerc_temsilciay_{yil}";
        return await CachedAsync(key, async () =>
        {
            using var dbsp = _contextFactory.CreateDbContext();
            dbsp.Database.SetCommandTimeout(60);
            var rows = await dbsp.Database
                .SqlQueryRaw<TemsilciAyMatrisRow>("EXEC SP_HEDEF_TEMSILCI_AY_MATRIS @p0", yil)
                .ToListAsync();
            var spResult = new Dictionary<(int, byte), (decimal, decimal, decimal)>();
            foreach (var r in rows)
                spResult[(r.TemsilciId, r.Ay)] = (r.Toplam, r.YeniSatis, r.Yenileme);
            return spResult;
        });
    }

    /// <summary>
    /// (Eski LINQ yolu — fallback olarak korunuyor, şu an kullanılmıyor)
    /// </summary>
    private async Task<Dictionary<(int temsilciId, byte ay), (decimal toplam, decimal ys, decimal yen)>>
        GetGerceklesenTemsilciAyMatrisLinqAsync(int yil)
    {
        var key = $"hedef_gerc_temsilciay_linq_{yil}";
        return await CachedAsync(key, async () =>
        {
            var start = new DateTime(yil, 1, 1);
            var end = new DateTime(yil, 12, 31, 23, 59, 59);

            var cockpitFat = await _cockpit.GetFaturalarAsync(start, end);
            if (cockpitFat.Count == 0)
                return new Dictionary<(int, byte), (decimal, decimal, decimal)>();

            using var db = _contextFactory.CreateDbContext();
            db.Database.SetCommandTimeout(60);

            // Hedef temsilci listesi (Ad → Id ve CrmPersonId → Id)
            var temsilciler = await db.TBLSOS_HEDEF_TEMSILCIs.AsNoTracking()
                .Select(t => new { t.Id, t.Ad, t.CrmPersonId })
                .ToListAsync();
            var adToId = temsilciler
                .GroupBy(t => t.Ad.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
            var crmIdToId = temsilciler
                .Where(t => !string.IsNullOrWhiteSpace(t.CrmPersonId))
                .GroupBy(t => t.CrmPersonId!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

            // Kanonik kaynak: ACCOUNT_REPRESENTATIVES → Person → TBLSOS_HEDEF_TEMSILCI.
            // (Person.Id string, AccountOwnerId Guid → join client-side)
            var reps = await db.TBL_VARUNA_ACCOUNT_REPRESENTATIVESs.AsNoTracking()
                .Where(r => r.AccountId.HasValue && r.AccountOwnerId.HasValue && r.State == "Active")
                .Select(r => new { r.AccountId, r.AccountOwnerId })
                .ToListAsync();
            var personMap = await db.TBL_VARUNA_PERSONs.AsNoTracking()
                .Where(p => p.PersonNameSurname != null)
                .Select(p => new { p.Id, p.PersonNameSurname })
                .ToListAsync();
            var personById = personMap
                .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().PersonNameSurname!, StringComparer.OrdinalIgnoreCase);
            var accountToTemsilciId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in reps)
            {
                var accIdKey = r.AccountId!.Value.ToString().Trim();
                if (accountToTemsilciId.ContainsKey(accIdKey)) continue;
                var ownerIdStr = r.AccountOwnerId!.Value.ToString();
                // Önce CrmPersonId üzerinden ID-bazlı eşleştir (yazım farkını yok eder),
                // bulamazsa Person.PersonNameSurname → ad-bazlı son çareye düş.
                if (crmIdToId.TryGetValue(ownerIdStr, out var tid))
                {
                    accountToTemsilciId[accIdKey] = tid;
                    continue;
                }
                if (!personById.TryGetValue(ownerIdStr, out var ad)) continue;
                if (adToId.TryGetValue(ad.Trim(), out var tid2))
                    accountToTemsilciId[accIdKey] = tid2;
            }

            // VARUNA SerialNumber → AccountId + DocCode (tek sorguda)
            var faturaNos = cockpitFat
                .Where(f => !string.IsNullOrEmpty(f.FaturaNo))
                .Select(f => f.FaturaNo)
                .Distinct()
                .ToList();
            var meta = await (
                from s in db.TBL_VARUNA_SIPARIs.AsNoTracking()
                join dt in db.TBL_VARUNA_SALESDOCUMENTTYPESAPs on s.SalesDocumentTypeSapId equals dt.Id into dtj
                from dt in dtj.DefaultIfEmpty()
                where faturaNos.Contains(s.SerialNumber!) && s.DeletedOn == null
                select new { SerialNumber = s.SerialNumber!, s.AccountId, DocCode = dt.Code }
            ).ToListAsync();
            var metaMap = meta
                .GroupBy(m => m.SerialNumber)
                .ToDictionary(g => g.Key, g => g.First());

            var result = new Dictionary<(int, byte), (decimal, decimal, decimal)>();
            foreach (var f in cockpitFat)
            {
                if (string.IsNullOrEmpty(f.FaturaNo)) continue;
                if (!metaMap.TryGetValue(f.FaturaNo, out var m)) continue;
                if (string.IsNullOrEmpty(m.AccountId)) continue;
                if (!accountToTemsilciId.TryGetValue(m.AccountId.Trim(), out var tid)) continue;

                var ay = (byte)f.EfektifTarih.Month;
                bool isYen = m.DocCode == "ZZ08";
                var k = (tid, ay);
                var cur = result.TryGetValue(k, out var c) ? c : (0m, 0m, 0m);
                result[k] = (cur.Item1 + f.NetTutar,
                              cur.Item2 + (isYen ? 0m : f.NetTutar),
                              cur.Item3 + (isYen ? f.NetTutar : 0m));
            }
            return result;
        });
    }

    /// <summary>
    /// Dönemde açık tekliflerin satış temsilcisi bazlı toplamı.
    /// Mantık: TBL_VARUNA_TEKLIF.AccountId → TBL_VARUNA_ACCOUNT_REPRESENTATIVES.AccountOwnerId → Person → TemsilciId
    /// (Hesaba bağlı satış temsilcisi — Gerçekleşen kolonu ile aynı eşleşme zinciri)
    /// Aktif teklif: DeletedOn=null + Status (Reject/Denied/Closed hariç) + CreatedOn dönemde.
    /// Tutar: TotalNetAmountLocalCurrency_Amount (KDV hariç TL).
    /// </summary>
    private async Task<Dictionary<int, decimal>> GetAcikTeklifSatisciAsync(DateTime start, DateTime end)
    {
        var key = $"hedef_acikteklif_satisci_{start:yyyyMMdd}_{end:yyyyMMdd}";
        return await CachedAsync(key, async () =>
        {
            using var db = _contextFactory.CreateDbContext();
            db.Database.SetCommandTimeout(60);

            // 1) Hedef temsilci listesi (Ad → Id ve CrmPersonId → Id)
            var temsilciler = await db.TBLSOS_HEDEF_TEMSILCIs.AsNoTracking()
                .Select(t => new { t.Id, t.Ad, t.CrmPersonId })
                .ToListAsync();
            var adToId = temsilciler
                .GroupBy(t => t.Ad.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
            var crmIdToId = temsilciler
                .Where(t => !string.IsNullOrWhiteSpace(t.CrmPersonId))
                .GroupBy(t => t.CrmPersonId!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

            // 2) Kanonik kaynak: ACCOUNT_REPRESENTATIVES → Person → TBLSOS_HEDEF_TEMSILCI.
            //    Person.Id string, AccountOwnerId Guid → join client-side.
            var accountToTemsilciId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var reps = await db.TBL_VARUNA_ACCOUNT_REPRESENTATIVESs.AsNoTracking()
                .Where(r => r.AccountId.HasValue && r.AccountOwnerId.HasValue && r.State == "Active")
                .Select(r => new { r.AccountId, r.AccountOwnerId })
                .ToListAsync();
            var personMap = await db.TBL_VARUNA_PERSONs.AsNoTracking()
                .Where(p => p.PersonNameSurname != null)
                .Select(p => new { p.Id, p.PersonNameSurname })
                .ToListAsync();
            var personById = personMap
                .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().PersonNameSurname!, StringComparer.OrdinalIgnoreCase);
            foreach (var r in reps)
            {
                var accIdKey = r.AccountId!.Value.ToString().Trim();
                if (accountToTemsilciId.ContainsKey(accIdKey)) continue;
                var ownerIdStr = r.AccountOwnerId!.Value.ToString();
                // Önce CrmPersonId üzerinden ID-bazlı eşleştir (yazım farkını yok eder),
                // bulamazsa Person.PersonNameSurname → ad-bazlı son çareye düş.
                if (crmIdToId.TryGetValue(ownerIdStr, out var tid))
                {
                    accountToTemsilciId[accIdKey] = tid;
                    continue;
                }
                if (!personById.TryGetValue(ownerIdStr, out var ad)) continue;
                if (adToId.TryGetValue(ad.Trim(), out var tid2))
                    accountToTemsilciId[accIdKey] = tid2;
            }

            // 3) Aktif teklifler — AccountId + OpportunityId + tutar (fırsata bağlı olanlar).
            //    Probability >= 70 filtresi kaldırıldı: Varuna FIRSAT_ODATA.Probability alanı
            //    pratikte hep NULL geliyor → filtre tüm teklifleri eliyordu. "Açık teklif"
            //    tanımı için Status filtresi (Reject/Denied/Closed hariç) yeterli.
            var rawRows = await db.TBL_VARUNA_TEKLIFs.AsNoTracking()
                .Where(t => t.DeletedOn == null
                         && t.CreatedOn.HasValue && t.CreatedOn >= start && t.CreatedOn <= end
                         && (t.Status == null || (t.Status != "Reject" && t.Status != "Denied" && t.Status != "Closed"))
                         && t.AccountId.HasValue
                         && t.OpportunityId.HasValue)
                .Select(t => new { t.AccountId, Tutar = t.TotalNetAmountLocalCurrency_Amount ?? 0m })
                .ToListAsync();

            var result = new Dictionary<int, decimal>();
            foreach (var r in rawRows)
            {
                if (!r.AccountId.HasValue) continue;
                var accIdStr = r.AccountId.Value.ToString();
                if (!accountToTemsilciId.TryGetValue(accIdStr, out var tid)) continue;
                result[tid] = result.TryGetValue(tid, out var c) ? c + r.Tutar : r.Tutar;
            }
            return result;
        });
    }

    /// <summary>
    /// Dönemde açık tekliflerin ürün bazlı toplamı (kalem oransal dağıtım).
    /// kalemTL = (kalem.Total_Amount / toplamDoviz) × teklif.TotalNetAmountLocalCurrency_Amount
    /// </summary>
    private async Task<(Dictionary<int, decimal> urunMap, decimal diger)>
        GetAcikTeklifUrunAsync(DateTime start, DateTime end)
    {
        var key = $"hedef_acikteklif_urun_{start:yyyyMMdd}_{end:yyyyMMdd}";
        return await CachedAsync(key, async () =>
        {
            using var db = _contextFactory.CreateDbContext();
            db.Database.SetCommandTimeout(60);

            // Aktif teklifler — fırsata bağlı olanlar (OpportunityId zorunlu).
            // Probability >= 70 filtresi kaldırıldı (FIRSAT_ODATA.Probability pratikte hep NULL).
            // Status filtresi açık teklifi tanımlamak için yeterli.
            var rawTeklifler = await db.TBL_VARUNA_TEKLIFs.AsNoTracking()
                .Where(t => t.DeletedOn == null
                         && t.CreatedOn.HasValue && t.CreatedOn >= start && t.CreatedOn <= end
                         && (t.Status == null || (t.Status != "Reject" && t.Status != "Denied" && t.Status != "Closed"))
                         && t.OpportunityId.HasValue)
                .Select(t => new { t.Id, Tutar = t.TotalNetAmountLocalCurrency_Amount ?? 0m })
                .ToListAsync();

            if (rawTeklifler.Count == 0)
                return (new Dictionary<int, decimal>(), 0m);

            var teklifler = rawTeklifler;

            var quoteIds = teklifler.Select(t => t.Id).ToList();

            var kalemler = await db.TBL_VARUNA_TEKLIF_URUNLERIs.AsNoTracking()
                .Where(k => k.QuoteId.HasValue && k.DeletedOn == null && quoteIds.Contains(k.QuoteId.Value))
                .Select(k => new { QuoteId = k.QuoteId!.Value, k.StockCode, Total = k.Total_Amount ?? 0m })
                .ToListAsync();

            var kalemByQuote = kalemler.GroupBy(k => k.QuoteId).ToDictionary(g => g.Key, g => g.ToList());

            // ESLESTIRME.AnaUrunId → ANA_URUN.Ad → HEDEF_URUN.Id bridge (panel UrunId namespace)
            var eslesme = (await (
                from e in db.TBLSOS_URUN_ESLESTIRMEs.AsNoTracking()
                join au in db.TBLSOS_ANA_URUNs on e.AnaUrunId equals au.Id
                join hu in db.TBLSOS_HEDEF_URUNs on au.Ad.Trim() equals hu.Ad.Trim()
                select new { e.StokKodu, HedefUrunId = hu.Id }
            ).ToListAsync())
                .GroupBy(x => x.StokKodu)
                .ToDictionary(g => g.Key, g => g.First().HedefUrunId, StringComparer.OrdinalIgnoreCase);

            var urunMap = new Dictionary<int, decimal>();
            decimal diger = 0m;

            foreach (var t in teklifler)
            {
                if (!kalemByQuote.TryGetValue(t.Id, out var fKalemler) || fKalemler.Count == 0)
                {
                    diger += t.Tutar;
                    continue;
                }
                decimal toplamDoviz = fKalemler.Sum(k => k.Total);
                if (toplamDoviz <= 0m)
                {
                    diger += t.Tutar;
                    continue;
                }
                foreach (var k in fKalemler)
                {
                    decimal pay = k.Total / toplamDoviz * t.Tutar;
                    if (!string.IsNullOrEmpty(k.StockCode) && eslesme.TryGetValue(k.StockCode, out var urunId))
                    {
                        urunMap[urunId] = urunMap.TryGetValue(urunId, out var c) ? c + pay : pay;
                    }
                    else
                    {
                        diger += pay;
                    }
                }
            }
            return (urunMap, diger);
        });
    }

    /// <summary>
    /// Ay-bazlı gerçekleşen kırılımı (YS + Yenileme).
    /// 12 ay × (Toplam, YS, Yen) döner. Cockpit fatura listesi (EfektifTarih + İade/Ret hariç) bazlı.
    /// </summary>
    private async Task<Dictionary<byte, (decimal toplam, decimal ys, decimal yen)>> GetGerceklesenByAyAsync(int yil, int? temsilciId)
    {
        var key = $"hedef_gerc_byay_{yil}_{temsilciId?.ToString() ?? "all"}";
        return await CachedAsync(key, async () =>
        {
            var start = new DateTime(yil, 1, 1);
            var end = new DateTime(yil, 12, 31, 23, 59, 59);

            var rows = await ResolveGercSatirlarAsync(start, end, temsilciId);

            var result = new Dictionary<byte, (decimal, decimal, decimal)>();
            for (byte ay = 1; ay <= 12; ay++) result[ay] = (0m, 0m, 0m);
            foreach (var r in rows)
            {
                byte ay = (byte)r.EfektifTarih.Month;
                bool isYen = r.DocCode == "ZZ08";
                var cur = result[ay];
                result[ay] = (cur.Item1 + r.NetTutar, cur.Item2 + (isYen ? 0m : r.NetTutar), cur.Item3 + (isYen ? r.NetTutar : 0m));
            }
            return result;
        });
    }

    /// <summary>
    /// Dönem-toplam gerçekleşen tutarı (Yeni Satış + Yenileme split'iyle).
    /// Cockpit SP_COCKPIT_FATURA çıktısı + VARUNA DocCode/Account zenginleştirme.
    /// EfektifFaturaTarihi (tahakkuk override) ve İade/Ret filtresi otomatik uygulanır.
    /// </summary>
    private async Task<(decimal toplam, decimal ys, decimal yen)> GetGerceklesenAsync(DateTime start, DateTime end, int? temsilciId)
    {
        var key = $"hedef_gerc_{start:yyyyMMdd}_{end:yyyyMMdd}_{temsilciId?.ToString() ?? "all"}";
        return await CachedAsync(key, async () =>
        {
            var rows = await ResolveGercSatirlarAsync(start, end, temsilciId);
            decimal toplam = rows.Sum(r => r.NetTutar);
            decimal yen = rows.Where(r => r.DocCode == "ZZ08").Sum(r => r.NetTutar);
            decimal ys = toplam - yen;
            return (toplam, ys, yen);
        });
    }

    private static string ResolveRenk(decimal attainment)
    {
        if (attainment >= 85m) return "green";
        if (attainment >= 70m) return "yellow";
        return "red";
    }

    // ── ANALİZ DETAY (Fırsat Analizi → Hedef Analizi Detay paneli) ──

    public async Task<List<HedefAnalizDetaySatir>> GetAnalizDetayAsync(string tab, int yil, DateTime start, DateTime end)
    {
        var key = $"hedef_analiz_detay_{tab}_{yil}_{start:yyyyMMdd}_{end:yyyyMMdd}";
        return await CachedAsync(key, async () =>
        {
            return tab switch
            {
                "yenileme" => await BuildYenilemeRowsAsync(yil, start, end),
                "urun" => await BuildUrunRowsAsync(yil, start, end),
                "satisci" => await BuildSatisciRowsAsync(yil, start, end),
                _ => new List<HedefAnalizDetaySatir>()
            };
        });
    }

    private async Task<List<HedefAnalizDetaySatir>> BuildYenilemeRowsAsync(int yil, DateTime start, DateTime end)
    {
        var (_, hedefYS, hedefYen) = await ComputeHedefForRangeAsync(yil, start, end, null);
        var (_, gercYS, gercYen) = await GetGerceklesenAsync(start, end, null);
        return new List<HedefAnalizDetaySatir>
        {
            new() { Isim = "Yenileme",  Hedef = hedefYen, Gerceklesen = gercYen },
            new() { Isim = "Yeni Satış", Hedef = hedefYS,  Gerceklesen = gercYS  },
        };
    }

    private async Task<List<HedefAnalizDetaySatir>> BuildUrunRowsAsync(int yil, DateTime start, DateTime end)
    {
        using var db = _contextFactory.CreateDbContext();

        // Tek sorgu — Toplam + YS + Yen tüm SatisTipi'lerini al (in-memory filtrele)
        var aylikTum = await db.TBLSOS_HEDEF_URUN_AYLIKs
            .AsNoTracking()
            .Where(x => x.SenaryoId == SENARYO_ID
                     && x.Ay >= (byte)start.Month && x.Ay <= (byte)end.Month)
            .Include(x => x.Urun)
            .ToListAsync();

        // Ürün × Ay × (Toplam, YS, Yen) — kalem-bazlı gerçekleşen
        var (matris, _) = await GetGerceklesenUrunMatrisAsync(yil, null);
        // Açık teklif (sadece Toplam — YS/Yen kırılımı yok)
        var (acikTeklifMap, _) = await GetAcikTeklifUrunAsync(start, end);

        var startMonth = (byte)start.Month;
        var endMonth   = (byte)end.Month;

        return aylikTum
            .Where(x => x.SatisTipi == "Toplam")
            .GroupBy(x => new { x.UrunId, x.Urun?.SiraNo, Ad = x.Urun?.Ad ?? "" })
            .Select(g =>
            {
                // Hedef Toplam (zaten "Toplam" satırlarından)
                var hedefTop = g.Sum(x => x.HedefTutar);

                // Hedef YS / Yen — aynı dönem aralığında, aynı ürün için ayrı SatisTipi'lerden
                var urunRows = aylikTum.Where(x => x.UrunId == g.Key.UrunId);
                var hedefYS  = urunRows.Where(x => x.SatisTipi == "YeniSatis").Sum(x => x.HedefTutar);
                var hedefYen = urunRows.Where(x => x.SatisTipi == "Yenileme").Sum(x => x.HedefTutar);

                // Gerçekleşen — kalem matrisinden (Toplam + YS + Yen ayrı)
                decimal gercTop = 0m, gercYS = 0m, gercYen = 0m;
                for (byte ay = startMonth; ay <= endMonth; ay++)
                {
                    if (matris.TryGetValue((g.Key.UrunId, ay), out var v))
                    {
                        gercTop += v.toplam;
                        gercYS  += v.ys;
                        gercYen += v.yen;
                    }
                }
                acikTeklifMap.TryGetValue(g.Key.UrunId, out var acik);
                return new HedefAnalizDetaySatir
                {
                    Isim = g.Key.Ad,
                    Hedef = hedefTop,
                    Gerceklesen = gercTop,
                    AcikTeklif = acik,
                    HedefYS = hedefYS,
                    HedefYen = hedefYen,
                    GerceklesenYS = gercYS,
                    GerceklesenYen = gercYen
                };
            })
            .OrderByDescending(x => x.Hedef)
            .ToList();
    }

    private async Task<List<HedefAnalizDetaySatir>> BuildSatisciRowsAsync(int yil, DateTime start, DateTime end)
    {
        using var db = _contextFactory.CreateDbContext();
        var temsilciler = await db.TBLSOS_HEDEF_TEMSILCIs.OrderBy(x => x.Ad).ToListAsync();
        var acikTeklifMap = await GetAcikTeklifSatisciAsync(start, end);
        // TEK PASS satışçı matrisi (9 round-trip yerine 1) — (toplam, ys, yen) tuple
        var temsilciMatris = await GetGerceklesenTemsilciAyMatrisAsync(yil);

        var rows = new List<HedefAnalizDetaySatir>();
        foreach (var t in temsilciler)
        {
            // ComputeHedefForRangeAsync zaten (toplam, ys, yen) döndürüyor
            var (hedefT, hedefYS, hedefYen) = await ComputeHedefForRangeAsync(yil, start, end, t.Id);
            decimal gercT = 0m, gercYS = 0m, gercYen = 0m;
            for (byte ay = (byte)start.Month; ay <= (byte)end.Month; ay++)
            {
                if (temsilciMatris.TryGetValue((t.Id, ay), out var v))
                {
                    gercT   += v.toplam;
                    gercYS  += v.ys;
                    gercYen += v.yen;
                }
            }
            acikTeklifMap.TryGetValue(t.Id, out var acik);
            if (hedefT == 0m && gercT == 0m && acik == 0m) continue;
            rows.Add(new HedefAnalizDetaySatir
            {
                Isim = t.Ad,
                Hedef = hedefT,
                Gerceklesen = gercT,
                AcikTeklif = acik,
                HedefYS = hedefYS,
                HedefYen = hedefYen,
                GerceklesenYS = gercYS,
                GerceklesenYen = gercYen
            });
        }

        return rows
            .OrderByDescending(r => r.Hedef > 0 ? (r.Gerceklesen + r.AcikTeklif) / r.Hedef : 0m)
            .ToList();
    }

    // ── Generic cache wrapper (CockpitDataService ile aynı pattern) ──
    private async Task<T> CachedAsync<T>(string key, Func<Task<T>> factory)
    {
        if (_cache.TryGetValue(key, out T? cached) && cached != null) return cached;
        var sem = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();
        try
        {
            if (_cache.TryGetValue(key, out cached) && cached != null) return cached;
            var result = await factory();
            _cache.Set(key, result, TTL);
            lock (_keys) _keys.Add(key);
            return result;
        }
        finally
        {
            sem.Release();
        }
    }
}
