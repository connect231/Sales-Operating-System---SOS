using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using SOS.DbData;
using SOS.Services;
using Microsoft.EntityFrameworkCore;
using SOS.Models.ViewModels;
using SOS.Models.MsK;
using Microsoft.Extensions.Caching.Memory;

namespace SOS.Controllers
{
    [Authorize]
    public class CockpitController : Controller
    {
        private readonly IDbContextFactory<MskDbContext> _contextFactory;
        private readonly IMemoryCache _cache;
        private readonly ICockpitDataService _cockpitData;
        private readonly IHedefService _hedef;
        private const string CACHE_KEY_FATURALAR = "cockpit_faturalar";
        private const string CACHE_KEY_SIPARISLER = "cockpit_siparisler";
        private const string CACHE_KEY_URUNLER = "cockpit_urunler";
        private const string CACHE_KEY_SOZLESMELER = "cockpit_sozlesmeler";
        private const string CACHE_KEY_URUN_MAP = "cockpit_urun_map";
        private const string CACHE_KEY_MUSTERI_MAP = "cockpit_musteri_map";
        private const string CACHE_KEY_HEDEFLER = "cockpit_hedefler";
        private const string CACHE_KEY_VARUNA_TUTAR = "cockpit_varuna_tutar";
        private const string CACHE_KEY_URUN_GRUP_MAP = "cockpit_urun_grup_map"; // StockCode → AnaUrunAd
        private const string CACHE_KEY_TEMSILCI_MAP = "cockpit_temsilci_map";  // Fatura_No (SerialNumber) → Satış Temsilcisi adı (4-kademe)

        private static readonly SemaphoreSlim _cacheLock = new(1, 1);

        public CockpitController(IDbContextFactory<MskDbContext> contextFactory, IMemoryCache cache, ICockpitDataService cockpitData, IHedefService hedef)
        {
            _contextFactory = contextFactory;
            _cache = cache;
            _cockpitData = cockpitData;
            _hedef = hedef;
        }

        #region Filter Parsing

        private (DateTime start, DateTime end, string filter, int months) ParseFilter(string? filter, string? startDate, string? endDate)
        {
            var now = DateTime.Now;
            var today = now.Date.AddDays(1).AddSeconds(-1); // 23:59:59
            var year = now.Year;
            DateTime start, end;
            int months;
            var fmtP = System.Globalization.CultureInfo.InvariantCulture;
            var style = System.Globalization.DateTimeStyles.None;

            if (!string.IsNullOrEmpty(startDate) && !string.IsNullOrEmpty(endDate)
                && DateTime.TryParseExact(startDate, "yyyy-MM-dd", fmtP, style, out var sd)
                && DateTime.TryParseExact(endDate, "yyyy-MM-dd", fmtP, style, out var ed))
            {
                start = sd.Date;
                end = ed.Date.AddDays(1).AddSeconds(-1);
                months = Math.Max(1, (end.Year - start.Year) * 12 + end.Month - start.Month + 1);
                return (start, end, "range", months);
            }

            switch (filter?.ToLowerInvariant())
            {
                case "ytd":
                    // YTD = yıl başı → bulunduğu ayın SONU (tam ay).
                    // FirsatAnaliz/Hedef ile aynı semantik — Bu Ay (ay sonu) ⊆ YTD garantisi.
                    start = new DateTime(year, 1, 1);
                    end = new DateTime(year, now.Month, DateTime.DaysInMonth(year, now.Month), 23, 59, 59);
                    months = now.Month;
                    break;
                case "q1":
                    start = new DateTime(year, 1, 1);
                    end = new DateTime(year, 3, 31, 23, 59, 59);
                    months = 3;
                    break;
                case "q2":
                    start = new DateTime(year, 4, 1);
                    end = new DateTime(year, 6, 30, 23, 59, 59);
                    months = 3;
                    break;
                case "q3":
                    start = new DateTime(year, 7, 1);
                    end = new DateTime(year, 9, 30, 23, 59, 59);
                    months = 3;
                    break;
                case "q4":
                    start = new DateTime(year, 10, 1);
                    end = new DateTime(year, 12, 31, 23, 59, 59);
                    months = 3;
                    break;
                case "lastmonth":
                    var lmMonth = now.Month == 1 ? 12 : now.Month - 1;
                    var lmYear = now.Month == 1 ? year - 1 : year;
                    start = new DateTime(lmYear, lmMonth, 1);
                    end = new DateTime(lmYear, lmMonth, DateTime.DaysInMonth(lmYear, lmMonth), 23, 59, 59);
                    months = 1;
                    break;
                default:
                    filter = "month";
                    start = new DateTime(year, now.Month, 1);
                    end = new DateTime(year, now.Month, DateTime.DaysInMonth(year, now.Month), 23, 59, 59);
                    months = 1;
                    break;
            }

            return (start, end, filter ?? "month", months);
        }

        #endregion

        #region Status Helpers

        private static readonly HashSet<string> _negativeDurumSet = new(StringComparer.OrdinalIgnoreCase)
        {
            "İADE", "IADE", "İPTAL", "IPTAL", "İADE FATURA", "IADE FATURA"
        };

        private static bool IsRetDurum(string? durum)
            => !string.IsNullOrWhiteSpace(durum)
               && durum.AsSpan().Trim().Equals("RET".AsSpan(), StringComparison.OrdinalIgnoreCase);

        private static bool IsNegatifDurum(string? durum)
        {
            if (string.IsNullOrWhiteSpace(durum)) return false;
            var d = durum.Trim();
            if (_negativeDurumSet.Contains(d)) return true;
            // "Iade Fatura" vb. varyantlar — contains bazlı fallback
            return d.Contains("ade", StringComparison.OrdinalIgnoreCase)
                || d.Contains("ptal", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTahsilatOrKrediKarti(string? durum)
        {
            if (string.IsNullOrWhiteSpace(durum)) return false;
            var trimmed = durum.AsSpan().Trim();
            return trimmed.Equals("TAHSİL EDİLDİ".AsSpan(), StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("KREDİ KARTI".AsSpan(), StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("KREDI KARTI".AsSpan(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDurumBos(string? durum)
            => string.IsNullOrWhiteSpace(durum);

        #endregion

        #region Cache (Parallel Loading)

        /// <summary>
        /// Cache'den veri yükler. Cold start'ta SemaphoreSlim ile race condition önlenir.
        /// Cache warm ise lock'a girmeden döner (hot path = 0 overhead).
        /// </summary>
        // Public static: hem controller hem CockpitCacheWarmer (BackgroundService) tarafından çağrılır.
        // forceRefresh=true → cache bypass (warmer background refresh için kullanır).
        internal static async Task<(List<VIEW_CP_EXCEL_FATURA> faturalar,
                            Dictionary<string, (string? AccountTitle, string? ProductName, decimal? Quantity)> urunMap,
                            Dictionary<string, string?> musteriMap,
                            List<TBL_VARUNA_SOZLESME> sozlesmeler,
                            Dictionary<int, decimal> hedefler,
                            Dictionary<string, decimal> varunaTutarMap,
                            Dictionary<string, List<(string Grup, decimal TlTutar)>> urunGrupMap)> LoadAllCachedDataAsync(
                            IDbContextFactory<MskDbContext> contextFactory,
                            IMemoryCache cache,
                            IHedefService hedefService,
                            bool forceRefresh = false)
        {
            // Hot path — cache warm, lock'a gerek yok
            if (!forceRefresh
                && cache.TryGetValue(CACHE_KEY_FATURALAR, out List<VIEW_CP_EXCEL_FATURA>? cf) && cf != null
                && cache.TryGetValue(CACHE_KEY_URUN_MAP, out Dictionary<string, (string?, string?, decimal?)>? um) && um != null
                && cache.TryGetValue(CACHE_KEY_MUSTERI_MAP, out Dictionary<string, string?>? mm) && mm != null
                && cache.TryGetValue(CACHE_KEY_SOZLESMELER, out List<TBL_VARUNA_SOZLESME>? cs) && cs != null
                && cache.TryGetValue(CACHE_KEY_HEDEFLER, out Dictionary<int, decimal>? ch) && ch != null
                && cache.TryGetValue(CACHE_KEY_VARUNA_TUTAR, out Dictionary<string, decimal>? vt) && vt != null
                && cache.TryGetValue(CACHE_KEY_URUN_GRUP_MAP, out Dictionary<string, List<(string Grup, decimal TlTutar)>>? ug) && ug != null)
            {
                return (cf, um, mm, cs, ch, vt, ug);
            }

            // Cold path — lock ile tek thread DB'ye gider, diğerleri bekler
            await _cacheLock.WaitAsync();
            try
            {
                // Double-check: lock beklerken başka thread doldurmuş olabilir (force refresh'te skip)
                if (!forceRefresh
                    && cache.TryGetValue(CACHE_KEY_FATURALAR, out List<VIEW_CP_EXCEL_FATURA>? cf2) && cf2 != null
                    && cache.TryGetValue(CACHE_KEY_URUN_MAP, out Dictionary<string, (string?, string?, decimal?)>? um2) && um2 != null
                    && cache.TryGetValue(CACHE_KEY_MUSTERI_MAP, out Dictionary<string, string?>? mm2) && mm2 != null
                    && cache.TryGetValue(CACHE_KEY_SOZLESMELER, out List<TBL_VARUNA_SOZLESME>? cs2) && cs2 != null
                    && cache.TryGetValue(CACHE_KEY_HEDEFLER, out Dictionary<int, decimal>? ch2) && ch2 != null
                    && cache.TryGetValue(CACHE_KEY_VARUNA_TUTAR, out Dictionary<string, decimal>? vt2) && vt2 != null
                    && cache.TryGetValue(CACHE_KEY_URUN_GRUP_MAP, out Dictionary<string, List<(string Grup, decimal TlTutar)>>? ug2) && ug2 != null)
                {
                    return (cf2, um2, mm2, cs2, ch2, vt2, ug2);
                }

                // ── Parallel DB sorguları — her biri kendi DbContext'i ile (IDbContextFactory) ──
                // Sıralı 5 sorgu yerine parallel: ~800ms → ~300ms (cold path)
                var faturaTask = Task.Run(async () =>
                {
                    using var db1 = contextFactory.CreateDbContext();
                    return await db1.VIEW_CP_EXCEL_FATURAs.AsNoTracking().ToListAsync();
                });

                var siparisTask = Task.Run(async () =>
                {
                    using var db2 = contextFactory.CreateDbContext();
                    return await db2.TBL_VARUNA_SIPARIs.AsNoTracking()
                        .Where(s => s.OrderId != null && s.DeletedOn == null)
                        .Select(s => new SiparisDto
                        {
                            SerialNumber = s.SerialNumber,
                            OrderId = s.OrderId,
                            AccountTitle = s.AccountTitle,
                            OrderStatus = s.OrderStatus,
                            TotalNetAmount = s.TotalNetAmount,
                            InvoiceDate = s.InvoiceDate,
                            SAPOutReferenceCode = s.SAPOutReferenceCode,
                            ModifiedOn = s.ModifiedOn,
                            CreatedOn = s.CreatedOn,
                            CreateOrderDate = s.CreateOrderDate
                        })
                        .ToListAsync();
                });

                // ── Müşteri lookup: SerialNumber → AccountTitle GENİŞ tarama ──
                // Kullanıcı kuralı (2026-05-13): "Fatura no varsa Varuna'da kaydı var; varsa müşteri de var."
                // Mali hesap için siparisTask DeletedOn IS NULL + OrderId NOT NULL filtreliyor — bu filtreler
                // silinmiş/eski sipariş kayıtlarındaki müşteri unvanını da eliyordu (104 fatura "—" ile geliyordu).
                // Müşteri lookup için ayrı, geniş tarama: yalnızca SerialNumber + AccountTitle dolu olsun.
                var musteriLookupTask = Task.Run(async () =>
                {
                    using var dbM = contextFactory.CreateDbContext();
                    return await dbM.TBL_VARUNA_SIPARIs.AsNoTracking()
                        .Where(s => s.SerialNumber != null && s.AccountTitle != null)
                        .Select(s => new { s.SerialNumber, s.AccountTitle, s.ModifiedOn, s.CreatedOn })
                        .ToListAsync();
                });

                var urunTask = Task.Run(async () =>
                {
                    using var db3 = contextFactory.CreateDbContext();
                    // CrmOrderId + StockCode bazlı dedupe — CLAUDE.md kuralı: aynı sipariş+stock bir satır olmalı
                    return (await db3.TBL_VARUNA_SIPARIS_URUNLERIs.AsNoTracking()
                        .Where(u => u.CrmOrderId != null)
                        .Select(u => new UrunDto { CrmOrderId = u.CrmOrderId, ProductName = u.ProductName, StockCode = u.StockCode, Quantity = u.Quantity, Total = u.Total })
                        .ToListAsync())
                        .GroupBy(u => new { u.CrmOrderId, u.StockCode })
                        .Select(g => new UrunDto
                        {
                            CrmOrderId = g.Key.CrmOrderId,
                            StockCode = g.Key.StockCode,
                            ProductName = g.First().ProductName,
                            Quantity = g.Sum(x => x.Quantity ?? 0),
                            Total = g.Sum(x => x.Total ?? 0)
                        })
                        .ToList();
                });

                var sozlesmeTask = Task.Run(async () =>
                {
                    using var db4 = contextFactory.CreateDbContext();
                    return await db4.TBL_VARUNA_SOZLESMEs.AsNoTracking()
                        .Where(s => s.RenewalDate.HasValue && s.DeletedOn == null)
                        .ToListAsync();
                });

                await Task.WhenAll(faturaTask, siparisTask, urunTask, sozlesmeTask, musteriLookupTask);
                var faturalar = faturaTask.Result;
                var siparisler = siparisTask.Result;
                var urunler = urunTask.Result;
                var sozlesmeler = sozlesmeTask.Result;
                var musteriLookup = musteriLookupTask.Result;

                // Lookup map'leri oluştur
                var urunMap = siparisler
                    .Join(urunler, s => s.OrderId, u => u.CrmOrderId,
                          (s, u) => new { s.SerialNumber, s.AccountTitle, u.ProductName, u.Quantity })
                    .Where(x => x.SerialNumber != null)
                    .GroupBy(x => x.SerialNumber!)
                    .ToDictionary(g => g.Key, g => (g.First().AccountTitle, g.First().ProductName, g.First().Quantity));

                // Müşteri map: geniş tarama (DeletedOn / OrderId filter YOK) — fatura no varsa müşteri çekilebilsin.
                // Aynı SerialNumber için birden çok kayıt varsa en güncel (ModifiedOn desc → CreatedOn desc) tercih.
                var musteriMap = musteriLookup
                    .GroupBy(x => x.SerialNumber!)
                    .ToDictionary(
                        g => g.Key,
                        g => g.OrderByDescending(x => x.ModifiedOn ?? x.CreatedOn ?? DateTime.MinValue)
                              .First().AccountTitle);

                // Varuna kalem bazlı net tutar: OrderId → SUM(kalem.Total)
                // İptal kalemleri negatif Total taşır → net toplam Excel ile tutarlı olur
                var kalemToplamByOrder = urunler
                    .GroupBy(u => u.CrmOrderId!)
                    .ToDictionary(g => g.Key, g => g.Sum(u => u.Total ?? 0));

                // SerialNumber → TL tutar map
                // Kalem toplamı varsa: (kalemNet / kalemBrüt) * TotalNetAmount → TL net
                // Kalem toplamı yoksa veya sıfırsa: TotalNetAmount direkt
                var varunaTutarMap = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                foreach (var sip in siparisler.Where(s =>
                    s.SerialNumber != null
                    && s.TotalNetAmount.HasValue && s.TotalNetAmount.Value > 0
                    && string.Equals(s.OrderStatus, "Closed", StringComparison.OrdinalIgnoreCase)))
                {
                    var tna = sip.TotalNetAmount!.Value;

                    // Kalemlerin net toplamı (iptal kalemleri negatif)
                    if (sip.OrderId != null && kalemToplamByOrder.TryGetValue(sip.OrderId, out var kalemNet))
                    {
                        // Kalemlerin brüt toplamı (mutlak değer — oran hesabı için)
                        var kalemBrut = urunler
                            .Where(u => u.CrmOrderId == sip.OrderId && (u.Total ?? 0) > 0)
                            .Sum(u => u.Total ?? 0);

                        if (kalemBrut > 0 && kalemNet < kalemBrut)
                        {
                            // İptal/iade kalemleri var → TL tutarı oranla düşür
                            varunaTutarMap[sip.SerialNumber!] = tna * (kalemNet / kalemBrut);
                        }
                        else
                        {
                            varunaTutarMap[sip.SerialNumber!] = tna;
                        }
                    }
                    else
                    {
                        varunaTutarMap[sip.SerialNumber!] = tna;
                    }
                }

                // Hedef artık HedefService üzerinden TBLSOS_HEDEF_URUN_AYLIK (yeni senaryo bazlı tablo)
                // toplamından gelir; eski TBLSOS_HEDEF_AYLIK fallback olarak HedefService içinde kalır.
                // Tahakkuk paralel — bağımsız context.
                var hedefTask = hedefService.GetGenelAylikSozlukAsync(DateTime.Now.Year);
                var tahakkukTask = Task.Run(async () =>
                {
                    using var dbT = contextFactory.CreateDbContext();
                    return await dbT.TBLSOS_FATURA_TAHAKKUKs.AsNoTracking()
                        .Where(t => t.Aktif)
                        .Select(t => new { t.SapReferansNo, t.FaturaNo, t.TahakkukTarihi })
                        .ToListAsync();
                });
                await Task.WhenAll(hedefTask, tahakkukTask);
                var hedefler = hedefTask.Result;
                var tahakkukRecords = tahakkukTask.Result;
                var tahakkukMap = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in tahakkukRecords)
                {
                    tahakkukMap[r.SapReferansNo] = r.TahakkukTarihi;       // SAP key (primary)
                    if (!string.IsNullOrEmpty(r.FaturaNo))
                        tahakkukMap[r.FaturaNo] = r.TahakkukTarihi;          // FaturaNo key (compat)
                }

                // İade/Ret faturalarının Varuna karşılığını blacklist'e al
                // VIEW'de İADE/RET olan VE Varuna'da eşleşen fatura → aynı sipariş iptal olmuş
                // O siparişin pozitif tutarını da dip toplamdan çıkarmalıyız
                var iadeRetBlacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in faturalar)
                {
                    if (f.Fatura_No != null
                        && (IsRetDurum(f.Durum) || IsNegatifDurum(f.Durum))
                        && varunaTutarMap.ContainsKey(f.Fatura_No))
                    {
                        iadeRetBlacklist.Add(f.Fatura_No);
                    }
                }
                // Blacklist'teki siparişleri varunaTutarMap'ten çıkar
                foreach (var fn in iadeRetBlacklist)
                    varunaTutarMap.Remove(fn);

                // Her faturaya NetTutar (Varuna KDV hariç), KdvDahilTutar (Excel), VarunaEslesti
                // ve EfektifFaturaTarihi (tahakkuk varsa onu, yoksa Fatura_Tarihi'ni) ata
                foreach (var f in faturalar)
                {
                    var excelTutar = f.Fatura_Toplam ?? 0;
                    f.KdvDahilTutar = excelTutar;
                    if (f.Fatura_No != null && varunaTutarMap.TryGetValue(f.Fatura_No, out var vNet))
                    {
                        f.NetTutar = vNet;
                        f.VarunaEslesti = true;
                    }
                    else
                    {
                        f.NetTutar = excelTutar; // Varuna'da yoksa Excel tutarı fallback
                        f.VarunaEslesti = false;
                    }

                    // Tahakkuk override
                    if (f.Fatura_No != null && tahakkukMap.TryGetValue(f.Fatura_No, out var tahakkukTarihi))
                    {
                        f.EfektifFaturaTarihi = tahakkukTarihi;
                        f.TahakkukVar = true;
                    }
                    else
                    {
                        f.EfektifFaturaTarihi = f.Fatura_Tarihi;
                        f.TahakkukVar = false;
                    }
                }

                // ── Sentetik fatura: Varuna Closed + VIEW'de yok (tahakkuk opsiyonel) ──
                // Tahakkuk varsa onun tarihi, yoksa InvoiceDate, yoksa ModifiedOn (Closed olduğu tarih)
                // VIEW'e girince SerialNumber eşleşmesiyle deduplicate olur — sentetik kaybolur.
                var excelFaturaNoSet = new HashSet<string>(
                    faturalar.Where(f => f.Fatura_No != null).Select(f => f.Fatura_No!),
                    StringComparer.OrdinalIgnoreCase);

                var sentetikEklenen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var sip in siparisler.Where(s =>
                    s.TotalNetAmount.HasValue && s.TotalNetAmount.Value > 0
                    && string.Equals(s.OrderStatus, "Closed", StringComparison.OrdinalIgnoreCase)))
                {
                    var faturaNo = sip.SerialNumber
                        ?? (!string.IsNullOrEmpty(sip.SAPOutReferenceCode) ? $"SAP:{sip.SAPOutReferenceCode.Trim()}" : null);
                    if (faturaNo == null) continue;

                    // Zaten VIEW'de varsa atla
                    if (excelFaturaNoSet.Contains(faturaNo) || !sentetikEklenen.Add(faturaNo)) continue;

                    // Tahakkuk lookup: SN, SAP no, veya SAP: prefix
                    DateTime? tahakkukOverride = null;
                    if (sip.SerialNumber != null && tahakkukMap.TryGetValue(sip.SerialNumber, out var thDate))
                        tahakkukOverride = thDate;
                    else if (!string.IsNullOrEmpty(sip.SAPOutReferenceCode) && tahakkukMap.TryGetValue(sip.SAPOutReferenceCode.Trim(), out var thDate2))
                        tahakkukOverride = thDate2;
                    else if (tahakkukMap.TryGetValue(faturaNo, out var thDate3))
                        tahakkukOverride = thDate3;

                    // Efektif tarih: tahakkuk → InvoiceDate → ModifiedOn (Closed olduğu tarih)
                    var efektifTarih = tahakkukOverride
                        ?? sip.InvoiceDate
                        ?? sip.ModifiedOn
                        ?? sip.CreatedOn
                        ?? sip.CreateOrderDate;
                    if (!efektifTarih.HasValue) continue;

                    var sentetik = new VIEW_CP_EXCEL_FATURA
                    {
                        Fatura_No = faturaNo,
                        Fatura_Tarihi = sip.InvoiceDate ?? efektifTarih,
                        Fatura_Toplam = sip.TotalNetAmount,
                        Fatura_Vade_Tarihi = sip.InvoiceDate ?? efektifTarih,
                        Tahsil_Edilen = 0,
                        Bekleyen_Bakiye = sip.TotalNetAmount,
                        Durum = null,
                        NetTutar = sip.TotalNetAmount,
                        KdvDahilTutar = sip.TotalNetAmount,
                        VarunaEslesti = true,
                        MusteriUnvan = sip.AccountTitle,
                        EfektifFaturaTarihi = efektifTarih,
                        TahakkukVar = tahakkukOverride.HasValue
                    };
                    faturalar.Add(sentetik);
                    if (!varunaTutarMap.ContainsKey(faturaNo))
                        varunaTutarMap[faturaNo] = sip.TotalNetAmount.Value;
                }

                // Ürün grup eşleştirme: StockCode → AnaUrunAd
                // NOT: DB'de nadiren duplicate StokKodu olabiliyor (EH.02.018 gibi) — GroupBy ilk kaydı alır
                // AnaUrun null ise (FK bozuksa) kayıt atlanır → kalem ürün kırılımına girmez
                Dictionary<string, string> eslestirmeler;
                using (var dbE = contextFactory.CreateDbContext())
                {
                    eslestirmeler = (await dbE.TBLSOS_URUN_ESLESTIRMEs.AsNoTracking()
                        .Include(e => e.AnaUrun)
                        .ToListAsync())
                        .Where(e => e.AnaUrun != null && !string.IsNullOrEmpty(e.AnaUrun.Ad))
                        .GroupBy(e => e.StokKodu)
                        .ToDictionary(g => g.Key, g => g.First().AnaUrun!.Ad);
                }

                // Fatura_No (SerialNumber) → kalem bazlı ürün grubu TL dağılımı
                // Her kalem için: (kalem.Total / toplamDöviz) * TotalNetAmount → ürün grubuna
                // NOT: TBLSOS_URUN_ESLESTIRME'de bulunmayan StockCode'lar SKIP edilir (UI'da "Diğer" gösterilmez).
                //      Bu durumda ürün kırılımı toplamı, fatura dip toplamından küçük olabilir.
                var urunGrupMap = new Dictionary<string, List<(string Grup, decimal TlTutar)>>();
                var urunByCrmOrder = urunler.Where(u => u.CrmOrderId != null)
                    .GroupBy(u => u.CrmOrderId!).ToDictionary(g => g.Key, g => g.ToList());
                foreach (var siparis in siparisler.Where(s => s.OrderId != null
                    && s.TotalNetAmount.HasValue && s.TotalNetAmount.Value > 0
                    && string.Equals(s.OrderStatus, "Closed", StringComparison.OrdinalIgnoreCase)))
                {
                    // Key: SerialNumber varsa onu, yoksa SAP:xxx
                    var mapKey = siparis.SerialNumber
                        ?? (!string.IsNullOrEmpty(siparis.SAPOutReferenceCode)
                            ? $"SAP:{siparis.SAPOutReferenceCode.Trim()}" : null);
                    if (mapKey == null) continue;
                    if (urunGrupMap.ContainsKey(mapKey)) continue;
                    if (!urunByCrmOrder.TryGetValue(siparis.OrderId!, out var sipUrunleri)) continue;
                    var toplamDoviz = sipUrunleri.Sum(u => u.Total ?? 0);
                    if (toplamDoviz == 0) continue;
                    var kalemler = new List<(string Grup, decimal TlTutar)>();
                    foreach (var u in sipUrunleri)
                    {
                        if (u.StockCode == null || !eslestirmeler.TryGetValue(u.StockCode, out var grup))
                            continue;
                        var tlTutar = (u.Total ?? 0) / toplamDoviz * siparis.TotalNetAmount!.Value;
                        kalemler.Add((grup, tlTutar));
                    }
                    urunGrupMap[mapKey] = kalemler;
                }

                // Müşteri unvanını faturalar listesine iliştir — tüm endpoint'lerin tutarlı görmesi için
                // cache.Set'ten ÖNCE bir kez yap. MapMusteriUrun in-place çalışır; idempotent.
                MapMusteriUrun(faturalar, urunMap, musteriMap);

                // Cache'e yaz — TTL 5 dk (CacheWarmer her 4 dk'da refresh eder; warmer fail olsa bile veri ≤5dk eskir)
                var ttl = TimeSpan.FromMinutes(5);
                cache.Set(CACHE_KEY_FATURALAR, faturalar, ttl);
                cache.Set(CACHE_KEY_SOZLESMELER, sozlesmeler, ttl);
                cache.Set(CACHE_KEY_URUN_MAP, urunMap, ttl);
                cache.Set(CACHE_KEY_MUSTERI_MAP, musteriMap, ttl);
                cache.Set(CACHE_KEY_HEDEFLER, hedefler, ttl);
                cache.Set(CACHE_KEY_VARUNA_TUTAR, varunaTutarMap, ttl);
                cache.Set(CACHE_KEY_URUN_GRUP_MAP, urunGrupMap, ttl);

                return (faturalar, urunMap, musteriMap, sozlesmeler, hedefler, varunaTutarMap, urunGrupMap);
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        private static void MapMusteriUrun(
            IEnumerable<VIEW_CP_EXCEL_FATURA> kayitlar,
            Dictionary<string, (string? AccountTitle, string? ProductName, decimal? Quantity)> urunMap,
            Dictionary<string, string?> musteriMap)
        {
            foreach (var f in kayitlar)
            {
                if (f.Fatura_No == null) continue;
                if (urunMap.TryGetValue(f.Fatura_No, out var urun))
                {
                    f.MusteriUnvan = urun.AccountTitle;
                    f.UrunAdi = urun.ProductName;
                    f.Miktar = urun.Quantity;
                }
                else if (musteriMap.TryGetValue(f.Fatura_No, out var musteri))
                {
                    f.MusteriUnvan = musteri;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Satış Temsilcisi 4-kademe resolver — Fatura_No (SerialNumber) → adı
        // Pri 0: REPS.EnterpriceAccountRepresentativeId (kurumsal)
        // Pri 1: REPS.AccountOwnerId                  (hesap sahibi)
        // Pri 2: SIPARIS.ProposalOwnerId              (teklif sahibi)
        // Canlı DB ölçümü (2026-05-13): 307 "atanmamış" → 1 (Pri 1 tek başına 308 ek kapatıyor).
        // ─────────────────────────────────────────────────────────────────────────
        internal static async Task<Dictionary<string, string>> GetTemsilciMapAsync(
            IDbContextFactory<MskDbContext> contextFactory,
            IMemoryCache cache)
        {
            if (cache.TryGetValue(CACHE_KEY_TEMSILCI_MAP, out Dictionary<string, string>? cached) && cached != null)
                return cached;

            using var db = contextFactory.CreateDbContext();

            // PERSON: Id → PersonNameSurname (birleşik field zaten dolu)
            var personList = await db.TBL_VARUNA_PERSONs.AsNoTracking()
                .Where(p => p.PersonNameSurname != null)
                .Select(p => new { p.Id, p.PersonNameSurname })
                .ToListAsync();
            var personById = personList
                .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().PersonNameSurname!.Trim(), StringComparer.OrdinalIgnoreCase);

            // REPS Active: AccountId → AccountOwnerId (Account'un müşteri temsilcisi)
            // Kullanıcı kararı (2026-05-14): EnterpriceAccountRepresentativeId KULLANMA — bu alan
            // "kurumsal ilişki yetkilisi" (örn. İsmet Alkan / Proje Müdür Yardımcısı) tarafına gidiyor,
            // satış temsilcisi DEĞİL. AccountOwnerId 9 kanonik satış temsilcisine birebir eşleşiyor.
            var repsList = await db.TBL_VARUNA_ACCOUNT_REPRESENTATIVESs.AsNoTracking()
                .Where(r => r.State == "Active" && r.AccountId != null && r.AccountOwnerId != null)
                .Select(r => new { r.AccountId, r.AccountOwnerId })
                .ToListAsync();
            var repByAccount = repsList
                .GroupBy(r => r.AccountId!.Value.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.First(),
                    StringComparer.OrdinalIgnoreCase);

            // Whitelist: TBLSOS_HEDEF_TEMSILCI Aktif=1 (kanonik satış temsilcileri)
            // Account.OwnerId/Sipariş.ProposalOwnerId bu whitelist'in dışındaysa atama yok.
            var whitelist = await db.TBLSOS_HEDEF_TEMSILCIs.AsNoTracking()
                .Where(t => t.Aktif && t.CrmPersonId != null)
                .Select(t => t.CrmPersonId!)
                .ToListAsync();
            var whitelistSet = new HashSet<string>(whitelist, StringComparer.OrdinalIgnoreCase);

            // SIPARIS: Fatura_No (SerialNumber veya "SAP:"+SAPOutReferenceCode sentetik) → (AccountId, ProposalOwnerId)
            // musteriMap ile aynı geniş kapsam — silinmiş sipariş kayıtlarını da analiz için yakala.
            // Sentetik fatura no formatı: LoadAllCachedDataAsync'in sentetik dal'ında "SAP:"+SAPOutReferenceCode.
            var siparisRaw = await db.TBL_VARUNA_SIPARIs.AsNoTracking()
                .Where(s => s.SerialNumber != null || s.SAPOutReferenceCode != null)
                .Select(s => new { s.SerialNumber, s.SAPOutReferenceCode, s.AccountId, s.ProposalOwnerId, s.ModifiedOn, s.CreatedOn })
                .ToListAsync();
            var siparisList = siparisRaw
                .Select(s => new
                {
                    Key = !string.IsNullOrEmpty(s.SerialNumber)
                          ? s.SerialNumber
                          : (!string.IsNullOrEmpty(s.SAPOutReferenceCode) ? $"SAP:{s.SAPOutReferenceCode.Trim()}" : null),
                    s.AccountId,
                    s.ProposalOwnerId,
                    s.ModifiedOn,
                    s.CreatedOn
                })
                .Where(s => s.Key != null)
                .GroupBy(s => s.Key!)
                .Select(g => g.OrderByDescending(x => x.ModifiedOn ?? x.CreatedOn ?? DateTime.MinValue).First())
                .ToList();

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in siparisList)
            {
                if (s.Key == null || map.ContainsKey(s.Key)) continue;
                string? rep = null;

                // Pri 0: Account.OwnerId (kanonik müşteri temsilcisi)
                if (!string.IsNullOrEmpty(s.AccountId)
                    && repByAccount.TryGetValue(s.AccountId, out var r)
                    && r.AccountOwnerId.HasValue)
                {
                    var ownerId = r.AccountOwnerId.Value.ToString();
                    if (whitelistSet.Contains(ownerId) && personById.TryGetValue(ownerId, out var p0))
                        rep = p0;
                }

                // Pri 1: Sipariş.ProposalOwnerId (account owner yoksa teklif sahibi)
                if (rep == null
                    && !string.IsNullOrEmpty(s.ProposalOwnerId)
                    && whitelistSet.Contains(s.ProposalOwnerId)
                    && personById.TryGetValue(s.ProposalOwnerId, out var p1))
                    rep = p1;

                if (rep != null) map[s.Key] = rep;
            }

            cache.Set(CACHE_KEY_TEMSILCI_MAP, map, TimeSpan.FromMinutes(5));
            return map;
        }

        // DTO'lar
        private class SpDefCheck { public string? Durum { get; set; } }
        private class SiparisDto
        {
            public string? SerialNumber { get; set; }
            public string? OrderId { get; set; }
            public string? AccountTitle { get; set; }
            public string? OrderStatus { get; set; }
            public decimal? TotalNetAmount { get; set; }
            public DateTime? InvoiceDate { get; set; }
            public string? SAPOutReferenceCode { get; set; }
            public DateTime? ModifiedOn { get; set; }
            public DateTime? CreatedOn { get; set; }
            public DateTime? CreateOrderDate { get; set; }
        }

        private class UrunDto
        {
            public string? CrmOrderId { get; set; }
            public string? ProductName { get; set; }
            public string? StockCode { get; set; }
            public decimal? Quantity { get; set; }
            public decimal? Total { get; set; }
        }

        #endregion

        #region Single-Pass Metrics

        /// <summary>
        /// allFaturalar üzerinde TEK geçişte tüm metrikleri hesaplar.
        /// 15+ ayrı LINQ iterasyonu yerine O(n) tek döngü.
        /// </summary>
        private struct FaturaMetrics
        {
            // Dönem fatura
            public decimal FatToplam;
            public int FatAdet;
            // Dönem tahsilat
            public decimal TahEdilen;      // PAY: Tahsil_Tarihi dönemde → SUM(Tahsil_Edilen)
            public decimal TahBakiye;      // Bekleyen_Bakiye toplamı (vade ≤ dönem sonu)
            public int TahAdet;
            public decimal TahGecmisTahsilat; // Tahsil_Tarihi dönem ÖNCESI → SUM(Tahsil_Edilen)
            // Önceki dönem (trend)
            public decimal PrevFatToplam;
            public decimal PrevTahToplam;
            // CEI Dönem
            public decimal CeiDonemVgBakiye;
            // CEI Haftalık (PAY: Tahsil_Tarihi hafta içi, PAYDA: efektif ≤ hafta sonu bakiye + pay)
            public decimal HaftalikTah;       // PAY: SUM(Tahsil_Edilen) where Tahsil_Tarihi in hafta
            public decimal HaftalikBakiye;    // SUM(Bekleyen_Bakiye) where efektif ≤ hafta sonu & bakiye > 0
            // CEI Aylık
            public decimal AylikTah;
            public decimal AylikBakiye;
            // CEI YTD
            public decimal YtdTahToplam;
            public decimal YtdBakiye;
            public decimal YtdVgBakiye;
            // Legacy 2025
            public decimal Legacy2025Bakiye;
            // Vadesi geçmiş
            public decimal VadesiGecmisAlacak;
            public int VadesiGecmisAdet;
            // Beklenen
            public decimal BeklenenTahsilat;
            public int BeklenenAdet;
            // Fixed cards
            public decimal FixedMonthActual;
            public decimal FixedYTDActual;
            // YTD Fatura
            public decimal YtdFatGerceklesme;
            // Varuna dışı (not için)
            public decimal VarunaDisiToplam;
            public int VarunaDisiAdet;
        }

        private static FaturaMetrics ComputeMetrics(
            List<VIEW_CP_EXCEL_FATURA> allFaturalar,
            DateTime start, DateTime end,
            DateTime prevStart, DateTime prevEnd,
            DateTime donemSonuCei,
            DateTime haftaBaslangic, DateTime haftaSonu,
            DateTime ayBaslangic, DateTime aySonu,
            DateTime ytdStart, DateTime ytdEnd, DateTime bugun,
            DateTime fixedMonthStart, DateTime fixedMonthEnd,
            DateTime fixedYTDStart, DateTime fixedYTDEnd)
        {
            var m = new FaturaMetrics();

            // ── Fatura toplamları: UNIQUE Fatura_No bazında (mükerrer sayım önleme) ──
            // Aynı Fatura_No birden fazla satırda olabilir (farklı Durum ile) → sadece 1 kez say
            var fatNoDonem = new HashSet<string>();
            var fatNoPrev = new HashSet<string>();
            var fatNoYtd = new HashSet<string>();
            var fatNoFixedMonth = new HashSet<string>();
            var fatNoFixedYTD = new HashSet<string>();

            for (int i = 0; i < allFaturalar.Count; i++)
            {
                var f = allFaturalar[i];
                // NetTutar: Varuna KDV hariç (yoksa Excel fallback) — LoadAllCachedDataAsync'te atandı
                var tutar = f.NetTutar ?? 0m;
                var durumBos = IsDurumBos(f.Durum);
                var isTahsilat = IsTahsilatOrKrediKarti(f.Durum);

                // ── İade/İptal/Ret faturalar tamamen atlanır ──
                if (IsRetDurum(f.Durum) || IsNegatifDurum(f.Durum))
                    continue;

                // ── VarunaDışı faturalar dip toplama dahil edilmez ──
                // Sadece Varuna Closed eşleşen faturalar sayılır (sentetik dahil)
                // VarunaDışı ayrı metrikte takip edilir
                if (!f.VarunaEslesti)
                {
                    // VarunaDışı metrikleri (ayrı gösterim)
                    if (f.EfektifFaturaTarihi.HasValue)
                    {
                        var ftVd = f.EfektifFaturaTarihi.Value;
                        var fNoVd = f.Fatura_No ?? $"__vd_{i}";
                        if (ftVd >= start && ftVd <= end && fatNoDonem.Add(fNoVd))
                        {
                            m.VarunaDisiToplam += tutar;
                            m.VarunaDisiAdet++;
                        }
                    }
                    continue;
                }

                var netTutar = tutar;
                // Bakiye: Fatura_Toplam - Tahsil_Edilen (finans mantığı)
                var bakiye = (f.Fatura_Toplam ?? 0) - (f.Tahsil_Edilen ?? 0);

                // ── Fatura tarihi bazlı metrikler (unique Fatura_No bazında) ──
                if (f.EfektifFaturaTarihi.HasValue)
                {
                    var ft = f.EfektifFaturaTarihi.Value;
                    var fNo = f.Fatura_No ?? $"__row_{i}";

                    // Dönem fatura — unique Fatura_No bazında (sadece Varuna eşleşen)
                    if (ft >= start && ft <= end && fatNoDonem.Add(fNo))
                    {
                        m.FatToplam += netTutar;
                        m.FatAdet++;
                    }

                    // Önceki dönem fatura (trend) — unique
                    if (ft >= prevStart && ft <= prevEnd && fatNoPrev.Add(fNo))
                        m.PrevFatToplam += netTutar;

                    // YTD fatura gerçekleşme — unique
                    if (ft >= ytdStart && ft <= end && fatNoYtd.Add(fNo))
                        m.YtdFatGerceklesme += netTutar;

                    // Fixed month — unique
                    if (ft >= fixedMonthStart && ft <= fixedMonthEnd && fatNoFixedMonth.Add(fNo))
                        m.FixedMonthActual += netTutar;

                    // Fixed YTD — unique
                    if (ft >= fixedYTDStart && ft <= fixedYTDEnd && fatNoFixedYTD.Add(fNo))
                        m.FixedYTDActual += netTutar;
                }

                // ── Efektif tarih: Ödeme sözü varsa O, yoksa vade tarihi ──
                // İade/Ret hariç — tahsilat + vadesi geçmiş + beklenen hepsi bu tarihe göre
                // ── Tahsilat hesapları: İADE/RET hariç, Hukuki takip hariç ──
                if (!IsNegatifDurum(f.Durum) && !IsRetDurum(f.Durum) && string.IsNullOrWhiteSpace(f.Hukuki_Durum))
                {
                    var tahsil = f.Tahsil_Edilen ?? 0;
                    var bekleyenBakiye = f.Bekleyen_Bakiye ?? 0;
                    var tahsilTarihi = f.Tahsil_Tarihi;
                    var vt = f.Fatura_Vade_Tarihi;

                    // ── PAY: Tahsil_Tarihi dönemde → SUM(Tahsil_Edilen) ──
                    if (tahsilTarihi.HasValue)
                    {
                        var tt = tahsilTarihi.Value;
                        // Dönem kartı
                        if (tt >= start && tt <= end) { m.TahEdilen += tahsil; m.TahAdet++; }
                        // Geçmiş dönem tahsilat (dönem başından önce)
                        if (tt < start) m.TahGecmisTahsilat += tahsil;
                        // Önceki dönem (trend)
                        if (tt >= prevStart && tt <= prevEnd) m.PrevTahToplam += tahsil;
                        // Haftalık
                        if (tt >= haftaBaslangic && tt <= haftaSonu) m.HaftalikTah += tahsil;
                        // Aylık
                        if (tt >= ayBaslangic && tt <= aySonu) m.AylikTah += tahsil;
                        // YTD
                        if (tt >= ytdStart && tt <= ytdEnd) m.YtdTahToplam += tahsil;
                    }

                    // ── PAYDA bakiye: Fatura_Vade_Tarihi ≤ dönem sonu & bekleyenBakiye > 0 ──
                    if (vt.HasValue && bekleyenBakiye > 0)
                    {
                        if (vt.Value <= end) m.TahBakiye += bekleyenBakiye;
                        if (vt.Value <= haftaSonu) m.HaftalikBakiye += bekleyenBakiye;
                        if (vt.Value <= aySonu) m.AylikBakiye += bekleyenBakiye;
                        if (vt.Value <= ytdEnd) m.YtdBakiye += bekleyenBakiye;
                    }

                    // ── Vadesi geçmiş / beklenen: Fatura_Vade_Tarihi bazlı, sadece durum boş ──
                    if (f.Fatura_Vade_Tarihi.HasValue && bakiye > 0 && durumBos)
                    {
                        var vd = f.Fatura_Vade_Tarihi.Value;

                        if (vd >= start && vd < bugun) m.CeiDonemVgBakiye += bakiye;
                        if (vd >= ytdStart && vd < bugun) m.YtdVgBakiye += bakiye;
                        if (vd >= new DateTime(2025, 1, 1) && vd < new DateTime(2026, 1, 1)) m.Legacy2025Bakiye += bakiye;

                        if (vd < start) { m.VadesiGecmisAlacak += bakiye; m.VadesiGecmisAdet++; }
                        if (vd > bugun && vd <= end) { m.BeklenenTahsilat += bakiye; m.BeklenenAdet++; }
                    }
                }
            }

            return m;
        }

        #endregion

        #region Actions

        /// <summary>
        /// Tüm pill-nav filtrelerini tek seferde döndürür.
        /// Sayfa ilk açılırken çağrılır → window.__allFilters'a yazılır → filtre tıklamasında AJAX yok.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> PreloadAllFilters()
        {
            // Cache'i bir kez yükle — sonra 7 filtre bu cache'den hesaplanır (lock yok)
            var cached = await LoadAllCachedDataAsync(_contextFactory, _cache, _hedef);

            var filters = new[] { "month", "lastmonth", "q1", "q2", "q3", "q4", "ytd" };

            // SP çağrıları tüm dönemler için parallel başlat
            var now = DateTime.Now;
            var bugun = now.Date;
            var today = bugun.AddDays(1).AddSeconds(-1);

            // Tüm filtre dönemleri için SP task'ları topla
            var spTasks = new Dictionary<string, (Task<FaturaOzet> fat, Task<TahsilatOzet> tah, Task<SozlesmeOzet> soz, Task<List<FaturaRow>> fatRows)>();
            foreach (var f in filters)
            {
                var (s, e, _, _) = ParseFilter(f, null, null);
                spTasks[f] = (
                    _cockpitData.GetFaturaOzetAsync(s, e),
                    _cockpitData.GetTahsilatOzetAsync(s, e),
                    _cockpitData.GetSozlesmeOzetAsync(s, e),
                    _cockpitData.GetFaturalarAsync(s, e)
                );
            }

            // Sabit SP'ler (üst kartlar, CEI)
            var ayBaslangic = new DateTime(now.Year, now.Month, 1);
            var aySonu = new DateTime(now.Year, now.Month, DateTime.DaysInMonth(now.Year, now.Month), 23, 59, 59);
            var ytdStart = new DateTime(now.Year, 1, 1);
            var dayOfWeek = bugun.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)bugun.DayOfWeek - 1;
            var haftaBaslangic = bugun.AddDays(-dayOfWeek);
            var haftaSonu = haftaBaslangic.AddDays(4).AddHours(23).AddMinutes(59).AddSeconds(59);
            var gecenHaftaBaslangic = haftaBaslangic.AddDays(-7);
            var gecenHaftaSonu = gecenHaftaBaslangic.AddDays(4).AddHours(23).AddMinutes(59).AddSeconds(59);

            var spFixedMonthTask = _cockpitData.GetFaturaOzetAsync(ayBaslangic, aySonu);
            var spFixedYTDTask = _cockpitData.GetFaturaOzetAsync(ytdStart, today);
            var spTahGecenHaftaTask = _cockpitData.GetTahsilatOzetAsync(gecenHaftaBaslangic, gecenHaftaSonu);
            var spTahBuHaftaTask = _cockpitData.GetTahsilatOzetAsync(haftaBaslangic, haftaSonu);
            var spTahAylikTask = _cockpitData.GetTahsilatOzetAsync(ayBaslangic, aySonu);
            // Tahsilat YTD payda: ay sonuna kadar (Aylık ve Fatura YTD kartlarıyla simetri).
            // Pay etkilenmez (gelecek tahsilat yok), sadece "vadesi gelecek açık" payda'ya katılır.
            var spTahYillikTask = _cockpitData.GetTahsilatOzetAsync(ytdStart, aySonu);

            // Tüm SP'leri parallel bekle
            var allTasks = new List<Task>();
            foreach (var kv in spTasks.Values)
            {
                allTasks.Add(kv.fat); allTasks.Add(kv.tah); allTasks.Add(kv.soz); allTasks.Add(kv.fatRows);
            }
            allTasks.AddRange(new Task[] { spFixedMonthTask, spFixedYTDTask, spTahGecenHaftaTask, spTahBuHaftaTask, spTahAylikTask, spTahYillikTask });
            await Task.WhenAll(allTasks);

            var spFixedMonth = spFixedMonthTask.Result;
            var spFixedYTD = spFixedYTDTask.Result;
            var spTahGecenHafta = spTahGecenHaftaTask.Result;
            var spTahBuHafta = spTahBuHaftaTask.Result;
            var spTahAylik = spTahAylikTask.Result;
            var spTahYillik = spTahYillikTask.Result;

            var (allFaturalar, _, _, sozlesmeler, hedefler, _, urunGrupMap) = cached;

            decimal HedefToplam(int ayBas, int aySon) =>
                Enumerable.Range(ayBas, aySon - ayBas + 1).Sum(ay => hedefler.GetValueOrDefault(ay, 0));

            var fixedMonthTarget = hedefler.GetValueOrDefault(now.Month, 0);
            var fixedAnnualTarget = HedefToplam(1, 12);
            var currentQuarter = (now.Month - 1) / 3 + 1;
            var quarterStartMonth = (currentQuarter - 1) * 3 + 1;
            var fixedQuarterTarget = HedefToplam(quarterStartMonth, quarterStartMonth + 2);

            // Her filtre için JSON hesapla
            var dict = new Dictionary<string, object>();
            foreach (var f in filters)
            {
                var (start, end, activeFilter, _) = ParseFilter(f, null, null);
                var sp = spTasks[f];
                var spFatura = sp.fat.Result;
                var spTahsilat = sp.tah.Result;
                var spSozlesme = sp.soz.Result;
                var spFatRows = sp.fatRows.Result;

                var prevDuration = end - start;
                var prevStart = start.AddDays(-prevDuration.TotalDays);
                var prevEnd = start.AddSeconds(-1);
                var donemSonuCei = end;
                if (activeFilter == "month" || activeFilter == "lastmonth")
                    donemSonuCei = new DateTime(end.Year, end.Month, DateTime.DaysInMonth(end.Year, end.Month), 23, 59, 59);

                var m = ComputeMetrics(allFaturalar, start, end, prevStart, prevEnd,
                    donemSonuCei, haftaBaslangic, haftaSonu,
                    ayBaslangic, aySonu, ytdStart, today, bugun,
                    ayBaslangic, aySonu, ytdStart, today);

                var tahsilEdilecek = spTahsilat.BekleyenBakiyeToplam + spTahsilat.TahsilEdilen;
                var ceiDonemOran = tahsilEdilecek > 0 ? Math.Round(spTahsilat.TahsilEdilen / tahsilEdilecek * 100, 1) : 0;
                var donemHedef = HedefToplam(start.Month, end.Month);
                var hedefYuzde = donemHedef > 0 ? Math.Round(Math.Min(spFatura.Toplam / donemHedef * 100, 100), 1) : 0;

                // Ürün kırılımı
                var spFaturaNoSet = new HashSet<string>(spFatRows.Select(r => r.FaturaNo), StringComparer.OrdinalIgnoreCase);
                var urunKirilimDict = new Dictionary<string, (decimal toplam, int adet)>();
                foreach (var faturaNo in spFaturaNoSet)
                {
                    if (urunGrupMap.TryGetValue(faturaNo, out var kalemler))
                        foreach (var (grup, tlTutar) in kalemler)
                        {
                            if (urunKirilimDict.TryGetValue(grup, out var mevcut))
                                urunKirilimDict[grup] = (mevcut.toplam + tlTutar, mevcut.adet + 1);
                            else
                                urunKirilimDict[grup] = (tlTutar, 1);
                        }
                }

                dict[f] = new
                {
                    faturalarToplam = spFatura.Toplam, faturalarAdet = spFatura.Adet,
                    varunaDisiToplam = m.VarunaDisiToplam, varunaDisiAdet = m.VarunaDisiAdet,
                    tahsilatlarToplam = spTahsilat.BekleyenBakiyeToplam + spTahsilat.TahsilEdilen,
                    tahsilatEdilen = spTahsilat.TahsilEdilen, tahsilatlarAdet = spTahsilat.TahsilAdet,
                    sozlesmelerToplam = spSozlesme.YeniTutar, sozlesmelerAdet = spSozlesme.Toplam,
                    sozFaturalandiToplam = spSozlesme.FaturalandiTutar, sozFaturalandiAdet = spSozlesme.FaturalandiAdet,
                    sozKismiFatToplam = spSozlesme.KismiFaturalandiTutar, sozKismiFatAdet = spSozlesme.KismiFaturalandiAdet,
                    sozFesihToplam = spSozlesme.FesihTutar, sozFesihAdet = spSozlesme.FesihAdet, sozFesihFirmalar = spSozlesme.FesihFirmalar,
                    sozArchivedToplam = spSozlesme.ArchivedTutar, sozArchivedAdet = spSozlesme.ArchivedAdet,
                    sozYenilenenAdet = spSozlesme.YenilenenAdet,
                    sozBekleyenTutar = spSozlesme.BekleyenTutar, sozBekleyenAdet = spSozlesme.BekleyenAdet,
                    sozEskiTutar = spSozlesme.EskiTutar,
                    urunKirilim = urunKirilimDict.Select(kv => new { grup = kv.Key, toplam = kv.Value.toplam, adet = kv.Value.adet }).OrderByDescending(x => x.toplam).ToList(),
                    faturalarTrend = m.PrevFatToplam > 0 ? Math.Round((m.FatToplam - m.PrevFatToplam) / m.PrevFatToplam * 100, 1) : 0,
                    tahsilatlarTrend = m.PrevTahToplam > 0 ? Math.Round((m.TahEdilen - m.PrevTahToplam) / m.PrevTahToplam * 100, 1) : 0,
                    prevFaturalarToplam = m.PrevFatToplam, prevTahsilatlarToplam = m.PrevTahToplam,
                    ceiDonemOran, ceiDonemTahsilat = spTahsilat.TahsilEdilen, ceiDonemVadesiGecmis = m.CeiDonemVgBakiye,
                    tahsilEdilecek, tahsilKalan = spTahsilat.BekleyenBakiyeToplam,
                    gecenHaftaTah = spTahGecenHafta.TahsilEdilen, gecenHaftaBakiye = spTahGecenHafta.BekleyenBakiyeToplam,
                    gecenHaftaBaslangicStr = gecenHaftaBaslangic.ToString("dd.MM"), gecenHaftaSonuStr = gecenHaftaSonu.ToString("dd.MM.yyyy"),
                    ceiHaftalikTahsilat = spTahBuHafta.TahsilEdilen,
                    ceiHaftalikToplam = spTahBuHafta.BekleyenBakiyeToplam + spTahBuHafta.TahsilEdilen,
                    ceiHaftalikOran = (spTahBuHafta.BekleyenBakiyeToplam + spTahBuHafta.TahsilEdilen) > 0
                        ? Math.Round(spTahBuHafta.TahsilEdilen / (spTahBuHafta.BekleyenBakiyeToplam + spTahBuHafta.TahsilEdilen) * 100, 1) : 0,
                    haftaBaslangicStr = haftaBaslangic.ToString("dd.MM"), haftaSonuStr = haftaSonu.ToString("dd.MM.yyyy"),
                    ceiAylikTahsilat = spTahAylik.TahsilEdilen,
                    ceiAylikToplam = spTahAylik.BekleyenBakiyeToplam + spTahAylik.TahsilEdilen,
                    ceiAylikOran = (spTahAylik.BekleyenBakiyeToplam + spTahAylik.TahsilEdilen) > 0
                        ? Math.Round(spTahAylik.TahsilEdilen / (spTahAylik.BekleyenBakiyeToplam + spTahAylik.TahsilEdilen) * 100, 1) : 0,
                    ceiYillikTahsilat = spTahYillik.TahsilEdilen,
                    ceiYillikToplam = spTahYillik.BekleyenBakiyeToplam + spTahYillik.TahsilEdilen,
                    ceiYillikOran = (spTahYillik.BekleyenBakiyeToplam + spTahYillik.TahsilEdilen) > 0
                        ? Math.Round(spTahYillik.TahsilEdilen / (spTahYillik.BekleyenBakiyeToplam + spTahYillik.TahsilEdilen) * 100, 1) : 0,
                    legacy2025Bakiye = 0,
                    aylikHedef = donemHedef, hedefGerceklesme = spFatura.Toplam,
                    hedefKalan = Math.Max(donemHedef - spFatura.Toplam, 0), hedefYuzde,
                    fixedCurrentMonthTarget = fixedMonthTarget, fixedCurrentMonthActual = spFixedMonth.Toplam,
                    fixedCurrentMonthPct = fixedMonthTarget > 0 ? Math.Round(spFixedMonth.Toplam / fixedMonthTarget * 100, 1) : 0,
                    fixedYTDTarget = fixedAnnualTarget, fixedYTDActual = spFixedYTD.Toplam,
                    fixedYTDPct = fixedAnnualTarget > 0 ? Math.Round(spFixedYTD.Toplam / fixedAnnualTarget * 100, 1) : 0,
                    fixedQuarterTarget, currentQuarter,
                    vadesiGecmisAlacak = m.VadesiGecmisAlacak, vadesiGecmisAdet = m.VadesiGecmisAdet,
                    beklenenTahsilat = m.BeklenenTahsilat, beklenenAdet = m.BeklenenAdet,
                    tahDonemBakiye = m.TahBakiye, tahGecmisBakiye = m.VadesiGecmisAlacak,
                    tahGecmisAdet = m.VadesiGecmisAdet, tahGecmisTahsilat = m.TahGecmisTahsilat,
                    filtreBaslangic = start.ToString("dd.MM.yyyy"), filtreBitis = end.ToString("dd.MM.yyyy"),
                };
            }

            return Json(dict);
        }

        /// <summary>
        /// Tek bir filtre dönemi için AJAX JSON verisini hesaplar.
        /// Hem Index AJAX hem PreloadAllFilters tarafından kullanılır.
        /// </summary>
        // ComputeFilterJsonAsync kaldırıldı — PreloadAllFilters doğrudan inline hesaplıyor
        private static object _removed_placeholder = null!; // compile guard — sonra silinecek
        private async Task<object> _ComputeFilterJsonAsync_REMOVED(DateTime start, DateTime end, string activeFilter)
        {
            await Task.CompletedTask;
            var now = DateTime.Now;
            var bugun = now.Date;
            var today = bugun.AddDays(1).AddSeconds(-1);

            var cacheTask = LoadAllCachedDataAsync(_contextFactory, _cache, _hedef);

            var prevDuration = end - start;
            var prevStart = start.AddDays(-prevDuration.TotalDays);
            var prevEnd = start.AddSeconds(-1);

            var donemSonuCei = end;
            if (activeFilter == "month" || activeFilter == "lastmonth")
                donemSonuCei = new DateTime(end.Year, end.Month, DateTime.DaysInMonth(end.Year, end.Month), 23, 59, 59);

            var ayBaslangic = new DateTime(now.Year, now.Month, 1);
            var aySonu = new DateTime(now.Year, now.Month, DateTime.DaysInMonth(now.Year, now.Month), 23, 59, 59);
            var ytdStart = new DateTime(now.Year, 1, 1);

            var dayOfWeek = bugun.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)bugun.DayOfWeek - 1;
            var haftaBaslangic = bugun.AddDays(-dayOfWeek);
            var haftaSonu = haftaBaslangic.AddDays(4).AddHours(23).AddMinutes(59).AddSeconds(59);
            var gecenHaftaBaslangic = haftaBaslangic.AddDays(-7);
            var gecenHaftaSonu = gecenHaftaBaslangic.AddDays(4).AddHours(23).AddMinutes(59).AddSeconds(59);

            var fixedMonthStart = ayBaslangic;
            var fixedMonthEnd = aySonu;
            var fixedYTDStart = ytdStart;
            var fixedYTDEnd = today;

            // SP çağrıları — parallel
            var spFaturaTask = _cockpitData.GetFaturaOzetAsync(start, end);
            var spTahsilatTask = _cockpitData.GetTahsilatOzetAsync(start, end);
            var spSozlesmeTask = _cockpitData.GetSozlesmeOzetAsync(start, end);
            var spFixedMonthTask = _cockpitData.GetFaturaOzetAsync(ayBaslangic, aySonu);
            var spFixedYTDTask = _cockpitData.GetFaturaOzetAsync(ytdStart, today);
            var spTahGecenHaftaTask = _cockpitData.GetTahsilatOzetAsync(gecenHaftaBaslangic, gecenHaftaSonu);
            var spTahBuHaftaTask = _cockpitData.GetTahsilatOzetAsync(haftaBaslangic, haftaSonu);
            var spTahAylikTask = _cockpitData.GetTahsilatOzetAsync(ayBaslangic, aySonu);
            var spTahYillikTask = _cockpitData.GetTahsilatOzetAsync(ytdStart, aySonu);

            await Task.WhenAll(cacheTask, spFaturaTask, spTahsilatTask, spSozlesmeTask,
                spFixedMonthTask, spFixedYTDTask,
                spTahGecenHaftaTask, spTahBuHaftaTask, spTahAylikTask, spTahYillikTask);

            var (allFaturalar, urunMap, musteriMap, sozlesmeler, hedefler, varunaTutarMap, urunGrupMap) = cacheTask.Result;
            var spFatura = spFaturaTask.Result;
            var spTahsilat = spTahsilatTask.Result;
            var spSozlesme = spSozlesmeTask.Result;
            var spFixedMonth = spFixedMonthTask.Result;
            var spFixedYTD = spFixedYTDTask.Result;
            var spTahGecenHafta = spTahGecenHaftaTask.Result;
            var spTahBuHafta = spTahBuHaftaTask.Result;
            var spTahAylik = spTahAylikTask.Result;
            var spTahYillik = spTahYillikTask.Result;

            var m = ComputeMetrics(allFaturalar, start, end, prevStart, prevEnd,
                donemSonuCei, haftaBaslangic, haftaSonu,
                ayBaslangic, aySonu, ytdStart, today, bugun,
                fixedMonthStart, fixedMonthEnd, fixedYTDStart, fixedYTDEnd);

            var tahsilEdilecek = m.TahBakiye + m.TahEdilen;
            var tahsilKalan = m.TahBakiye;
            var ceiDonemTahsilat = m.TahEdilen;
            var ceiDonemOran = tahsilEdilecek > 0
                ? Math.Round(ceiDonemTahsilat / tahsilEdilecek * 100, 1) : 0;

            decimal HedefToplam(int ayBas, int aySon) =>
                Enumerable.Range(ayBas, aySon - ayBas + 1).Sum(ay => hedefler.GetValueOrDefault(ay, 0));

            var donemBasAy = start.Month;
            var donemSonAy = end.Month;
            var donemHedef = HedefToplam(donemBasAy, donemSonAy);
            var hedefKalan = Math.Max(donemHedef - spFatura.Toplam, 0);
            var hedefYuzde = donemHedef > 0
                ? Math.Round(Math.Min(spFatura.Toplam / donemHedef * 100, 100), 1) : 0;

            var fixedMonthTarget = hedefler.GetValueOrDefault(now.Month, 0);
            var fixedAnnualTarget = HedefToplam(1, 12);
            var fixedYTDTarget = fixedAnnualTarget;

            var currentQuarter = (now.Month - 1) / 3 + 1;
            var quarterStartMonth = (currentQuarter - 1) * 3 + 1;
            var fixedQuarterTarget = HedefToplam(quarterStartMonth, quarterStartMonth + 2);

            // Ürün grubu kırılımı
            var spFaturalar = await _cockpitData.GetFaturalarAsync(start, end);
            var spFaturaNoSet = new HashSet<string>(spFaturalar.Select(f => f.FaturaNo), StringComparer.OrdinalIgnoreCase);
            var urunKirilimDict = new Dictionary<string, (decimal toplam, int adet)>();
            foreach (var faturaNo in spFaturaNoSet)
            {
                if (urunGrupMap.TryGetValue(faturaNo, out var kalemler))
                {
                    foreach (var (grup, tlTutar) in kalemler)
                    {
                        if (urunKirilimDict.TryGetValue(grup, out var mevcut))
                            urunKirilimDict[grup] = (mevcut.toplam + tlTutar, mevcut.adet + 1);
                        else
                            urunKirilimDict[grup] = (tlTutar, 1);
                    }
                }
            }
            var urunKirilim = urunKirilimDict
                .Select(kv => new { grup = kv.Key, toplam = kv.Value.toplam, adet = kv.Value.adet })
                .OrderByDescending(x => x.toplam).ToList();

            return new
            {
                faturalarToplam = spFatura.Toplam,
                faturalarAdet = spFatura.Adet,
                varunaDisiToplam = m.VarunaDisiToplam,
                varunaDisiAdet = m.VarunaDisiAdet,
                tahsilatlarToplam = spTahsilat.BekleyenBakiyeToplam + spTahsilat.TahsilEdilen,
                tahsilatEdilen = spTahsilat.TahsilEdilen,
                tahsilatlarAdet = spTahsilat.TahsilAdet,
                sozlesmelerToplam = spSozlesme.YeniTutar,
                sozlesmelerAdet = spSozlesme.Toplam,
                sozArchivedToplam = spSozlesme.ArchivedTutar,
                sozArchivedAdet = spSozlesme.ArchivedAdet,
                sozYenilenenAdet = spSozlesme.YenilenenAdet,
                sozBekleyenTutar = spSozlesme.BekleyenTutar,
                sozBekleyenAdet = spSozlesme.BekleyenAdet,
                sozEskiTutar = spSozlesme.EskiTutar,
                urunKirilim,
                faturalarTrend = m.PrevFatToplam > 0 ? Math.Round((m.FatToplam - m.PrevFatToplam) / m.PrevFatToplam * 100, 1) : 0,
                tahsilatlarTrend = m.PrevTahToplam > 0 ? Math.Round((m.TahEdilen - m.PrevTahToplam) / m.PrevTahToplam * 100, 1) : 0,
                prevFaturalarToplam = m.PrevFatToplam,
                prevTahsilatlarToplam = m.PrevTahToplam,
                ceiDonemOran,
                ceiDonemTahsilat,
                ceiDonemVadesiGecmis = m.CeiDonemVgBakiye,
                tahsilEdilecek,
                tahsilKalan,
                gecenHaftaTah = spTahGecenHafta.TahsilEdilen,
                gecenHaftaBakiye = spTahGecenHafta.BekleyenBakiyeToplam,
                gecenHaftaBaslangicStr = gecenHaftaBaslangic.ToString("dd.MM"),
                gecenHaftaSonuStr = gecenHaftaSonu.ToString("dd.MM.yyyy"),
                ceiHaftalikTahsilat = spTahBuHafta.TahsilEdilen,
                ceiHaftalikToplam = spTahBuHafta.BekleyenBakiyeToplam + spTahBuHafta.TahsilEdilen,
                ceiHaftalikOran = (spTahBuHafta.BekleyenBakiyeToplam + spTahBuHafta.TahsilEdilen) > 0
                    ? Math.Round(spTahBuHafta.TahsilEdilen / (spTahBuHafta.BekleyenBakiyeToplam + spTahBuHafta.TahsilEdilen) * 100, 1) : 0,
                haftaBaslangicStr = haftaBaslangic.ToString("dd.MM"),
                haftaSonuStr = haftaSonu.ToString("dd.MM.yyyy"),
                ceiAylikTahsilat = spTahAylik.TahsilEdilen,
                ceiAylikToplam = spTahAylik.BekleyenBakiyeToplam + spTahAylik.TahsilEdilen,
                ceiAylikOran = (spTahAylik.BekleyenBakiyeToplam + spTahAylik.TahsilEdilen) > 0
                    ? Math.Round(spTahAylik.TahsilEdilen / (spTahAylik.BekleyenBakiyeToplam + spTahAylik.TahsilEdilen) * 100, 1) : 0,
                ceiYillikTahsilat = spTahYillik.TahsilEdilen,
                ceiYillikToplam = spTahYillik.BekleyenBakiyeToplam + spTahYillik.TahsilEdilen,
                ceiYillikOran = (spTahYillik.BekleyenBakiyeToplam + spTahYillik.TahsilEdilen) > 0
                    ? Math.Round(spTahYillik.TahsilEdilen / (spTahYillik.BekleyenBakiyeToplam + spTahYillik.TahsilEdilen) * 100, 1) : 0,
                legacy2025Bakiye = 0,
                aylikHedef = donemHedef,
                hedefGerceklesme = spFatura.Toplam,
                hedefKalan,
                hedefYuzde,
                fixedCurrentMonthTarget = fixedMonthTarget,
                fixedCurrentMonthActual = spFixedMonth.Toplam,
                fixedCurrentMonthPct = fixedMonthTarget > 0 ? Math.Round(spFixedMonth.Toplam / fixedMonthTarget * 100, 1) : 0,
                fixedYTDTarget,
                fixedYTDActual = spFixedYTD.Toplam,
                fixedYTDPct = fixedAnnualTarget > 0 ? Math.Round(spFixedYTD.Toplam / fixedAnnualTarget * 100, 1) : 0,
                fixedQuarterTarget,
                currentQuarter,
                vadesiGecmisAlacak = m.VadesiGecmisAlacak,
                vadesiGecmisAdet = m.VadesiGecmisAdet,
                beklenenTahsilat = m.BeklenenTahsilat,
                beklenenAdet = m.BeklenenAdet,
                tahDonemBakiye = m.TahBakiye,
                tahGecmisBakiye = m.VadesiGecmisAlacak,
                tahGecmisAdet = m.VadesiGecmisAdet,
                tahGecmisTahsilat = m.TahGecmisTahsilat,
                filtreBaslangic = start.ToString("dd.MM.yyyy"),
                filtreBitis = end.ToString("dd.MM.yyyy"),
            };
        }

        /// <summary>
        /// Cache warmer durumu — UI "güncelleme X dk önce" göstergesi için.
        /// Class-level [Authorize] devralır.
        /// </summary>
        [HttpGet]
        public IActionResult CacheStats([FromServices] SOS.Services.CockpitCacheWarmerState state)
        {
            var now = DateTime.UtcNow;
            int? ageSeconds = state.LastRefreshAt.HasValue
                ? (int)(now - state.LastRefreshAt.Value).TotalSeconds
                : null;
            return Json(new
            {
                lastRefreshAt = state.LastRefreshAt,
                lastRefreshAtLocal = state.LastRefreshAt?.ToLocalTime().ToString("HH:mm:ss"),
                ageSeconds,
                lastRefreshDurationMs = state.LastRefreshDurationMs,
                refreshCount = state.RefreshCount,
                failureCount = state.FailureCount,
                lastTaskFailures = state.LastTaskFailures,
                lastError = state.LastError,
                lastErrorAt = state.LastErrorAt
            });
        }

        /// <summary>
        /// Cache'i hemen yenile — kullanıcı UI'dan tetikler. SP cache + C# cache temizlenir,
        /// taze data DB'den çekilir. ~2-5 sn bekler, dönüş = yeni state.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CacheRefresh([FromServices] SOS.Services.CockpitCacheWarmerState state)
        {
            var startedAt = DateTime.UtcNow;
            try
            {
                _cockpitData.InvalidateAll();
                // Endpoint-seviye cache'leri de temizle (yoksa kullanıcı Yenile basınca 5dk eski veri görür)
                _cache.Remove($"Cockpit_MonthlyBreakdown_{DateTime.Now.Year}_{DateTime.Today:yyyyMMdd}");
                await LoadAllCachedDataAsync(_contextFactory, _cache, _hedef, forceRefresh: true);
                state.LastRefreshAt = DateTime.UtcNow;
                state.LastRefreshDurationMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                return Json(new { ok = true, durationMs = state.LastRefreshDurationMs, lastRefreshAt = state.LastRefreshAt });
            }
            catch (Exception ex)
            {
                state.LastError = ex.Message;
                state.LastErrorAt = DateTime.UtcNow;
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        public async Task<IActionResult> Index(string? filter, string? startDate, string? endDate)
        {
            var (start, end, activeFilter, months) = ParseFilter(filter, startDate, endDate);
            var now = DateTime.Now;
            var bugun = now.Date;
            var today = bugun.AddDays(1).AddSeconds(-1);

            // ══════════════════════════════════════════════════════════════
            // Eski cache (ürün kırılımı, müşteri eşleşme için)
            // ══════════════════════════════════════════════════════════════
            var cacheTask = LoadAllCachedDataAsync(_contextFactory, _cache, _hedef);

            // ══════════════════════════════════════════════════════════════
            // Single-pass: Tüm KPI'lar TEK döngüde hesaplanır
            // ══════════════════════════════════════════════════════════════
            var prevDuration = end - start;
            var prevStart = start.AddDays(-prevDuration.TotalDays);
            var prevEnd = start.AddSeconds(-1);

            // CEI dönem sonu: seçili filtrenin TAM dönem sonu
            // Bu ay → 30 Nisan, Geçen ay → 31 Mart, Q1 → 31 Mart, YTD → bugün
            var donemSonuCei = end;
            if (activeFilter == "month" || activeFilter == "lastmonth")
                donemSonuCei = new DateTime(end.Year, end.Month, DateTime.DaysInMonth(end.Year, end.Month), 23, 59, 59);

            var ayBaslangic = new DateTime(now.Year, now.Month, 1);
            var aySonu = new DateTime(now.Year, now.Month, DateTime.DaysInMonth(now.Year, now.Month), 23, 59, 59);
            var ytdStart = new DateTime(now.Year, 1, 1);

            // Hafta başlangıcı (Pazartesi) ve sonu (Cuma) — 5 iş günü
            var dayOfWeek = bugun.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)bugun.DayOfWeek - 1;
            var haftaBaslangic = bugun.AddDays(-dayOfWeek);
            var haftaSonu = haftaBaslangic.AddDays(4).AddHours(23).AddMinutes(59).AddSeconds(59);

            // Geçen hafta: bu haftanın Pazartesi'sinden 7 gün geri → geçen Pazartesi-Cuma
            var gecenHaftaBaslangic = haftaBaslangic.AddDays(-7);
            var gecenHaftaSonu = gecenHaftaBaslangic.AddDays(4).AddHours(23).AddMinutes(59).AddSeconds(59);

            var fixedMonthStart = ayBaslangic;
            var fixedMonthEnd = aySonu;
            var fixedYTDStart = ytdStart;
            var fixedYTDEnd = today; // Ocak 1 → bugün (Nisan dahil)

            var donemSonu = end;
            if (activeFilter == "month" || activeFilter == "lastmonth")
                donemSonu = new DateTime(end.Year, end.Month, DateTime.DaysInMonth(end.Year, end.Month), 23, 59, 59);

            // ══════════════════════════════════════════════════════════════
            // SP çağrıları — parallel (tüm kartlar + üst kartlar + tahsilat oranları)
            // ══════════════════════════════════════════════════════════════
            var spFaturaTask = _cockpitData.GetFaturaOzetAsync(start, end);
            var spTahsilatTask = _cockpitData.GetTahsilatOzetAsync(start, end);
            var spSozlesmeTask = _cockpitData.GetSozlesmeOzetAsync(start, end);
            var spFixedMonthTask = _cockpitData.GetFaturaOzetAsync(ayBaslangic, aySonu);
            var spFixedYTDTask = _cockpitData.GetFaturaOzetAsync(ytdStart, today);
            var spTahGecenHaftaTask = _cockpitData.GetTahsilatOzetAsync(gecenHaftaBaslangic, gecenHaftaSonu);
            var spTahBuHaftaTask = _cockpitData.GetTahsilatOzetAsync(haftaBaslangic, haftaSonu);
            var spTahAylikTask = _cockpitData.GetTahsilatOzetAsync(ayBaslangic, aySonu);
            var spTahYillikTask = _cockpitData.GetTahsilatOzetAsync(ytdStart, aySonu);

            await Task.WhenAll(cacheTask, spFaturaTask, spTahsilatTask, spSozlesmeTask,
                spFixedMonthTask, spFixedYTDTask,
                spTahGecenHaftaTask, spTahBuHaftaTask, spTahAylikTask, spTahYillikTask);

            var (allFaturalar, urunMap, musteriMap, sozlesmeler, hedefler, varunaTutarMap, urunGrupMap) = cacheTask.Result;
            var spFatura = spFaturaTask.Result;
            var spTahsilat = spTahsilatTask.Result;
            var spSozlesme = spSozlesmeTask.Result;
            var spFixedMonth = spFixedMonthTask.Result;
            var spFixedYTD = spFixedYTDTask.Result;
            var spTahGecenHafta = spTahGecenHaftaTask.Result;
            var spTahBuHafta = spTahBuHaftaTask.Result;
            var spTahAylik = spTahAylikTask.Result;
            var spTahYillik = spTahYillikTask.Result;

            var m = ComputeMetrics(allFaturalar, start, end, prevStart, prevEnd,
                donemSonuCei, haftaBaslangic, haftaSonu,
                ayBaslangic, aySonu, ytdStart, today, bugun,
                fixedMonthStart, fixedMonthEnd, fixedYTDStart, fixedYTDEnd);

            // Tahsilat kartı: SP'den — PAYDA = bekleyen bakiye + tahsil edilen (toplam alacak)
            var tahsilEdilecek = spTahsilat.BekleyenBakiyeToplam + spTahsilat.TahsilEdilen;  // PAYDA
            var tahsilKalan = spTahsilat.BekleyenBakiyeToplam;                    // Kalan = bekleyen bakiye

            // CEI hesapları
            var ceiDonemTahsilat = spTahsilat.TahsilEdilen;  // dönemde gerçek tahsil edilen
            var ceiDonemOran = tahsilEdilecek > 0
                ? Math.Round(ceiDonemTahsilat / tahsilEdilecek * 100, 1) : 0;
            // CEI: PAYDA = bekleyen bakiye + tahsil edilen (dönem sonuna kadar vadesi gelen tüm alacak)
            var haftalikPayda = m.HaftalikBakiye + m.HaftalikTah;
            var aylikPayda = m.AylikBakiye + m.AylikTah;
            var ytdPayda = m.YtdBakiye + m.YtdTahToplam;
            var ceiHaftalikOran = haftalikPayda > 0
                ? Math.Round(m.HaftalikTah / haftalikPayda * 100, 1) : 0;
            var ceiAylikOran = aylikPayda > 0
                ? Math.Round(m.AylikTah / aylikPayda * 100, 1) : 0;
            var ceiYillikOran = ytdPayda > 0
                ? Math.Round(m.YtdTahToplam / ytdPayda * 100, 1) : 0;

            // Hedefler — DB'den ay bazlı (TBLSOS_HEDEF_AYLIK)
            // Helper: belirli ay aralığı için hedef toplamı
            decimal HedefToplam(int ayBas, int aySon) =>
                Enumerable.Range(ayBas, aySon - ayBas + 1).Sum(ay => hedefler.GetValueOrDefault(ay, 0));

            // Dönem hedefi: filtredeki aylar
            var donemBasAy = start.Month;
            var donemSonAy = end.Month;
            var donemHedef = HedefToplam(donemBasAy, donemSonAy);
            var hedefKalan = Math.Max(donemHedef - spFatura.Toplam, 0);
            var hedefYuzde = donemHedef > 0
                ? Math.Round(Math.Min(spFatura.Toplam / donemHedef * 100, 100), 1) : 0;

            // YTD hedef: Ocak → filtrenin bitiş ayı
            var ytdAySayisi = Math.Max(1, end.Month);
            var ytdHedef = HedefToplam(1, end.Month);
            var ytdKalan = Math.Max(ytdHedef - m.YtdFatGerceklesme, 0);

            // Bu ay hedefi
            var fixedMonthTarget = hedefler.GetValueOrDefault(now.Month, 0);
            var fixedMonthPct = fixedMonthTarget > 0 ? Math.Round(m.FixedMonthActual / fixedMonthTarget * 100, 1) : 0;

            // Yıllık hedef: tüm 12 ay toplamı (₺600M)
            var fixedAnnualTarget = HedefToplam(1, 12);
            var fixedAnnualActual = m.FixedYTDActual;
            var fixedAnnualPct = fixedAnnualTarget > 0 ? Math.Round(fixedAnnualActual / fixedAnnualTarget * 100, 1) : 0;

            // Çeyrek hesabı
            var currentQuarter = (now.Month - 1) / 3 + 1;
            var quarterStartMonth = (currentQuarter - 1) * 3 + 1;
            var fixedQuarterTarget = HedefToplam(quarterStartMonth, quarterStartMonth + 2);
            var fixedQuarterMonths = now.Month - quarterStartMonth + 1;

            // Kalan ay
            var remainingMonths = 12 - now.Month;

            // Eski fixedYTD alanlarını annual ile eşle (ViewModel uyumu)
            var fixedYTDTarget = fixedAnnualTarget;
            var fixedYTDActual = fixedAnnualActual;
            var fixedYTDPct = fixedAnnualPct;

            // Sözleşmeler: seçili dönemde RenewalDate olanlar
            var sozDonem = sozlesmeler.Where(s => s.RenewalDate!.Value >= start && s.RenewalDate!.Value <= end).ToList();
            var sozToplam = sozDonem.Sum(s => s.TotalAmount ?? 0);
            var sozArchivedList = sozDonem.Where(s => string.Equals(s.ContractStatus, "Archived", StringComparison.OrdinalIgnoreCase)).ToList();
            var sozArchivedToplam = sozArchivedList.Sum(s => s.TotalAmount ?? 0);
            var sozArchivedAdet = sozArchivedList.Count;

            // Gecikmiş sözleşmeler: RenewalDate < dönem başı, hâlâ Archived değil
            var sozGecikmisList = sozlesmeler.Where(s => s.RenewalDate!.Value < start
                && !string.Equals(s.ContractStatus, "Archived", StringComparison.OrdinalIgnoreCase)).ToList();
            var sozGecikmisToplam = sozGecikmisList.Sum(s => s.TotalAmount ?? 0);
            var sozGecikmiAdet = sozGecikmisList.Count;

            // Ürün grubu kırılımı: SP fatura listesindeki FaturaNo'lar → urunGrupMap'ten kalem dağılımı
            var spFaturalar = await _cockpitData.GetFaturalarAsync(start, end);
            var spFaturaNoSet = new HashSet<string>(spFaturalar.Select(f => f.FaturaNo), StringComparer.OrdinalIgnoreCase);

            var urunKirilimDict = new Dictionary<string, (decimal toplam, int adet)>();
            foreach (var faturaNo in spFaturaNoSet)
            {
                if (urunGrupMap.TryGetValue(faturaNo, out var kalemler))
                {
                    foreach (var (grup, tlTutar) in kalemler)
                    {
                        if (urunKirilimDict.TryGetValue(grup, out var mevcut))
                            urunKirilimDict[grup] = (mevcut.toplam + tlTutar, mevcut.adet + 1);
                        else
                            urunKirilimDict[grup] = (tlTutar, 1);
                    }
                }
            }
            var urunKirilim = urunKirilimDict
                .Select(kv => new { grup = kv.Key, toplam = kv.Value.toplam, adet = kv.Value.adet })
                .OrderByDescending(x => x.toplam)
                .ToList();

            // ══════════════════════════════════════════════════════════════
            // AJAX: Sadece summary JSON döndür (detay listesi YOK)
            // ══════════════════════════════════════════════════════════════
            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                return Json(new
                {
                    // Fatura kartı — SP'den
                    faturalarToplam = spFatura.Toplam,
                    faturalarAdet = spFatura.Adet,
                    varunaDisiToplam = m.VarunaDisiToplam,
                    varunaDisiAdet = m.VarunaDisiAdet,
                    // Tahsilat kartı — SP'den
                    tahsilatlarToplam = spTahsilat.BekleyenBakiyeToplam + spTahsilat.TahsilEdilen,
                    tahsilatEdilen = spTahsilat.TahsilEdilen,
                    tahsilatlarAdet = spTahsilat.TahsilAdet,
                    // Sözleşme kartı
                    // PAY = faturalanmış (InvoiceStatusId=Tamamlandı+Kısmi), PAYDA = toplam yeni sözleşme
                    sozlesmelerToplam = spSozlesme.YeniTutar,            // PAYDA: tüm yeni sözleşme tutarı
                    sozlesmelerAdet = spSozlesme.Toplam,                 // toplam eski sözleşme sayısı
                    sozFaturalandiToplam = spSozlesme.FaturalandiTutar,
                    sozFaturalandiAdet = spSozlesme.FaturalandiAdet,
                    sozKismiFatToplam = spSozlesme.KismiFaturalandiTutar,
                    sozKismiFatAdet = spSozlesme.KismiFaturalandiAdet,
                    sozFesihToplam = spSozlesme.FesihTutar,
                    sozFesihAdet = spSozlesme.FesihAdet,
                    sozFesihFirmalar = spSozlesme.FesihFirmalar,
                    sozArchivedToplam = spSozlesme.ArchivedTutar,        // imzalanan (Archived) tutar
                    sozArchivedAdet = spSozlesme.ArchivedAdet,           // imzalanan adet
                    sozYenilenenAdet = spSozlesme.YenilenenAdet,         // tüm yenilenen sayısı
                    sozBekleyenTutar = spSozlesme.BekleyenTutar,         // yenilenmemiş eski tutar
                    sozBekleyenAdet = spSozlesme.BekleyenAdet,           // yenilenmemiş sayısı
                    sozEskiTutar = spSozlesme.EskiTutar,                 // tüm eski sözleşme tutarı
                    urunKirilim,
                    faturalarTrend = m.PrevFatToplam > 0 ? Math.Round((m.FatToplam - m.PrevFatToplam) / m.PrevFatToplam * 100, 1) : 0,
                    tahsilatlarTrend = m.PrevTahToplam > 0 ? Math.Round((m.TahEdilen - m.PrevTahToplam) / m.PrevTahToplam * 100, 1) : 0,
                    prevFaturalarToplam = m.PrevFatToplam,
                    prevTahsilatlarToplam = m.PrevTahToplam,
                    // CEI
                    ceiDonemOran,
                    ceiDonemTahsilat,
                    ceiDonemVadesiGecmis = m.CeiDonemVgBakiye,
                    tahsilEdilecek,
                    tahsilKalan,
                    // Geçen hafta (SP'den)
                    gecenHaftaTah = spTahGecenHafta.TahsilEdilen,
                    gecenHaftaBakiye = spTahGecenHafta.BekleyenBakiyeToplam,
                    gecenHaftaBaslangicStr = gecenHaftaBaslangic.ToString("dd.MM"),
                    gecenHaftaSonuStr = gecenHaftaSonu.ToString("dd.MM.yyyy"),
                    // Bu hafta (SP'den)
                    ceiHaftalikTahsilat = spTahBuHafta.TahsilEdilen,
                    ceiHaftalikToplam = spTahBuHafta.BekleyenBakiyeToplam + spTahBuHafta.TahsilEdilen,
                    ceiHaftalikOran = (spTahBuHafta.BekleyenBakiyeToplam + spTahBuHafta.TahsilEdilen) > 0
                        ? Math.Round(spTahBuHafta.TahsilEdilen / (spTahBuHafta.BekleyenBakiyeToplam + spTahBuHafta.TahsilEdilen) * 100, 1) : 0,
                    haftaBaslangicStr = haftaBaslangic.ToString("dd.MM"),
                    haftaSonuStr = haftaSonu.ToString("dd.MM.yyyy"),
                    // Aylık (SP'den)
                    ceiAylikTahsilat = spTahAylik.TahsilEdilen,
                    ceiAylikToplam = spTahAylik.BekleyenBakiyeToplam + spTahAylik.TahsilEdilen,
                    ceiAylikOran = (spTahAylik.BekleyenBakiyeToplam + spTahAylik.TahsilEdilen) > 0
                        ? Math.Round(spTahAylik.TahsilEdilen / (spTahAylik.BekleyenBakiyeToplam + spTahAylik.TahsilEdilen) * 100, 1) : 0,
                    // Yıllık (SP'den)
                    ceiYillikTahsilat = spTahYillik.TahsilEdilen,
                    ceiYillikToplam = spTahYillik.BekleyenBakiyeToplam + spTahYillik.TahsilEdilen,
                    ceiYillikOran = (spTahYillik.BekleyenBakiyeToplam + spTahYillik.TahsilEdilen) > 0
                        ? Math.Round(spTahYillik.TahsilEdilen / (spTahYillik.BekleyenBakiyeToplam + spTahYillik.TahsilEdilen) * 100, 1) : 0,
                    legacy2025Bakiye = 0,
                    // Hedef (SP'den)
                    aylikHedef = donemHedef,
                    hedefGerceklesme = spFatura.Toplam,
                    hedefKalan,
                    hedefYuzde,
                    // Üst kartlar (SP'den)
                    fixedCurrentMonthTarget = fixedMonthTarget,
                    fixedCurrentMonthActual = spFixedMonth.Toplam,
                    fixedCurrentMonthPct = fixedMonthTarget > 0 ? Math.Round(spFixedMonth.Toplam / fixedMonthTarget * 100, 1) : 0,
                    fixedYTDTarget = fixedYTDTarget,
                    fixedYTDActual = spFixedYTD.Toplam,
                    fixedYTDPct = fixedAnnualTarget > 0 ? Math.Round(spFixedYTD.Toplam / fixedAnnualTarget * 100, 1) : 0,
                    fixedQuarterTarget,
                    currentQuarter,
                    // Vadesi geçmiş & beklenen
                    vadesiGecmisAlacak = m.VadesiGecmisAlacak,
                    vadesiGecmisAdet = m.VadesiGecmisAdet,
                    beklenenTahsilat = m.BeklenenTahsilat,
                    beklenenAdet = m.BeklenenAdet,
                    // Dönem/Geçmiş bakiye (NaN fix)
                    tahDonemBakiye = m.TahBakiye,
                    tahGecmisBakiye = m.VadesiGecmisAlacak,
                    tahGecmisAdet = m.VadesiGecmisAdet,
                    tahGecmisTahsilat = m.TahGecmisTahsilat,
                    // Filtre
                    filtreBaslangic = start.ToString("dd.MM.yyyy"),
                    filtreBitis = end.ToString("dd.MM.yyyy"),
                });
            }

            // ══════════════════════════════════════════════════════════════
            // Full page: Detay listelerini hazırla (sadece ilk yükleme)
            // ══════════════════════════════════════════════════════════════
            // Tüm faturalar listede görünür (iade/iptal dahil — UI'da rozetle ayırt edilir)
            var faturalar = allFaturalar
                .Where(f => f.EfektifFaturaTarihi.HasValue && f.EfektifFaturaTarihi.Value >= start && f.EfektifFaturaTarihi.Value <= end)
                .ToList();
            MapMusteriUrun(faturalar, urunMap, musteriMap);

            // Tahsilat listesi: Fatura_Vade_Tarihi dönemde olan faturalar, iade/ret hariç
            var tahsilatlar = allFaturalar
                .Where(f => !IsNegatifDurum(f.Durum) && !IsRetDurum(f.Durum))
                .Where(f => f.Fatura_Vade_Tarihi.HasValue)
                .Where(f => f.Fatura_Vade_Tarihi!.Value >= start && f.Fatura_Vade_Tarihi!.Value <= end)
                .ToList();
            MapMusteriUrun(tahsilatlar, urunMap, musteriMap);

            // Beklenen tahsilat: Fatura_Vade_Tarihi bugün → dönem sonu, bakiye > 0
            var beklenenList = allFaturalar
                .Where(f => {
                    if (!f.Fatura_Vade_Tarihi.HasValue) return false;
                    var bakiye = (f.Fatura_Toplam ?? 0) - (f.Tahsil_Edilen ?? 0);
                    return f.Fatura_Vade_Tarihi.Value > bugun && f.Fatura_Vade_Tarihi.Value <= donemSonu && bakiye > 0;
                })
                .ToList();
            MapMusteriUrun(beklenenList, urunMap, musteriMap);

            // Kümülatif hesaplama — iade/ret faturalar komple atlanır
            var orderedFaturalar = faturalar.OrderBy(f => f.EfektifFaturaTarihi).ToList();
            decimal running = 0;
            foreach (var f in orderedFaturalar)
            {
                if (!IsRetDurum(f.Durum) && !IsNegatifDurum(f.Durum))
                    running += f.NetTutar ?? 0m;
                f.KumulatifToplam = running;
            }

            var vm = new CockpitViewModel
            {
                FaturalarToplam = spFatura.Toplam,
                FaturalarAdet = spFatura.Adet,
                VarunaDisiToplam = m.VarunaDisiToplam,
                VarunaDisiAdet = m.VarunaDisiAdet,
                TahsilatlarToplam = spTahsilat.BekleyenBakiyeToplam + spTahsilat.TahsilEdilen,
                TahsilatlarAdet = spTahsilat.TahsilAdet,
                SozlesmelerToplam = spSozlesme.YeniTutar,
                SozlesmelerAdet = spSozlesme.Toplam,
                SozFaturalandiToplam = spSozlesme.FaturalandiTutar,
                SozFaturalandiAdet = spSozlesme.FaturalandiAdet,
                SozArchivedToplam = spSozlesme.ArchivedTutar,
                SozArchivedAdet = spSozlesme.ArchivedAdet,
                SozGecikmisToplam = spSozlesme.BekleyenTutar,
                SozGecikmiAdet = spSozlesme.BekleyenAdet,
                SozKismiFatToplam = spSozlesme.KismiFaturalandiTutar,
                SozKismiFatAdet = spSozlesme.KismiFaturalandiAdet,
                SozFesihToplam = spSozlesme.FesihTutar,
                SozFesihAdet = spSozlesme.FesihAdet,
                SozFesihFirmalar = spSozlesme.FesihFirmalar,
                FaturalarTrend = m.PrevFatToplam > 0 ? Math.Round((m.FatToplam - m.PrevFatToplam) / m.PrevFatToplam * 100, 1) : 0,
                PrevFaturalarToplam = m.PrevFatToplam,
                PrevTahsilatlarToplam = m.PrevTahToplam,
                TahsilatlarTrend = m.PrevTahToplam > 0 ? Math.Round((m.TahEdilen - m.PrevTahToplam) / m.PrevTahToplam * 100, 1) : 0,
                SozlesmelerTrend = 0,
                AylikHedef = donemHedef,
                HedefTutar = donemHedef,
                HedefGerceklesme = spFatura.Toplam,
                HedefKalan = hedefKalan,
                HedefYuzde = hedefYuzde,
                HedefAySayisi = months,
                YtdHedef = ytdHedef,
                YtdGerceklesme = m.YtdFatGerceklesme,
                YtdKalan = ytdKalan,
                YtdYuzde = ytdHedef > 0 ? Math.Round(Math.Min(m.YtdFatGerceklesme / ytdHedef * 100, 100), 1) : 0,
                AktifFiltre = activeFilter,
                FiltreBaslangic = start,
                FiltreBitis = end,
                FaturaDetaylari = orderedFaturalar,
                TahsilatDetaylari = tahsilatlar.OrderByDescending(f => f.Fatura_Vade_Tarihi).ToList(),
                SozlesmeDetaylari = sozDonem.OrderByDescending(s => s.TotalAmount).ToList(),
                TahsilEdilecek = tahsilEdilecek,
                TahsilatEdilen = spTahsilat.TahsilEdilen,
                TahsilKalan = tahsilKalan,
                CeiDonemTahsilat = ceiDonemTahsilat,
                CeiDonemVadesiGecmis = m.CeiDonemVgBakiye,
                CeiDonemOran = ceiDonemOran,
                CeiHaftalikTahsilat = spTahBuHafta.TahsilEdilen,
                CeiHaftalikToplam = spTahBuHafta.BekleyenBakiyeToplam + spTahBuHafta.TahsilEdilen,
                CeiHaftalikOran = (spTahBuHafta.BekleyenBakiyeToplam + spTahBuHafta.TahsilEdilen) > 0
                    ? Math.Round(spTahBuHafta.TahsilEdilen / (spTahBuHafta.BekleyenBakiyeToplam + spTahBuHafta.TahsilEdilen) * 100, 1) : 0,
                HaftaBaslangic = haftaBaslangic,
                HaftaSonu = haftaSonu,
                CeiAylikTahsilat = spTahAylik.TahsilEdilen,
                CeiAylikToplam = spTahAylik.BekleyenBakiyeToplam + spTahAylik.TahsilEdilen,
                CeiAylikOran = (spTahAylik.BekleyenBakiyeToplam + spTahAylik.TahsilEdilen) > 0
                    ? Math.Round(spTahAylik.TahsilEdilen / (spTahAylik.BekleyenBakiyeToplam + spTahAylik.TahsilEdilen) * 100, 1) : 0,
                CeiYillikTahsilat = spTahYillik.TahsilEdilen,
                CeiYillikToplam = spTahYillik.BekleyenBakiyeToplam + spTahYillik.TahsilEdilen,
                CeiYillikOran = (spTahYillik.BekleyenBakiyeToplam + spTahYillik.TahsilEdilen) > 0
                    ? Math.Round(spTahYillik.TahsilEdilen / (spTahYillik.BekleyenBakiyeToplam + spTahYillik.TahsilEdilen) * 100, 1) : 0,
                Legacy2025Bakiye = 0,
                BeklenenTahsilat = m.BeklenenTahsilat,
                BeklenenAdet = m.BeklenenAdet,
                BeklenenDetaylari = beklenenList.OrderBy(f => f.Fatura_Vade_Tarihi).ToList(),
                VadesiGecmisAlacak = m.VadesiGecmisAlacak,
                VadesiGecmisAdet = m.VadesiGecmisAdet,
                TahDonemBakiye = m.TahBakiye,
                TahGecmisTahsilat = m.TahGecmisTahsilat,
                TahGecmisBakiye = m.VadesiGecmisAlacak,
                TahGecmisAdet = m.VadesiGecmisAdet,
                FixedCurrentMonthTarget = fixedMonthTarget,
                FixedCurrentMonthActual = spFixedMonth.Toplam,
                FixedCurrentMonthPct = fixedMonthTarget > 0 ? Math.Round(spFixedMonth.Toplam / fixedMonthTarget * 100, 1) : 0,
                FixedYTDTarget = fixedYTDTarget,
                FixedYTDActual = spFixedYTD.Toplam,
                FixedYTDPct = fixedAnnualTarget > 0 ? Math.Round(spFixedYTD.Toplam / fixedAnnualTarget * 100, 1) : 0,
                FixedQuarterTarget = fixedQuarterTarget,
                RemainingMonths = remainingMonths,
                CurrentQuarter = currentQuarter,
                GecenHaftaTah = spTahGecenHafta.TahsilEdilen,
                GecenHaftaBakiye = spTahGecenHafta.BekleyenBakiyeToplam,
                GecenHaftaBaslangicStr = gecenHaftaBaslangic.ToString("dd.MM"),
                GecenHaftaSonuStr = gecenHaftaSonu.ToString("dd.MM.yyyy"),
                UrunKirilim = urunKirilim.Select(x => new SOS.Models.ViewModels.UrunKirilimItem
                {
                    Grup = x.grup,
                    Toplam = x.toplam,
                    Adet = x.adet
                }).ToList()
            };

            return View(vm);
        }

        /// <summary>
        /// Hiyerarşik detay: Yıl → Ay → Hafta → Gün → Fatura detayları
        /// Tahsilat: + haftalık alınması gereken vs alınan
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetDetailTable(string type, string? filter, string? startDate, string? endDate, int page = 1, int pageSize = 50)
        {
            var (start, end, activeFilter, _) = ParseFilter(filter, startDate, endDate);
            var (allFaturalar, urunMap, musteriMap, sozlesmeler, hedefler, varunaTutarMap, urunGrupMap) = await LoadAllCachedDataAsync(_contextFactory, _cache, _hedef);
            var bugun = DateTime.Now.Date;

            // Hangi seviyeden başla: month/lastmonth → ay, q1-q4 → çeyrek, ytd/range → yıl
            var startLevel = activeFilter switch
            {
                "month" or "lastmonth" => "ay",
                "q1" or "q2" or "q3" or "q4" => "ceyrek",
                _ => "yil"
            };

            // ISO week hesaplama
            static int GetIsoWeek(DateTime d) => System.Globalization.ISOWeek.GetWeekOfYear(d);

            switch (type?.ToLowerInvariant())
            {
                case "faturalar":
                {
                    // SP fatura listesiyle senkron: sadece SP'de olan FaturaNo'lar
                    var spFatList = await _cockpitData.GetFaturalarAsync(start, end);
                    var spFatNoSet = new HashSet<string>(spFatList.Select(f => f.FaturaNo), StringComparer.OrdinalIgnoreCase);

                    var filtered = allFaturalar
                        .Where(f => f.Fatura_No != null && spFatNoSet.Contains(f.Fatura_No))
                        .OrderBy(f => f.EfektifFaturaTarihi)
                        .ToList();
                    MapMusteriUrun(filtered, urunMap, musteriMap);

                    // Kümülatif
                    decimal running = 0;
                    foreach (var f in filtered)
                    {
                        running += f.NetTutar ?? 0m;
                        f.KumulatifToplam = running;
                    }

                    // Net tutar helper (Varuna KDV hariç bazlı)
                    decimal FatNet(IEnumerable<VIEW_CP_EXCEL_FATURA> grp) =>
                        grp.Sum(f => f.NetTutar ?? 0m);
                    // KDV dahil toplam helper
                    decimal FatBrut(IEnumerable<VIEW_CP_EXCEL_FATURA> grp) =>
                        grp.Sum(f => f.KdvDahilTutar ?? 0m);

                    // Hiyerarşi: Yıl → Çeyrek → Ay → Hafta → Gün → Detay
                    var hierarchy = filtered
                        .GroupBy(f => f.EfektifFaturaTarihi!.Value.Year)
                        .OrderBy(y => y.Key)
                        .Select(yGrp => new
                        {
                            yil = yGrp.Key,
                            toplam = FatNet(yGrp),
                            kdvDahilToplam = FatBrut(yGrp),
                            adet = yGrp.Count(),
                            ceyrekler = yGrp
                                .GroupBy(f => (f.EfektifFaturaTarihi!.Value.Month - 1) / 3 + 1)
                                .OrderBy(q => q.Key)
                                .Select(qGrp => new
                                {
                                    ceyrek = qGrp.Key,
                                    label = qGrp.Key + ". Çeyrek",
                                    toplam = FatNet(qGrp),
                                    kdvDahilToplam = FatBrut(qGrp),
                                    adet = qGrp.Count(),
                                    aylar = qGrp
                                        .GroupBy(f => f.EfektifFaturaTarihi!.Value.Month)
                                        .OrderBy(m => m.Key)
                                        .Select(mGrp => new
                                        {
                                            ay = mGrp.Key,
                                            ayAdi = new DateTime(yGrp.Key, mGrp.Key, 1).ToString("MMMM", new System.Globalization.CultureInfo("tr-TR")),
                                            toplam = FatNet(mGrp),
                                            kdvDahilToplam = FatBrut(mGrp),
                                            adet = mGrp.Count(),
                                            haftalar = mGrp
                                                .GroupBy(f => GetIsoWeek(f.EfektifFaturaTarihi!.Value))
                                                .OrderBy(w => w.Key)
                                                .Select(wGrp => new
                                                {
                                                    hafta = wGrp.Key,
                                                    toplam = FatNet(wGrp),
                                                    kdvDahilToplam = FatBrut(wGrp),
                                                    adet = wGrp.Count(),
                                                    gunler = wGrp
                                                        .GroupBy(f => f.EfektifFaturaTarihi!.Value.Date)
                                                        .OrderBy(d => d.Key)
                                                        .Select(dGrp => new
                                                        {
                                                            tarih = dGrp.Key.ToString("dd.MM.yyyy"),
                                                            toplam = FatNet(dGrp),
                                                            kdvDahilToplam = FatBrut(dGrp),
                                                            adet = dGrp.Count(),
                                                            faturalar = dGrp.Select(f => new
                                                            {
                                                                faturaNo = f.Fatura_No,
                                                                musteri = f.MusteriUnvan,
                                                                tutar = (f.NetTutar ?? 0m),
                                                                kdvDahilTutar = (f.KdvDahilTutar ?? 0m),
                                                                kumulatif = f.KumulatifToplam,
                                                                durum = f.Durum?.Trim()?.ToUpper()
                                                            })
                                                        })
                                                })
                                        })
                                })
                        });

                    return Json(new { total = filtered.Count, dipToplam = running, startLevel, hierarchy });
                }
                case "tahsilatlar":
                {
                    // SP_COCKPIT_TAHSILAT ile aynı mantık:
                    // Direkt VIEW'den, Fatura_No dedupe, İade/Ret hariç
                    using var dbTah = _contextFactory.CreateDbContext();
                    var viewFaturalar = (await dbTah.VIEW_CP_EXCEL_FATURAs.AsNoTracking().ToListAsync())
                        .Where(f => {
                            var d2 = (f.Durum ?? "").Trim();
                            return d2 != "İADE" && d2 != "IADE" && d2 != "İPTAL" && d2 != "IPTAL" && d2 != "RET"
                                && !d2.Equals("İade Fatura", StringComparison.OrdinalIgnoreCase)
                                && !d2.Equals("Iade Fatura", StringComparison.OrdinalIgnoreCase);
                        })
                        .ToList();

                    // Vade tarihi dönemde olan faturalar — hiyerarşi Fatura_Vade_Tarihi bazlı
                    var vadeDonemdeFaturalar = viewFaturalar
                        .Where(f => f.Fatura_Vade_Tarihi.HasValue
                            && f.Fatura_Vade_Tarihi.Value >= start && f.Fatura_Vade_Tarihi.Value <= end)
                        .ToList();

                    // Tahsil edilenler: Tahsil_Edilen > 0
                    var tahsilEdilenler = vadeDonemdeFaturalar
                        .Where(f => (f.Tahsil_Edilen ?? 0) > 0)
                        .Select(f => new { fatura = f, vadeTarih = f.Fatura_Vade_Tarihi!.Value, odendi = true })
                        .ToList();

                    // Bekleyen bakiye: bakiye > 0
                    var bekleyenler = vadeDonemdeFaturalar
                        .Where(f => (f.Bekleyen_Bakiye ?? ((f.Fatura_Toplam ?? 0) - (f.Tahsil_Edilen ?? 0))) > 0
                            && !tahsilEdilenler.Any(t => t.fatura.Fatura_No == f.Fatura_No))
                        .Select(f => new { fatura = f, vadeTarih = f.Fatura_Vade_Tarihi!.Value, odendi = false })
                        .ToList();

                    var combined = tahsilEdilenler.Concat(bekleyenler)
                        .OrderBy(x => x.vadeTarih)
                        .ToList();

                    // Müşteri bilgisi: VIEW'den Ilgili_Kisi veya Varuna AccountTitle
                    var filteredFaturalar = combined.Select(x => x.fatura).ToList();
                    MapMusteriUrun(filteredFaturalar, urunMap, musteriMap);
                    // Ilgili_Kisi fallback
                    foreach (var f in filteredFaturalar)
                        if (string.IsNullOrEmpty(f.MusteriUnvan)) f.MusteriUnvan = f.Ilgili_Kisi;

                    // Haftalık hedef hesapla
                    var allOpenInvoices = allFaturalar
                        .Where(f => IsDurumBos(f.Durum) && (f.Bekleyen_Bakiye ?? (f.Fatura_Toplam ?? 0) - (f.Tahsil_Edilen ?? 0)) > 0)
                        .ToList();

                    // dipToplam SP'den — kart ile tutarlı
                    var spTahDip = await _cockpitData.GetTahsilatOzetAsync(start, end);
                    var dipToplam = spTahDip.TahsilEdilen;

                    var hierarchy = combined
                        .GroupBy(x => x.vadeTarih.Year)
                        .OrderBy(y => y.Key)
                        .Select(yGrp => new
                        {
                            yil = yGrp.Key,
                            toplam = yGrp.Where(x => x.odendi).Sum(x => x.fatura.Tahsil_Edilen ?? 0),
                            adet = yGrp.Count(),
                            ceyrekler = yGrp
                                .GroupBy(x => (x.vadeTarih.Month - 1) / 3 + 1)
                                .OrderBy(q => q.Key)
                                .Select(qGrp => new
                                {
                                    ceyrek = qGrp.Key,
                                    label = qGrp.Key + ". Çeyrek",
                                    toplam = qGrp.Where(x => x.odendi).Sum(x => x.fatura.Tahsil_Edilen ?? 0),
                                    adet = qGrp.Count(),
                                    aylar = qGrp
                                        .GroupBy(x => x.vadeTarih.Month)
                                        .OrderBy(m => m.Key)
                                        .Select(mGrp => new
                                        {
                                            ay = mGrp.Key,
                                            ayAdi = new DateTime(yGrp.Key, mGrp.Key, 1).ToString("MMMM", new System.Globalization.CultureInfo("tr-TR")),
                                            toplam = mGrp.Where(x => x.odendi).Sum(x => x.fatura.Tahsil_Edilen ?? 0),
                                            adet = mGrp.Count(),
                                            haftalar = mGrp
                                                .GroupBy(x => GetIsoWeek(x.vadeTarih))
                                                .OrderBy(w => w.Key)
                                                .Select(wGrp =>
                                                {
                                                    var anyDate = wGrp.First().vadeTarih;
                                                    var wStart = System.Globalization.ISOWeek.ToDateTime(anyDate.Year, wGrp.Key, DayOfWeek.Monday);
                                                    var wEnd = wStart.AddDays(6);
                                                    var haftaHedef = allOpenInvoices
                                                        .Where(inv => inv.Fatura_Vade_Tarihi.HasValue
                                                            && inv.Fatura_Vade_Tarihi.Value.Date >= wStart
                                                            && inv.Fatura_Vade_Tarihi.Value.Date <= wEnd)
                                                        .Sum(inv => inv.Bekleyen_Bakiye ?? ((inv.Fatura_Toplam ?? 0) - (inv.Tahsil_Edilen ?? 0)));
                                                    var alinan = wGrp.Where(x => x.odendi).Sum(x => x.fatura.Tahsil_Edilen ?? 0);

                                                    return new
                                                    {
                                                        hafta = wGrp.Key,
                                                        alinan,
                                                        alinmasiGereken = haftaHedef,
                                                        adet = wGrp.Count(),
                                                        gunler = wGrp
                                                            .GroupBy(x => x.vadeTarih.Date)
                                                            .OrderBy(d => d.Key)
                                                            .Select(dGrp => new
                                                            {
                                                                tarih = dGrp.Key.ToString("dd.MM.yyyy"),
                                                                toplam = dGrp.Where(x => x.odendi).Sum(x => x.fatura.Tahsil_Edilen ?? 0),
                                                                adet = dGrp.Count(),
                                                                faturalar = dGrp.Select(x => new
                                                                {
                                                                    faturaNo = x.fatura.Fatura_No,
                                                                    musteri = x.fatura.MusteriUnvan ?? x.fatura.Ilgili_Kisi,
                                                                    tahsilEdilen = x.fatura.Tahsil_Edilen ?? 0,
                                                                    bakiye = x.fatura.Bekleyen_Bakiye ?? ((x.fatura.Fatura_Toplam ?? 0) - (x.fatura.Tahsil_Edilen ?? 0)),
                                                                    tutar = x.odendi ? (x.fatura.Tahsil_Edilen ?? 0) : (x.fatura.Bekleyen_Bakiye ?? ((x.fatura.Fatura_Toplam ?? 0) - (x.fatura.Tahsil_Edilen ?? 0))),
                                                                    tarih = x.odendi ? x.fatura.Tahsil_Tarihi?.ToString("dd.MM.yyyy") : x.fatura.Fatura_Vade_Tarihi?.ToString("dd.MM.yyyy"),
                                                                    vadeTarihi = x.fatura.Fatura_Vade_Tarihi?.ToString("dd.MM.yyyy"),
                                                                    durum = x.fatura.Durum?.Trim()?.ToUpper(),
                                                                    odendi = x.odendi
                                                                })
                                                            })
                                                    };
                                                })
                                        })
                                })
                        });

                    return Json(new { total = combined.Count, dipToplam, startLevel, hierarchy });
                }
                case "sozlesmeler":
                {
                    // SP'den — FinishDate+1 bazlı, RelatedContractId ile yeni sözleşme
                    var spSozList = await _cockpitData.GetSozlesmelerAsync(start, end);
                    var spOzet = await _cockpitData.GetSozlesmeOzetAsync(start, end);

                    // Tarih anahtarı: Eski → Yenilemetarihi (FinishDate+1), BagsizYeni → YeniBaslangic (StartDate)
                    DateTime DonemAnahtari(SozlesmeRow s) => string.Equals(s.Tipi, "BagsizYeni", StringComparison.OrdinalIgnoreCase)
                        ? (s.YeniBaslangic ?? DateTime.Now)
                        : (s.Yenilemetarihi ?? s.EskiBitis ?? DateTime.Now);
                    decimal SatirTutari(SozlesmeRow s) => string.Equals(s.Tipi, "BagsizYeni", StringComparison.OrdinalIgnoreCase)
                        ? (s.YeniTutar ?? 0)
                        : (s.EskiTutar ?? 0);
                    bool SayilirYenilendi(SozlesmeRow s) =>
                        s.Yenilendi == 1 && string.Equals(s.YeniStatus, "Archived", StringComparison.OrdinalIgnoreCase);

                    var hierarchy = spSozList
                        .GroupBy(s => DonemAnahtari(s).Year)
                        .OrderBy(y => y.Key)
                        .Select(yGrp => new
                        {
                            yil = yGrp.Key,
                            toplam = yGrp.Sum(SatirTutari),
                            adet = yGrp.Count(),
                            archivedAdet = yGrp.Count(SayilirYenilendi),
                            ceyrekler = yGrp
                                .GroupBy(s => (DonemAnahtari(s).Month - 1) / 3 + 1)
                                .OrderBy(q => q.Key)
                                .Select(qGrp => new
                                {
                                    ceyrek = qGrp.Key,
                                    label = qGrp.Key + ". Çeyrek",
                                    toplam = qGrp.Sum(SatirTutari),
                                    adet = qGrp.Count(),
                                    archivedAdet = qGrp.Count(SayilirYenilendi),
                                    aylar = qGrp
                                        .GroupBy(s => DonemAnahtari(s).Month)
                                        .OrderBy(m => m.Key)
                                        .Select(mGrp => new
                                        {
                                            ay = mGrp.Key,
                                            ayAdi = new DateTime(yGrp.Key, mGrp.Key, 1).ToString("MMMM", new System.Globalization.CultureInfo("tr-TR")),
                                            toplam = mGrp.Sum(SatirTutari),
                                            adet = mGrp.Count(),
                                            archivedAdet = mGrp.Count(SayilirYenilendi),
                                            bagsizYeniAdet = mGrp.Count(s => string.Equals(s.Tipi, "BagsizYeni", StringComparison.OrdinalIgnoreCase)),
                                            sozlesmeler = mGrp
                                                .OrderBy(s => (s.Firma ?? string.Empty).ToUpper(new System.Globalization.CultureInfo("tr-TR")), StringComparer.Create(new System.Globalization.CultureInfo("tr-TR"), ignoreCase: false))
                                                .Select(s => new
                                            {
                                                tipi = s.Tipi,
                                                musteri = s.Firma,
                                                tanim = s.ContractName,
                                                baslangic = s.EskiBitis?.AddYears(-1).ToString("dd.MM.yyyy"),
                                                bitis = s.EskiBitis?.ToString("dd.MM.yyyy"),
                                                yenileme = s.Yenilemetarihi?.ToString("dd.MM.yyyy"),
                                                tutar = s.EskiTutar ?? 0,
                                                durum = s.ContractStatus,
                                                yenilendi = s.Yenilendi == 1,
                                                yeniTutar = s.YeniTutar,
                                                yeniDurum = s.YeniStatus,
                                                yeniBaslangic = s.YeniBaslangic?.ToString("dd.MM.yyyy"),
                                                yeniBitis = s.YeniBitis?.ToString("dd.MM.yyyy"),
                                                faturaStatu = s.FaturaStatu,
                                                eskiTip = s.EskiTip,
                                                yeniTip = s.YeniTip
                                            })
                                        })
                                })
                        });

                    return Json(new {
                        total = spOzet.Toplam,
                        dipToplam = spOzet.YeniTutar,
                        archivedToplam = spOzet.ArchivedTutar,
                        startLevel,
                        hierarchy
                    });
                }
                default:
                    return BadRequest(new { error = "Geçersiz tip" });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetDailyBreakdown(string type, string? filter, string? startDate, string? endDate)
        {
            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);
            var (allFaturalar, _, _, sozlesmeler, _, _, _) = await LoadAllCachedDataAsync(_contextFactory, _cache, _hedef);

            switch (type?.ToLowerInvariant())
            {
                case "faturalar":
                {
                    // İade/Ret tamamen atlanır
                    var daily = allFaturalar
                        .Where(f => f.EfektifFaturaTarihi.HasValue && f.EfektifFaturaTarihi.Value >= start && f.EfektifFaturaTarihi.Value <= end
                            && !IsRetDurum(f.Durum) && !IsNegatifDurum(f.Durum))
                        .GroupBy(f => f.EfektifFaturaTarihi!.Value.Date)
                        .Select(g => new
                        {
                            tarih = g.Key.ToString("yyyy-MM-dd"),
                            toplam = g.Sum(x => x.NetTutar ?? 0),
                            adet = g.Count()
                        })
                        .OrderBy(x => x.tarih).ToList();
                    return Json(daily);
                }
                case "tahsilatlar":
                {
                    var daily = allFaturalar
                        .Where(f => f.Fatura_Vade_Tarihi.HasValue && IsTahsilatOrKrediKarti(f.Durum))
                        .Where(f => f.Fatura_Vade_Tarihi!.Value >= start && f.Fatura_Vade_Tarihi!.Value <= end)
                        .GroupBy(f => f.Fatura_Vade_Tarihi!.Value.Date)
                        .Select(g => new { tarih = g.Key.ToString("yyyy-MM-dd"), toplam = g.Sum(x => x.Fatura_Toplam ?? 0), adet = g.Count() })
                        .OrderBy(x => x.tarih).ToList();
                    return Json(daily);
                }
                case "sozlesmeler":
                {
                    var daily = sozlesmeler.Where(s => s.CreatedOn.HasValue)
                        .GroupBy(s => s.CreatedOn!.Value.Date)
                        .Select(g => new { tarih = g.Key.ToString("yyyy-MM-dd"), toplam = g.Sum(x => x.TotalAmount ?? 0), adet = g.Count() })
                        .OrderBy(x => x.tarih).ToList();
                    return Json(daily);
                }
                default:
                    return BadRequest(new { error = "Geçersiz tip" });
            }
        }

        /// <summary>
        /// Aylık Fatura & Tahsilat Dağılımı — 12 ay (Oca-Ara).
        /// SP verisi kullanır — kartlarla birebir tutarlı.
        /// Fatura = SP_COCKPIT_FATURA, Tahsilat = SP_COCKPIT_TAHSILAT, Vade = allFaturalar (Fatura_Vade_Tarihi).
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMonthlyBreakdown()
        {
            var year = DateTime.Now.Year;
            var trAylar = new[] { "", "Oca", "Şub", "Mar", "Nis", "May", "Haz", "Tem", "Ağu", "Eyl", "Eki", "Kas", "Ara" };

            // ⚡ Perf: Aylık özet 5dk cache. 12 ay × 2 SP = 24 paralel çağrıydı (cold ~9s).
            // Cache hit ile ms döner. SP cache'i 5dk TTL ile uyumlu — veri tazeliği aynı.
            var monthlyCacheKey = $"Cockpit_MonthlyBreakdown_v4_{year}_{DateTime.Today:yyyyMMdd}";
            if (_cache.TryGetValue(monthlyCacheKey, out object? cachedMonthly) && cachedMonthly != null)
                return Json(cachedMonthly);

            // 12 ay için SP fatura + tahsilat parallel çağır (SP cache'den ~1ms × 12)
            var fatTasks = new Dictionary<int, Task<FaturaOzet>>();
            var tahTasks = new Dictionary<int, Task<TahsilatOzet>>();
            for (int m = 1; m <= 12; m++)
            {
                var s = new DateTime(year, m, 1);
                var e = new DateTime(year, m, DateTime.DaysInMonth(year, m), 23, 59, 59);
                fatTasks[m] = _cockpitData.GetFaturaOzetAsync(s, e);
                tahTasks[m] = _cockpitData.GetTahsilatOzetAsync(s, e);
            }

            var allTasks = new List<Task>();
            allTasks.AddRange(fatTasks.Values);
            allTasks.AddRange(tahTasks.Values);
            await Task.WhenAll(allTasks);

            // Vade Tarihi: SP_COCKPIT_TAHSILAT'taki VadesiGelenToplam/Adet kullanılır.
            // SP iade/ret/iptal filtresini DB seviyesinde uygular (`NOT IN` list, deduplicated).
            // Eski C# yolu `vadeByMonth` IsRetDurum/IsNegatifDurum filtresini in-memory uyguluyordu
            // ama VIEW'deki duplicate kayıtlar + filtre lambda capture sorunu → 192 adet / ₺67.34M
            // (gerçek SP çıktısı: 160 / ₺55.27M). 2026-05-13'te SP'ye delegate edildi.
            // Gelecek aylar (faturaAdet=0) için bekleyen alanlar NULL döner → grafikte boşluk.
            // Aksi takdirde kümülatif bekleyen yıl sonuna kadar düz çizgi olur, görsel anlamsız.
            var thisMonth = DateTime.Today.Month;
            var result = Enumerable.Range(1, 12).Select(m =>
            {
                var fat = fatTasks[m].Result;
                var tah = tahTasks[m].Result;
                bool gelecekAy = m > thisMonth;
                return new
                {
                    ay = m,
                    ayAd = trAylar[m],
                    faturaToplam = fat.Toplam,
                    faturaAdet = fat.Adet,
                    tahsilatToplam = tah.TahsilEdilen,
                    tahsilatAdet = tah.TahsilAdet,
                    vadeToplam = tah.VadesiGelenToplam,
                    vadeAdet = tah.VadesiGelenAdet,
                    // O ay vadeli + hâlâ bekleyen bakiye (sadece bu ayın net açık alacağı)
                    oAyBekleyenToplam = gelecekAy ? (decimal?)null : tah.OAyBekleyenToplam,
                    oAyBekleyenAdet = gelecekAy ? (int?)null : tah.OAyBekleyenAdet,
                    // Kümülatif bekleyen bakiye (vade <= ay sonu — geçmişten kalan dahil tüm açık alacak)
                    kumulatifBekleyenToplam = gelecekAy ? (decimal?)null : tah.BekleyenBakiyeToplam,
                };
            }).ToList();

            _cache.Set(monthlyCacheKey, result, TimeSpan.FromMinutes(5));
            return Json(result);
        }

        [HttpGet]
        public async Task<IActionResult> GetKalemDetay(string faturaNo)
        {
            if (string.IsNullOrEmpty(faturaNo))
                return BadRequest(new { error = "Fatura no gerekli" });

            using var db = _contextFactory.CreateDbContext();

            // Fatura_No → SerialNumber → OrderId + TotalNetAmount (TL bazlı)
            var siparis = await db.TBL_VARUNA_SIPARIs
                .AsNoTracking()
                .Where(s => s.SerialNumber == faturaNo && s.DeletedOn == null)
                .Select(s => new { s.OrderId, s.TotalNetAmount })
                .FirstOrDefaultAsync();

            if (siparis?.OrderId == null)
                return Json(new List<object>());

            var kalemler = await db.TBL_VARUNA_SIPARIS_URUNLERIs
                .AsNoTracking()
                .Where(u => u.CrmOrderId == siparis.OrderId)
                .Select(u => new
                {
                    UrunAdi = u.ProductName,
                    StokKodu = u.StockCode,
                    Miktar = u.Quantity,
                    BirimFiyat = u.UnitPrice,
                    DovizToplam = u.Total,
                    KDV = u.Tax
                })
                .ToListAsync();

            // Kalem bazlı TL dağılımı: s.TotalNetAmount'ı kalemlerin döviz oranına göre dağıt
            var dovizGenel = kalemler.Sum(k => k.DovizToplam ?? 0);
            var tlNet = siparis.TotalNetAmount ?? 0;

            var urunler = kalemler.Select(k =>
            {
                var oran = dovizGenel != 0 ? (k.DovizToplam ?? 0) / dovizGenel : 0;
                var tlToplam = tlNet * oran;
                return new
                {
                    k.UrunAdi,
                    k.StokKodu,
                    k.Miktar,
                    BirimFiyat = k.Miktar > 0 ? tlToplam / (decimal)k.Miktar : 0,
                    Toplam = tlToplam,
                    k.KDV
                };
            }).ToList();

            return Json(urunler);
        }


        // ═══ DEBUG: Departman ve Ürün listesi + StockCode eşleşme ═══
        [HttpGet]
        public async Task<IActionResult> GetDepartmanUrunList()
        {
            using var db = _contextFactory.CreateDbContext();

            // 1) Faturalardaki benzersiz Proje (departman) isimleri
            var projeler = await db.VIEW_CP_EXCEL_FATURAs
                .AsNoTracking()
                .Where(f => f.Proje != null && f.Proje != "")
                .Select(f => f.Proje!)
                .Distinct()
                .OrderBy(p => p)
                .ToListAsync();

            // 2) VIEW_ORTAK_PROJE_ISIMLERI (master departman listesi)
            var ortakProjeler = await db.VIEW_ORTAK_PROJE_ISIMLERIs
                .AsNoTracking()
                .Where(p => p.TXTORTAKPROJEADI != null)
                .Select(p => new { p.LNGKOD, p.TXTORTAKPROJEADI, p.DURUM })
                .OrderBy(p => p.TXTORTAKPROJEADI)
                .ToListAsync();

            // 3) Ürün grupları
            var urunGruplari = await db.TBL_VARUNA_URUN_GRUPLAMAs
                .AsNoTracking()
                .Select(u => new { u.LNGKOD, u.TXTURUNGRUP, u.TXTURUNMASK, u.TXTKOD })
                .OrderBy(u => u.TXTURUNGRUP)
                .ToListAsync();

            // 4) Sipariş ürünleri (StockCode + ProductName)
            var siparisUrunleri = await db.TBL_VARUNA_SIPARIS_URUNLERIs
                .AsNoTracking()
                .Where(u => u.StockCode != null && u.StockCode != "")
                .Select(u => new { u.StockCode, u.ProductName })
                .Distinct()
                .OrderBy(u => u.StockCode)
                .ToListAsync();

            // 5) StockCode → Ürün grubu mask eşleştirme
            var gruplama = urunGruplari
                .Where(g => g.TXTURUNMASK != null && g.TXTURUNMASK != "")
                .OrderByDescending(g => (g.TXTURUNMASK ?? "").Length)
                .ToList();

            var stockMapping = siparisUrunleri.Select(u =>
            {
                var match = gruplama.FirstOrDefault(g =>
                    u.StockCode!.Trim().StartsWith(g.TXTURUNMASK!.Trim(), StringComparison.OrdinalIgnoreCase));
                return new
                {
                    StockCode = (u.StockCode ?? "").Trim(),
                    ProductName = (u.ProductName ?? "").Trim(),
                    MatchedMask = match?.TXTURUNMASK?.Trim(),
                    UrunGrubu = match?.TXTURUNGRUP?.Trim(),
                    Matched = match != null
                };
            }).ToList();

            return Json(new
            {
                faturaDepartmanlari = projeler,
                masterDepartmanlar = ortakProjeler,
                urunGruplari = urunGruplari,
                stockMapping = new
                {
                    toplam = stockMapping.Count,
                    eslesenAdet = stockMapping.Count(s => s.Matched),
                    eslesmeyenAdet = stockMapping.Count(s => !s.Matched),
                    eslesen = stockMapping.Where(s => s.Matched)
                        .GroupBy(s => s.UrunGrubu)
                        .Select(g => new
                        {
                            urunGrubu = g.Key,
                            adet = g.Count(),
                            ornekler = g.Take(5).Select(x => new { x.StockCode, x.ProductName, x.MatchedMask })
                        }),
                    eslesmeyen = stockMapping.Where(s => !s.Matched)
                        .Select(x => new { x.StockCode, x.ProductName })
                }
            });
        }

        [HttpGet]
        public async Task<IActionResult> GetUrunKategoriExcel()
        {
            using var db = _contextFactory.CreateDbContext();

            var siparisUrunleri = await db.TBL_VARUNA_SIPARIS_URUNLERIs
                .AsNoTracking()
                .Where(u => u.StockCode != null && u.StockCode != "")
                .Select(u => new { StockCode = u.StockCode!.Trim(), ProductName = (u.ProductName ?? "").Trim() })
                .Distinct()
                .OrderBy(u => u.StockCode)
                .ToListAsync();

            var gruplama = await db.TBL_VARUNA_URUN_GRUPLAMAs
                .AsNoTracking()
                .Where(g => g.TXTURUNMASK != null && g.TXTURUNMASK != "")
                .OrderByDescending(g => g.TXTURUNMASK!.Length)
                .ToListAsync();

            // Güncel kategori map (UH → E-Dönüşüm)
            var kategoriMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["EY"] = "Enroute", ["EH"] = "Enroute", ["EYS"] = "Enroute",
                ["SH"] = "Stokbar", ["SH.01"] = "Stokbar", ["SY"] = "Stokbar",
                ["QY"] = "Quest", ["QH"] = "Quest", ["QMH"] = "Quest", ["QYS"] = "Quest",
                ["CDY"] = "ServiceCore", ["CDH"] = "ServiceCore",
                ["VY"] = "Varuna", ["VH"] = "Varuna",
                ["OH"] = "Hosting", ["WPH"] = "Hosting", ["WPY"] = "Hosting",
                ["SM"] = "E-Dönüşüm", ["SMY"] = "E-Dönüşüm", ["SMH"] = "E-Dönüşüm", ["UH"] = "E-Dönüşüm",
                ["PP"] = "BFG",
                ["zzzUH"] = "E-Dönüşüm",
            };

            var rows = siparisUrunleri.Select(u =>
            {
                var maskMatch = gruplama.FirstOrDefault(g =>
                    u.StockCode.StartsWith(g.TXTURUNMASK!.Trim(), StringComparison.OrdinalIgnoreCase));
                var mask = maskMatch?.TXTURUNMASK?.Trim() ?? "";
                var grupTip = maskMatch?.TXTURUNGRUP?.Trim() ?? "";
                kategoriMap.TryGetValue(mask, out var anaUrun);
                return new { AnaUrun = anaUrun ?? "Eşleşmedi", Mask = mask, YazilimHizmet = grupTip, StokKodu = u.StockCode, UrunAdi = u.ProductName };
            })
            .OrderBy(r => r.AnaUrun).ThenBy(r => r.Mask).ThenBy(r => r.StokKodu)
            .ToList();

            // CSV üret
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Ana Ürün\tMask\tYazılım/Hizmet\tStok Kodu\tÜrün Açıklaması");
            foreach (var r in rows)
                sb.AppendLine($"{r.AnaUrun}\t{r.Mask}\t{r.YazilimHizmet}\t{r.StokKodu}\t{r.UrunAdi}");

            var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return File(bytes, "text/csv; charset=utf-8", "urun_kategori_eslestirme.csv");
        }

        /// <summary>
        /// Tanı: SP_COCKPIT_SOZLESME dönüşünde Tipi='BagsizYeni' kaç satır geliyor?
        /// Kullanım: /Cockpit/DiagBagsizYeni?start=2026-04-01&end=2026-04-30
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> DiagBagsizYeni(string? start, string? end)
        {
            // Cache'i bypass et — SP'yi direkt çağır
            var s = !string.IsNullOrEmpty(start) ? DateTime.Parse(start) : new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
            var e = !string.IsNullOrEmpty(end) ? DateTime.Parse(end) : new DateTime(s.Year, s.Month, DateTime.DaysInMonth(s.Year, s.Month));

            using var db = _contextFactory.CreateDbContext();
            db.Database.SetCommandTimeout(60);
            var rows = await db.Database.SqlQueryRaw<SozlesmeRow>(
                "EXEC SP_COCKPIT_SOZLESME @p0, @p1", s, e).ToListAsync();

            var bagsiz = rows.Where(r => string.Equals(r.Tipi, "BagsizYeni", StringComparison.OrdinalIgnoreCase)).ToList();
            var eski = rows.Where(r => !string.Equals(r.Tipi, "BagsizYeni", StringComparison.OrdinalIgnoreCase)).ToList();

            // Ham DB sorgusu — RelatedContractId NULL olan dönemde başlayan tüm sözleşmeler (deleted dahil/hariç)
            var hamSorgu = await db.Database.SqlQueryRaw<DiagSozlesmeDto>(@"
SELECT
    CAST(s.Id AS NVARCHAR(64)) AS Id,
    s.AccountTitle AS AccountTitle,
    s.ContractNo AS ContractNo,
    s.ContractStatus AS ContractStatus,
    s.StartDate AS StartDate,
    s.FinishDate AS FinishDate,
    CAST(s.RelatedContractId AS NVARCHAR(64)) AS RelatedContractId,
    s.TotalAmount AS TotalAmount,
    CAST(s.DeletedOn AS NVARCHAR(50)) AS DeletedOn
FROM TBL_VARUNA_SOZLESME s
WHERE s.RelatedContractId IS NULL
  AND s.StartDate >= {0} AND s.StartDate < DATEADD(DAY, 1, {1})
ORDER BY s.StartDate DESC", s, e).ToListAsync();

            return Json(new
            {
                donem = new { baslangic = s.ToString("yyyy-MM-dd"), bitis = e.ToString("yyyy-MM-dd") },
                spOzet = new
                {
                    toplamSatir = rows.Count,
                    eskiAdet = eski.Count,
                    bagsizYeniAdet = bagsiz.Count,
                    bagsizYeniler = bagsiz.Select(b => new
                    {
                        b.Id,
                        firma = b.Firma,
                        yeniTutar = b.YeniTutar,
                        yeniBaslangic = b.YeniBaslangic?.ToString("yyyy-MM-dd"),
                        yeniBitis = b.YeniBitis?.ToString("yyyy-MM-dd"),
                        yeniStatus = b.YeniStatus,
                        faturaStatu = b.FaturaStatu
                    })
                },
                hamSorgu = new
                {
                    toplam = hamSorgu.Count,
                    silinmemis = hamSorgu.Count(x => string.IsNullOrEmpty(x.DeletedOn)),
                    silinmis = hamSorgu.Count(x => !string.IsNullOrEmpty(x.DeletedOn)),
                    ornekler = hamSorgu.Take(20).ToList()
                }
            });
        }

        private class DiagSozlesmeDto
        {
            public string? Id { get; set; }
            public string? AccountTitle { get; set; }
            public string? ContractNo { get; set; }
            public string? ContractStatus { get; set; }
            public DateTime? StartDate { get; set; }
            public DateTime? FinishDate { get; set; }
            public string? RelatedContractId { get; set; }
            public decimal? TotalAmount { get; set; }
            public string? DeletedOn { get; set; }
        }

        [HttpGet]
        public async Task<IActionResult> SapLookup(string sapNos)
        {
            using var db = _contextFactory.CreateDbContext();
            var sapList = sapNos.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();

            var siparisler = await db.TBL_VARUNA_SIPARIs.AsNoTracking()
                .Where(s => s.SAPOutReferenceCode != null && s.DeletedOn == null)
                .Select(s => new { s.OrderId, s.SerialNumber, s.SAPOutReferenceCode, s.OrderStatus, s.InvoiceDate, s.AccountTitle, s.TotalNetAmount })
                .ToListAsync();

            var results = new List<object>();
            foreach (var sap in sapList)
            {
                var matches = siparisler.Where(s => s.SAPOutReferenceCode != null && s.SAPOutReferenceCode.Trim().Contains(sap)).ToList();
                results.Add(new { sap, eslesen = matches.Count, detay = matches.Select(m => new { m.OrderId, m.SerialNumber, m.SAPOutReferenceCode, m.OrderStatus, m.InvoiceDate, m.AccountTitle, m.TotalNetAmount }).ToList() });
            }
            return Json(results);
        }

        /// <summary>
        /// VIEW'de olup Varuna'da olmayan faturaları Varuna'ya ekler (SAP bazlı).
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> AddMissingVaruna([FromBody] List<MissingSiparisDto> items)
        {
            if (items == null || items.Count == 0)
                return Json(new { ok = false, error = "Liste boş" });
            using var db = _contextFactory.CreateDbContext();
            int eklenen = 0;
            foreach (var item in items)
            {
                // Zaten var mı kontrol
                var exists = await db.TBL_VARUNA_SIPARIs.AnyAsync(s =>
                    s.SAPOutReferenceCode == item.SapNo || s.SerialNumber == item.SerialNumber);
                if (exists) continue;

                db.TBL_VARUNA_SIPARIs.Add(new Models.MsK.TBL_VARUNA_SIPARI
                {
                    OrderId = item.OrderId ?? Guid.NewGuid().ToString(),
                    SerialNumber = item.SerialNumber,
                    SAPOutReferenceCode = item.SapNo,
                    OrderStatus = "Closed",
                    TotalNetAmount = item.TotalNetAmount,
                    AccountTitle = item.AccountTitle,
                    InvoiceDate = item.InvoiceDate,
                    CreateOrderDate = item.InvoiceDate,
                    CreatedOn = DateTime.Now
                });
                eklenen++;
            }
            await db.SaveChangesAsync();
            // Cache invalidate
            _cache.Remove(CACHE_KEY_FATURALAR);
            _cache.Remove(CACHE_KEY_VARUNA_TUTAR);
            return Json(new { ok = true, eklenen });
        }
        public class MissingSiparisDto
        {
            public string? OrderId { get; set; }
            public string SerialNumber { get; set; } = "";
            public string SapNo { get; set; } = "";
            public decimal TotalNetAmount { get; set; }
            public string? AccountTitle { get; set; }
            public DateTime? InvoiceDate { get; set; }
        }

        /// <summary>Geçici debug: Sözleşme InvoiceStatusId dağılımı</summary>
        [HttpGet]
        public async Task<IActionResult> SozlesmeFaturaDurum(string? filter, string? startDate, string? endDate)
        {
            using var db = _contextFactory.CreateDbContext();

            // 2026 yenileme sözleşmeleri — RenewalDate bazlı, ContractStatus dağılımı
            var yenileme = await db.TBL_VARUNA_SOZLESMEs.AsNoTracking()
                .Where(s => s.RenewalDate.HasValue && s.RenewalDate.Value.Year == 2026 && s.DeletedOn == null)
                .OrderBy(s => s.RenewalDate).ThenBy(s => s.AccountTitle)
                .ToListAsync();

            // ContractStatus özet
            var ozet = yenileme
                .GroupBy(s => s.ContractStatus ?? "Belirsiz")
                .Select(g => new { status = g.Key, adet = g.Count(), tutar = g.Sum(s => s.TotalAmount ?? 0) })
                .OrderByDescending(x => x.adet).ToList();

            // Ay + ContractStatus detay
            var trAylar = new[] { "", "Oca", "Şub", "Mar", "Nis", "May", "Haz", "Tem", "Ağu", "Eyl", "Eki", "Kas", "Ara" };
            var detay = yenileme.Select(s => new {
                ay = s.RenewalDate!.Value.Month,
                ayAd = trAylar[s.RenewalDate!.Value.Month],
                firma = s.AccountTitle,
                contractNo = s.ContractNo,
                contractStatus = s.ContractStatus,
                invoiceStatusId = s.InvoiceStatusId?.ToString(),
                invoiceNumber = s.InvoiceNumber,
                tutar = s.TotalAmount ?? 0,
                kalanBakiye = s.RemainingBalance ?? 0
            }).ToList();

            return Json(new { toplam = yenileme.Count, ozet, detay });
        }

        /// <summary>Geçici: SP güncelle + cache temizle + tam diagnostik</summary>
        [HttpGet]
        public async Task<IActionResult> RefreshSP([FromServices] ICockpitDataService cockpitData)
        {
            using var db = _contextFactory.CreateDbContext();
            db.Database.SetCommandTimeout(120);

            // 1) SP'nin DB'deki mevcut durumunu kontrol et
            var spDefBefore = await db.Database.SqlQueryRaw<SpDefCheck>(
                "SELECT CASE WHEN OBJECT_DEFINITION(OBJECT_ID('SP_COCKPIT_TAHSILAT')) LIKE '%ISNULL(Fatura_Toplam%' THEN 'FALLBACK_VAR' ELSE 'FALLBACK_YOK' END AS Durum").FirstOrDefaultAsync();

            // 2) SP'yi fallback'li versiyonla güncelle
            await db.Database.ExecuteSqlRawAsync(@"
CREATE OR ALTER PROCEDURE SP_COCKPIT_TAHSILAT
    @StartDate DATE,
    @EndDate   DATE
AS
BEGIN
    SET NOCOUNT ON;
    ;WITH DistinctFatura AS (
        SELECT *, CASE WHEN Fatura_No IS NULL THEN 1
            ELSE ROW_NUMBER() OVER (PARTITION BY Fatura_No ORDER BY (SELECT NULL)) END AS rn
        FROM VIEW_CP_EXCEL_FATURA
    ),
    Faturalar AS (
        SELECT Fatura_No, Fatura_Tarihi, Fatura_Toplam, Durum,
               Fatura_Vade_Tarihi, Tahsil_Edilen, Bekleyen_Bakiye, Tahsil_Tarihi
        FROM DistinctFatura
        WHERE rn = 1
          AND ISNULL(LTRIM(RTRIM(Hukuki_Durum)), '') = ''
          AND LTRIM(RTRIM(ISNULL(Durum,''))) NOT IN (N'İADE',N'IADE',N'İPTAL',N'IPTAL',N'RET',N'İade Fatura',N'Iade Fatura')
    )
    SELECT
        ISNULL(SUM(CASE WHEN Tahsil_Tarihi >= @StartDate AND Tahsil_Tarihi < DATEADD(DAY,1,@EndDate)
                        THEN ISNULL(Tahsil_Edilen, 0) END), 0) AS TahsilEdilen,
        ISNULL(SUM(CASE WHEN Tahsil_Tarihi >= @StartDate AND Tahsil_Tarihi < DATEADD(DAY,1,@EndDate)
                        THEN 1 END), 0) AS TahsilAdet,
        -- PAYDA bakiye: as-of @EndDate snapshot (donem sonrasi tahsilatlar geri eklenir)
        ISNULL(SUM(CASE WHEN Fatura_Vade_Tarihi <= @EndDate
                        THEN ISNULL(Bekleyen_Bakiye, ISNULL(Fatura_Toplam,0) - ISNULL(Tahsil_Edilen,0))
                           + CASE WHEN Tahsil_Tarihi > @EndDate THEN ISNULL(Tahsil_Edilen, 0) ELSE 0 END
                        END), 0) AS BekleyenBakiyeToplam,
        ISNULL(SUM(CASE WHEN Fatura_Vade_Tarihi >= @StartDate AND Fatura_Vade_Tarihi < DATEADD(DAY,1,@EndDate)
                        THEN ISNULL(Fatura_Toplam, 0) END), 0) AS VadesiGelenToplam,
        ISNULL(SUM(CASE WHEN Fatura_Vade_Tarihi >= @StartDate AND Fatura_Vade_Tarihi < DATEADD(DAY,1,@EndDate)
                        THEN 1 END), 0) AS VadesiGelenAdet
    FROM Faturalar;
END;");

            // 3) Cache temizle
            cockpitData.InvalidateAll();

            // 4) SP'nin yeni durumunu kontrol et
            var spDefAfter = await db.Database.SqlQueryRaw<SpDefCheck>(
                "SELECT CASE WHEN OBJECT_DEFINITION(OBJECT_ID('SP_COCKPIT_TAHSILAT')) LIKE '%ISNULL(Fatura_Toplam%' THEN 'FALLBACK_VAR' ELSE 'FALLBACK_YOK' END AS Durum").FirstOrDefaultAsync();

            // 5) Diagnostik: Nisan + YTD sonuçlarını SP'den çek
            var spNisan = (await db.Database.SqlQueryRaw<TahsilatOzet>(
                "EXEC SP_COCKPIT_TAHSILAT @p0, @p1",
                new DateTime(2026, 4, 1), new DateTime(2026, 4, 30)).ToListAsync()).FirstOrDefault();
            var spYtd = (await db.Database.SqlQueryRaw<TahsilatOzet>(
                "EXEC SP_COCKPIT_TAHSILAT @p0, @p1",
                new DateTime(2026, 1, 1), DateTime.Today).ToListAsync()).FirstOrDefault();

            // 6) Ham veri analizi: VIEW'deki toplam kayıt ve filtre etkisi
            var hamToplam = await db.VIEW_CP_EXCEL_FATURAs.AsNoTracking().CountAsync();
            var hamBakiyeli = await db.VIEW_CP_EXCEL_FATURAs.AsNoTracking()
                .Where(f => (f.Bekleyen_Bakiye ?? 0) > 0).CountAsync();
            var hamBakiyeliToplam = await db.VIEW_CP_EXCEL_FATURAs.AsNoTracking()
                .Where(f => (f.Bekleyen_Bakiye ?? 0) > 0)
                .SumAsync(f => f.Bekleyen_Bakiye ?? 0);

            // Bekleyen_Bakiye NULL ama Fatura_Toplam - Tahsil_Edilen > 0 olanlar (fallback farkı)
            var nullBakiyeFallback = await db.VIEW_CP_EXCEL_FATURAs.AsNoTracking()
                .Where(f => f.Bekleyen_Bakiye == null
                    && (f.Fatura_Toplam ?? 0) - (f.Tahsil_Edilen ?? 0) > 0)
                .CountAsync();
            var nullBakiyeFallbackToplam = (await db.VIEW_CP_EXCEL_FATURAs.AsNoTracking()
                .Where(f => f.Bekleyen_Bakiye == null
                    && (f.Fatura_Toplam ?? 0) - (f.Tahsil_Edilen ?? 0) > 0)
                .ToListAsync())
                .Sum(f => (f.Fatura_Toplam ?? 0) - (f.Tahsil_Edilen ?? 0));

            // Hukuki filtresi etkisi
            var hukukiAdet = await db.VIEW_CP_EXCEL_FATURAs.AsNoTracking()
                .Where(f => !string.IsNullOrWhiteSpace(f.Hukuki_Durum) && (f.Bekleyen_Bakiye ?? 0) > 0)
                .CountAsync();
            var hukukiToplam = await db.VIEW_CP_EXCEL_FATURAs.AsNoTracking()
                .Where(f => !string.IsNullOrWhiteSpace(f.Hukuki_Durum) && (f.Bekleyen_Bakiye ?? 0) > 0)
                .SumAsync(f => f.Bekleyen_Bakiye ?? 0);

            return Json(new {
                spOncekiDurum = spDefBefore?.Durum,
                spSonrakiDurum = spDefAfter?.Durum,
                nisan = new {
                    tahsilEdilen = spNisan?.TahsilEdilen,
                    bekleyenBakiye = spNisan?.BekleyenBakiyeToplam,
                    payda = (spNisan?.TahsilEdilen ?? 0) + (spNisan?.BekleyenBakiyeToplam ?? 0),
                    oran = (spNisan?.TahsilEdilen ?? 0) + (spNisan?.BekleyenBakiyeToplam ?? 0) > 0
                        ? Math.Round((spNisan?.TahsilEdilen ?? 0) / ((spNisan?.TahsilEdilen ?? 0) + (spNisan?.BekleyenBakiyeToplam ?? 0)) * 100, 1) : 0
                },
                ytd = new {
                    tahsilEdilen = spYtd?.TahsilEdilen,
                    bekleyenBakiye = spYtd?.BekleyenBakiyeToplam,
                    payda = (spYtd?.TahsilEdilen ?? 0) + (spYtd?.BekleyenBakiyeToplam ?? 0),
                    oran = (spYtd?.TahsilEdilen ?? 0) + (spYtd?.BekleyenBakiyeToplam ?? 0) > 0
                        ? Math.Round((spYtd?.TahsilEdilen ?? 0) / ((spYtd?.TahsilEdilen ?? 0) + (spYtd?.BekleyenBakiyeToplam ?? 0)) * 100, 1) : 0
                },
                hamVeri = new {
                    toplamKayit = hamToplam,
                    bakiyeliKayit = hamBakiyeli,
                    bakiyeliToplam = hamBakiyeliToplam,
                    nullBakiyeFallbackAdet = nullBakiyeFallback,
                    nullBakiyeFallbackToplam = nullBakiyeFallbackToplam,
                    hukukiAdet,
                    hukukiToplam
                }
            });
        }

        /// <summary>Geçici: Tahsilat payda fark analizi</summary>
        [HttpGet]
        public async Task<IActionResult> TahsilatPaydaDiag()
        {
            using var db = _contextFactory.CreateDbContext();
            var endDate = new DateTime(2026, 4, 17);

            // 1) Ham — filtre yok
            var ham = (await db.VIEW_CP_EXCEL_FATURAs.AsNoTracking().ToListAsync())
                .Where(f => f.Fatura_Vade_Tarihi.HasValue && f.Fatura_Vade_Tarihi.Value <= endDate && (f.Bekleyen_Bakiye ?? 0) > 0)
                .ToList();

            // 2) Dedupe
            var dedupe = ham.GroupBy(f => f.Fatura_No ?? f.GetHashCode().ToString()).Select(g => g.First()).ToList();

            // 3) İade/Ret hariç
            var iadeRet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "İADE", "IADE", "İPTAL", "IPTAL", "RET", "İade Fatura", "Iade Fatura" };
            var noIade = dedupe.Where(f => !iadeRet.Contains((f.Durum ?? "").Trim())).ToList();

            // 4) Hukuki hariç
            var noHukuki = noIade.Where(f => string.IsNullOrWhiteSpace(f.Hukuki_Durum)).ToList();

            // Hukuki olanlar
            var hukukiList = noIade.Where(f => !string.IsNullOrWhiteSpace(f.Hukuki_Durum))
                .Select(f => new { f.Fatura_No, f.Bekleyen_Bakiye, hukuki = f.Hukuki_Durum, f.Durum }).ToList();

            // Dedupe'da silinen duplar
            var dupSilinen = ham.Count - dedupe.Count;
            var dupTutar = ham.Sum(f => f.Bekleyen_Bakiye ?? 0) - dedupe.Sum(f => f.Bekleyen_Bakiye ?? 0);

            // İade/Ret silinen
            var iadeSilinen = dedupe.Where(f => iadeRet.Contains((f.Durum ?? "").Trim()))
                .Select(f => new { f.Fatura_No, f.Bekleyen_Bakiye, f.Durum }).ToList();

            return Json(new
            {
                ham = new { adet = ham.Count, toplam = ham.Sum(f => f.Bekleyen_Bakiye ?? 0) },
                dedupe = new { adet = dedupe.Count, toplam = dedupe.Sum(f => f.Bekleyen_Bakiye ?? 0), silinenAdet = dupSilinen, silinenTutar = dupTutar },
                iadeRetHaric = new { adet = noIade.Count, toplam = noIade.Sum(f => f.Bekleyen_Bakiye ?? 0), silinenler = iadeSilinen },
                hukukiHaric = new { adet = noHukuki.Count, toplam = noHukuki.Sum(f => f.Bekleyen_Bakiye ?? 0), hukukiSilinenler = hukukiList },
                spSonuc = noHukuki.Sum(f => f.Bekleyen_Bakiye ?? 0)
            });
        }

        [HttpGet]
        public async Task<IActionResult> TahsilatDurumAnaliz()
        {
            using var db = _contextFactory.CreateDbContext();
            // Tüm faturalar vade <= 10 Nisan (İade/Ret hariç)
            var tum = (await db.VIEW_CP_EXCEL_FATURAs.AsNoTracking().ToListAsync())
                .GroupBy(f => f.Fatura_No ?? f.GetHashCode().ToString()).Select(g => g.First())
                .Where(f => f.Fatura_Vade_Tarihi.HasValue && f.Fatura_Vade_Tarihi.Value <= new DateTime(2026,4,10,23,59,59))
                .Where(f => {
                    var d2 = (f.Durum ?? "").Trim();
                    return d2 != "İADE" && d2 != "IADE" && d2 != "İPTAL" && d2 != "IPTAL" && d2 != "RET"
                        && !d2.Equals("İade Fatura", StringComparison.OrdinalIgnoreCase)
                        && !d2.Equals("Iade Fatura", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            // Tahsil edilenler (hafta içi)
            var tahsilHafta = tum.Where(f => f.Tahsil_Tarihi.HasValue
                && f.Tahsil_Tarihi.Value >= new DateTime(2026,4,6)
                && f.Tahsil_Tarihi.Value <= new DateTime(2026,4,10,23,59,59))
                .Sum(f => f.Tahsil_Edilen ?? 0);

            // Bekleyen bakiye
            var bekleyen = tum.Where(f => (f.Bekleyen_Bakiye ?? ((f.Fatura_Toplam ?? 0) - (f.Tahsil_Edilen ?? 0))) > 0).ToList();

            var byDurum = bekleyen.GroupBy(f => (f.Durum ?? "").Trim()).Select(g => new {
                durum = g.Key == "" ? "(boş)" : g.Key, adet = g.Count(),
                bakiye = g.Sum(f => f.Bekleyen_Bakiye ?? ((f.Fatura_Toplam ?? 0) - (f.Tahsil_Edilen ?? 0)))
            }).OrderByDescending(x => x.bakiye).ToList();

            var byHukuki = bekleyen.GroupBy(f => (f.Hukuki_Durum ?? "").Trim()).Select(g => new {
                hukukiDurum = g.Key == "" ? "(boş)" : g.Key, adet = g.Count(),
                bakiye = g.Sum(f => f.Bekleyen_Bakiye ?? ((f.Fatura_Toplam ?? 0) - (f.Tahsil_Edilen ?? 0)))
            }).OrderByDescending(x => x.bakiye).ToList();

            // Durum boş olmayan faturaların tahsil_edilen toplamı (PAYDA'ya katkısı)
            var durumDoluTahsil = tum.Where(f => (f.Durum ?? "").Trim() != "" && f.Tahsil_Tarihi.HasValue
                && f.Tahsil_Tarihi.Value >= new DateTime(2026,4,6)
                && f.Tahsil_Tarihi.Value <= new DateTime(2026,4,10,23,59,59))
                .GroupBy(f => (f.Durum ?? "").Trim())
                .Select(g => new { durum = g.Key, adet = g.Count(), tahsil = g.Sum(f => f.Tahsil_Edilen ?? 0) })
                .OrderByDescending(x => x.tahsil).ToList();

            var toplamBakiye = bekleyen.Sum(f => f.Bekleyen_Bakiye ?? ((f.Fatura_Toplam ?? 0) - (f.Tahsil_Edilen ?? 0)));

            return Json(new {
                tahsilHafta,
                toplamBakiye,
                payda = tahsilHafta + toplamBakiye,
                excelPayda = 30880797m,
                fark = (tahsilHafta + toplamBakiye) - 30880797m,
                bekleyenAdet = bekleyen.Count,
                byDurum, byHukuki, durumDoluTahsil
            });
        }

        /// <summary>Fatura SP diagnostic — Mart 2026 her adımın katkısını gösterir</summary>
        [HttpGet]
        public async Task<IActionResult> FaturaDiag()
        {
            using var db = _contextFactory.CreateDbContext();
            db.Database.SetCommandTimeout(120);

            var startDate = new DateTime(2026, 3, 1);
            var endDate = new DateTime(2026, 3, 31);

            // 1) SP sonucu
            var spRows = await db.Database.SqlQueryRaw<FaturaRow>(
                "EXEC SP_COCKPIT_FATURA @p0, @p1", startDate, endDate).ToListAsync();
            var spToplam = spRows.Sum(r => r.NetTutar);

            // 2) Ham VIEW: Mart faturalari (Fatura_Tarihi bazli)
            var hamView = (await db.VIEW_CP_EXCEL_FATURAs.AsNoTracking().ToListAsync())
                .GroupBy(f => f.Fatura_No ?? f.GetHashCode().ToString()).Select(g => g.First())
                .Where(f => f.Fatura_Tarihi.HasValue && f.Fatura_Tarihi.Value >= startDate && f.Fatura_Tarihi.Value <= endDate)
                .ToList();

            // 3) Varuna Closed Mart (InvoiceDate bazli)
            var varunaMart = await db.TBL_VARUNA_SIPARIs.AsNoTracking()
                .Where(s => s.OrderStatus == "Closed" && s.TotalNetAmount > 0
                    && s.SerialNumber != null
                    && s.DeletedOn == null
                    && s.InvoiceDate.HasValue && s.InvoiceDate.Value >= startDate && s.InvoiceDate.Value <= endDate)
                .ToListAsync();

            // 4) LoadAllCachedData sonucu (eski yöntem)
            var cached = await LoadAllCachedDataAsync(_contextFactory, _cache, _hedef);
            var allFaturalar = cached.faturalar;
            var varunaTutarMap = cached.varunaTutarMap;

            // Eski yöntemle Mart fatura toplamı
            var iadeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "İADE", "IADE", "İPTAL", "IPTAL", "RET", "İade Fatura", "Iade Fatura" };

            // Tahakkuk map
            var tahakkukRecs = await db.TBLSOS_FATURA_TAHAKKUKs.AsNoTracking()
                .Where(t => t.Aktif).ToListAsync();
            var tahakkukMap = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in tahakkukRecs)
            {
                if (!string.IsNullOrWhiteSpace(r.SapReferansNo))
                    tahakkukMap.TryAdd(r.SapReferansNo.Trim(), r.TahakkukTarihi);
                if (!string.IsNullOrWhiteSpace(r.FaturaNo))
                    tahakkukMap.TryAdd(r.FaturaNo.Trim(), r.TahakkukTarihi);
            }

            // Siparis map
            var siparisler = await db.TBL_VARUNA_SIPARIs.AsNoTracking()
                .Where(s => s.OrderId != null && s.SerialNumber != null
                    && s.OrderStatus == "Closed" && s.TotalNetAmount > 0
                    && s.DeletedOn == null)
                .Select(s => new { s.SerialNumber, s.SAPOutReferenceCode, s.TotalNetAmount })
                .ToListAsync();
            var sapMap = siparisler
                .Where(s => !string.IsNullOrWhiteSpace(s.SAPOutReferenceCode))
                .GroupBy(s => s.SerialNumber!)
                .ToDictionary(g => g.Key, g => g.First().SAPOutReferenceCode?.Trim(), StringComparer.OrdinalIgnoreCase);

            decimal eskiToplam = 0; int eskiAdet = 0;
            decimal eskiVarunaEslesenToplam = 0; int eskiVarunaEslesen = 0;
            decimal eskiVarunaDisiToplam = 0; int eskiVarunaDisi = 0;

            foreach (var f in allFaturalar)
            {
                if (f.Fatura_No != null && iadeSet.Contains((f.Durum ?? "").Trim())) continue;
                if (IsNegatifDurum(f.Durum) || IsRetDurum(f.Durum)) continue;

                // Efektif tarih hesapla
                DateTime efektifTarih = f.Fatura_Tarihi ?? DateTime.MinValue;
                if (f.Fatura_No != null)
                {
                    // SAP bazli tahakkuk
                    if (sapMap.TryGetValue(f.Fatura_No, out var sapRef) && sapRef != null
                        && tahakkukMap.TryGetValue(sapRef, out var tahTarih))
                        efektifTarih = tahTarih;
                    else if (tahakkukMap.TryGetValue(f.Fatura_No, out var tahTarih2))
                        efektifTarih = tahTarih2;
                }

                if (efektifTarih < startDate || efektifTarih > endDate) continue;

                decimal tutar = 0;
                if (f.Fatura_No != null && varunaTutarMap.TryGetValue(f.Fatura_No, out var vt))
                {
                    tutar = vt;
                    eskiVarunaEslesenToplam += tutar;
                    eskiVarunaEslesen++;
                }
                else
                {
                    tutar = f.Fatura_Toplam ?? 0;
                    eskiVarunaDisiToplam += tutar;
                    eskiVarunaDisi++;
                }
                eskiToplam += tutar;
                eskiAdet++;
            }

            return Json(new
            {
                mart_SP = new { toplam = spToplam, adet = spRows.Count },
                mart_EskiYontem = new {
                    toplam = eskiToplam, adet = eskiAdet,
                    varunaEslesen = new { toplam = eskiVarunaEslesenToplam, adet = eskiVarunaEslesen },
                    varunaDisi = new { toplam = eskiVarunaDisiToplam, adet = eskiVarunaDisi }
                },
                fark = eskiToplam - spToplam,
                hamView_Mart = new { adet = hamView.Count, toplam = hamView.Sum(f => f.Fatura_Toplam ?? 0) },
                varunaClosed_Mart = new { adet = varunaMart.Count, toplam = varunaMart.Sum(s => s.TotalNetAmount ?? 0) },
            });
        }

        // ───────────────────────────────────────────────────────────────────────
        // Vadesi geçmiş faturalar — sayfa içi alt-panel için detay listesi
        // Tahsilat kartındaki "Vadesi geçmiş · N fatura · ₺X" bandına tıklanınca açılır.
        // Aynı filtre mantığı: Fatura_Vade_Tarihi < bugün, Bekleyen_Bakiye > 0, Durum boş.
        // Yaş = (bugün - Fatura_Vade_Tarihi) gün; minYas chip filtreleri için (0/30/60/90).
        // ───────────────────────────────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> VadesiGecmisDetay(int minYas = 0)
        {
            var (faturalar, _, _, _, _, _, _) = await LoadAllCachedDataAsync(_contextFactory, _cache, _hedef, false);
            var temsilciMap = await GetTemsilciMapAsync(_contextFactory, _cache);
            var bugun = DateTime.Now.Date;

            // Vadesi geçmiş kuralı — GetCockpit @724-732 ile birebir
            // Müşteri zinciri (kullanıcı kararı 2026-05-13, canlı DB doğrulamalı):
            //   1) Varuna (MusteriUnvan = musteriMap[FaturaNo] = TBL_VARUNA_SIPARIS.AccountTitle)
            //   2) Excel fallback — VIEW_CP_EXCEL_FATURA.Proje (kaynak: VeriOkumaDonusum.TBL_FINANS_FATURA.Proje;
            //      Satici_Adi/Ilgili_Kisi 104/104 vadesi geçmiş kayıtta BOŞ; Proje 104/104 DOLU — müşteri/proje adı taşır)
            //      UI'da küçük "Excel" rozeti gösterir.
            //   3) Boşsa "—" (hukuki badge ayrı kolon)
            // Sıralama: Vade tarihi asc (en eski vade en üstte) → Bakiye desc.
            var rowsAll = faturalar
                .Where(f => f.Fatura_Vade_Tarihi.HasValue
                            && (f.Bekleyen_Bakiye ?? 0m) > 0
                            && string.IsNullOrWhiteSpace(f.Durum)
                            && f.Fatura_Vade_Tarihi!.Value.Date < bugun)
                .OrderBy(f => f.Fatura_Vade_Tarihi!.Value.Date)
                .ThenByDescending(f => f.Bekleyen_Bakiye ?? 0m)
                .Select(f =>
                {
                    string musteriAd;
                    string kaynak;
                    if (!string.IsNullOrWhiteSpace(f.MusteriUnvan))
                    {
                        musteriAd = f.MusteriUnvan!.Trim();
                        kaynak = "varuna";
                    }
                    else if (!string.IsNullOrWhiteSpace(f.Proje))
                    {
                        musteriAd = f.Proje!.Trim();
                        kaynak = "excel";
                    }
                    else if (!string.IsNullOrWhiteSpace(f.Satici_Adi))
                    {
                        musteriAd = f.Satici_Adi!.Trim();
                        kaynak = "excel";
                    }
                    else
                    {
                        musteriAd = "—";
                        kaynak = "yok";
                    }
                    return new
                    {
                        faturaNo = f.Fatura_No,
                        musteri = musteriAd,
                        musteriKaynak = kaynak,
                        satisRep = (f.Fatura_No != null && temsilciMap.TryGetValue(f.Fatura_No, out var rep))
                                   ? rep
                                   : (!string.IsNullOrWhiteSpace(f.Ilgili_Kisi) ? f.Ilgili_Kisi!.Trim() : "—"),
                        faturaTarihi = f.Fatura_Tarihi?.ToString("dd.MM.yyyy"),
                        vadeTarihi = f.Fatura_Vade_Tarihi?.ToString("dd.MM.yyyy"),
                        bakiye = f.Bekleyen_Bakiye ?? 0m,
                        hukuki = !string.IsNullOrWhiteSpace(f.Hukuki_Durum),
                        yas = (int)(bugun - f.Fatura_Vade_Tarihi!.Value.Date).TotalDays
                    };
                })
                .ToList();

            // Özet: hukuki takip dahil tüm vadesi geçmiş (üst kartla tutarlı)
            var ozet = new
            {
                toplamAdet = rowsAll.Count,
                toplamBakiye = rowsAll.Sum(r => r.bakiye),
                hukukiAdet = rowsAll.Count(r => r.hukuki),
                hukukiBakiye = rowsAll.Where(r => r.hukuki).Sum(r => r.bakiye),
                yas30Plus = rowsAll.Count(r => r.yas >= 30),
                yas60Plus = rowsAll.Count(r => r.yas >= 60),
                yas90Plus = rowsAll.Count(r => r.yas >= 90)
            };

            // Yaş filtresi (chip)
            var rows = minYas > 0 ? rowsAll.Where(r => r.yas >= minYas).ToList() : rowsAll;

            return Json(new { ok = true, ozet, rows });
        }

        #endregion
    }
}
