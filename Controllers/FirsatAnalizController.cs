using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SOS.DbData;
using SOS.Services;
using SOS.Models.ViewModels;
using SOS.Models.MsK;

namespace SOS.Controllers
{
    // Raw SQL result DTO
    public class FirsatUrunGrupDto
    {
        public string? UrunGrubu { get; set; }
        public int Adet { get; set; }
        public decimal Tutar { get; set; }
    }

    public class FirsatUrunIdDto
    {
        public string? FirsatId { get; set; }
        public string? UrunGrubu { get; set; }
        public decimal Tutar { get; set; }
    }

    public class ProductGroupNameDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    public class ProductGroupParentDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? ParentName { get; set; }
    }

    public class FirsatMusteriDto
    {
        public string? Musteri { get; set; }
        public int Adet { get; set; }
        public decimal Tutar { get; set; }
    }

    public class MusteriDto { public string? Name { get; set; } public string? Musteri { get; set; } }

    // ── Pipeline SP DTO'ları ──
    public class PipelineFirsatRow
    {
        public decimal TutarAcik { get; set; }
        public int AdetAcik { get; set; }
        public decimal TutarWon { get; set; }
        public int AdetWon { get; set; }
        public decimal TutarLost { get; set; }
        public int AdetLost { get; set; }
    }
    public class PipelineTeklifRow
    {
        public decimal TutarAktif { get; set; }
        public int AdetAktif { get; set; }
        public decimal TutarRed { get; set; }
        public int AdetRed { get; set; }
    }
    public class PipelineSiparisRow
    {
        public decimal TutarAcik { get; set; }
        public int AdetAcik { get; set; }
        public decimal TutarKapali { get; set; }
        public int AdetKapali { get; set; }
    }

    [Authorize]
    public class FirsatAnalizController : Controller
    {
        private readonly IDbContextFactory<MskDbContext> _contextFactory;
        private readonly IMemoryCache _cache;
        private readonly SOS.Services.ITahakkukService _tahakkukService;
        private readonly ICockpitDataService _cockpitData;
        private readonly IHedefService _hedef;
        private static readonly TimeSpan CacheTTL = TimeSpan.FromMinutes(30);
        private static readonly SemaphoreSlim _cacheLock = new(1, 1);

        // Cache keys
        private const string CACHE_KEY_URUN_ESLESTIRME = "firsat_urun_eslestirme";
        private const string CACHE_KEY_ANA_URUNLER = "firsat_ana_urunler";

        // Varuna PRODUCTGRUPS parent adı → TBLSOS_ANA_URUN adı (yetim fırsat çözümlemesinde)
        private static readonly Dictionary<string, string> FirsatGrupAnaUrunMap =
            new(StringComparer.OrdinalIgnoreCase)
            {
                { "CallDesk", "ServiceCore" }
            };

        // REAL Status values from database (English strings, not numeric)
        // Accepted=662, Draft=199, Presented=163, Closed=69, Reject=45, Denied=38, InReview=11, Approved=7, PartiallyOrdered=5
        private static readonly string[] WonStatuses = { "Accepted", "Approved", "PartiallyOrdered" };
        private static readonly string[] LostStatuses = { "Reject", "Denied", "Closed" };
        // Aktif teklif: müşteriye sunulmuş veya inceleme aşamasında. Draft (taslak) hariç.
        // Whitelist tercih edildi → bilinmeyen yeni status'ler otomatik dışarıda kalır.
        private static readonly string[] ActiveTeklifStatuses = { "Presented", "InReview" };
        // Sanity: CreatedOn epoch (1970) civarı bozuk değerleri ele.
        private static readonly DateTime MinValidCreatedOn = new(2020, 1, 1);
        private static readonly string[] OpenStatuses = { "Draft", "Presented", "InReview" };
        // Pipeline = open (not won, not lost)
        private static readonly string[] PipelineStatuses = { "Draft", "Presented", "InReview" };

        // Siparis statuses (from DB: Open, Closed, Canceled)
        private static readonly string[] SiparisClosedStatuses = { "Closed" };
        private static readonly string[] SiparisCancelledStatuses = { "Canceled" };

        // İade/İptal/Ret durum filtreleri — Cockpit ile aynı mantık
        private static readonly HashSet<string> _negativeDurumSetFA = new(StringComparer.OrdinalIgnoreCase)
        {
            "İADE", "IADE", "İPTAL", "IPTAL", "İADE FATURA", "IADE FATURA"
        };

        private static bool IsRetDurumStatic(string? durum)
            => !string.IsNullOrWhiteSpace(durum)
               && durum.AsSpan().Trim().Equals("RET".AsSpan(), StringComparison.OrdinalIgnoreCase);

        private static bool IsNegatifDurumStatic(string? durum)
        {
            if (string.IsNullOrWhiteSpace(durum)) return false;
            var d = durum.Trim();
            if (_negativeDurumSetFA.Contains(d)) return true;
            return d.Contains("ade", StringComparison.OrdinalIgnoreCase)
                || d.Contains("ptal", StringComparison.OrdinalIgnoreCase);
        }

        // Test/deneme kayıtları filtresi — EF IQueryable extension
        // Soft-delete: silinmiş kayıtları da burada eliyoruz (DeletedOn IS NULL).
        private static IQueryable<TBL_VARUNA_TEKLIF> ExcludeTest(IQueryable<TBL_VARUNA_TEKLIF> q)
            => q.Where(t => t.Account_Title == null || (!t.Account_Title.Contains("TEST") && !t.Account_Title.Contains("DENEME") && !t.Account_Title.Contains("test") && !t.Account_Title.Contains("deneme")));
        private static IQueryable<TBL_VARUNA_SIPARI> ExcludeTestSiparis(IQueryable<TBL_VARUNA_SIPARI> q)
            => q.Where(s => s.DeletedOn == null && (s.AccountTitle == null || (!s.AccountTitle.Contains("TEST") && !s.AccountTitle.Contains("DENEME") && !s.AccountTitle.Contains("test") && !s.AccountTitle.Contains("deneme"))));
        private static IQueryable<TBL_VARUNA_OPPORTUNITIES> ExcludeTestFirsat(IQueryable<TBL_VARUNA_OPPORTUNITIES> q)
            => q.Where(o => o.DeletedOn == null && (o.Name == null || (!o.Name.Contains("TEST") && !o.Name.Contains("DENEME") && !o.Name.Contains("test") && !o.Name.Contains("deneme"))));

        public FirsatAnalizController(
            IDbContextFactory<MskDbContext> contextFactory,
            IMemoryCache cache,
            SOS.Services.ITahakkukService tahakkukService,
            ICockpitDataService cockpitData,
            IHedefService hedef)
        {
            _contextFactory = contextFactory;
            _cache = cache;
            _tahakkukService = tahakkukService;
            _cockpitData = cockpitData;
            _hedef = hedef;
        }

        /// <summary>
        /// Sipariş için efektif fatura tarihi: tahakkuk varsa onu, yoksa orijinal InvoiceDate'i döner.
        /// Tahakkuk tablosu SAP bazlı (SapReferansNo primary, FaturaNo opsiyonel) — önce SerialNumber/FaturaNo,
        /// yoksa SAPOutReferenceCode ile lookup yapılır.
        /// </summary>
        private static DateTime? EfektifInvoice(string? serialNumber, string? sapOutRef, DateTime? invoiceDate, Dictionary<string, DateTime> tahakkukMap)
        {
            if (!string.IsNullOrEmpty(serialNumber) && tahakkukMap.TryGetValue(serialNumber, out var th1))
                return th1;
            if (!string.IsNullOrEmpty(sapOutRef) && tahakkukMap.TryGetValue(sapOutRef, out var th2))
                return th2;
            return invoiceDate;
        }

        // ?_force=1 → cache'i atla, taze veri çek. Warmer kullanmaz; sadece UI "Yenile" butonu.
        // HttpContext null ise (warmer direct action call) güvenli false döner.
        private bool IsForceRefresh()
        {
            try { return HttpContext?.Request?.Query["_force"].ToString() == "1"; }
            catch { return false; }
        }

        // ── Tahakkuk-bazlı kapalı OppId → efektif fatura tarihi haritası ──
        // Won fırsatların efektif kapanış tarihleri. Dönem-bağımsız (havuz-seviye).
        // Hem GetOpportunitySummary hem GetFunnelBreakdown bu haritayı kullanır.
        // Önceden iki endpoint birebir aynı 7 EF query'yi tekrar tekrar çalıştırıyordu.
        // 5 dk cache + force flag ile UI "Yenile" davranışı bozulmaz.
        private const string CACHE_KEY_KAPALI_OPP_EFEKTIF = "FA_KapaliOppEfektifMap_v1";
        private static readonly TimeSpan KapaliOppEfektifTTL = TimeSpan.FromMinutes(5);

        private async Task<Dictionary<string, DateTime?>> GetKapaliOppEfektifMapCachedAsync(bool force = false)
        {
            if (!force && _cache.TryGetValue(CACHE_KEY_KAPALI_OPP_EFEKTIF, out Dictionary<string, DateTime?>? cached) && cached != null)
                return cached;

            using var db = _contextFactory.CreateDbContext();
            var tahakkukMap = await _tahakkukService.GetTahakkukMapAsync();

            // Yol 1: Teklif(QuoteId) → Sipariş zinciri (kapalı siparişe bağlanan fırsatlar)
            var kapaliZincir = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue)
                .Join(ExcludeTestSiparis(db.TBL_VARUNA_SIPARIs.AsNoTracking())
                    .Where(s => s.OrderStatus == "Closed" && s.QuoteId != null),
                    t => t.Id.ToString(), s => s.QuoteId,
                    (t, s) => new { OppId = t.OpportunityId!.Value.ToString().ToLower(), s.SerialNumber, s.SAPOutReferenceCode, s.InvoiceDate })
                .ToListAsync();

            // Yol 2: Won fırsat → teklifteki Account_Title → siparişteki AccountTitle eşleşmesi
            var wonOppIds = await ExcludeTestFirsat(db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking())
                .Where(o => o.OpportunityStageName == "Won")
                .Select(o => o.Id).ToListAsync();
            var wonOppIdSet = wonOppIds.Where(id => id != null).Select(id => id!.ToLower()).ToHashSet();

            var teklifMusteriMap = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue && t.Account_Title != null)
                .Select(t => new { OppId = t.OpportunityId!.Value.ToString().ToLower(), t.Account_Title })
                .ToListAsync();
            var oppMusteriMap = teklifMusteriMap
                .Where(t => wonOppIdSet.Contains(t.OppId))
                .GroupBy(t => t.OppId)
                .ToDictionary(g => g.Key, g => g.First().Account_Title!.Trim().ToLower());

            var closedSipEfektif = await ExcludeTestSiparis(db.TBL_VARUNA_SIPARIs.AsNoTracking())
                .Where(s => s.OrderStatus == "Closed" && s.SerialNumber != null && s.AccountTitle != null)
                .Select(s => new { s.SerialNumber, s.SAPOutReferenceCode, s.InvoiceDate, AccountTitle = s.AccountTitle!.Trim().ToLower() })
                .ToListAsync();
            var musteriEfektifMap = closedSipEfektif
                .GroupBy(s => s.AccountTitle)
                .ToDictionary(g => g.Key, g => {
                    foreach (var s in g.OrderByDescending(x => x.InvoiceDate))
                    {
                        var ef = EfektifInvoice(s.SerialNumber, s.SAPOutReferenceCode, s.InvoiceDate, tahakkukMap);
                        if (ef.HasValue && s.InvoiceDate.HasValue && ef.Value != s.InvoiceDate.Value)
                            return ef;
                    }
                    return g.OrderByDescending(x => x.InvoiceDate).First().InvoiceDate;
                });

            var kapaliOppEfektif = kapaliZincir
                .GroupBy(x => x.OppId)
                .ToDictionary(g => g.Key, g => {
                    var first = g.First();
                    return EfektifInvoice(first.SerialNumber, first.SAPOutReferenceCode, first.InvoiceDate, tahakkukMap);
                });

            // Customer-level fallback: aktif teklifi olan fırsatları muaf tut (süreç açık)
            var aktifTeklifOppIds = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue
                    && (t.Status == null || (t.Status != "Reject" && t.Status != "Denied" && t.Status != "Closed")))
                .Select(t => t.OpportunityId!.Value.ToString().ToLower())
                .Distinct().ToListAsync();
            var aktifTeklifSet = aktifTeklifOppIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in oppMusteriMap)
            {
                if (!kapaliOppEfektif.ContainsKey(kv.Key)
                    && !aktifTeklifSet.Contains(kv.Key)
                    && musteriEfektifMap.TryGetValue(kv.Value, out var efDate))
                {
                    kapaliOppEfektif[kv.Key] = efDate;
                }
            }

            _cache.Set(CACHE_KEY_KAPALI_OPP_EFEKTIF, kapaliOppEfektif, KapaliOppEfektifTTL);
            return kapaliOppEfektif;
        }

        #region ParseFilter

        private (DateTime start, DateTime end, string filter, int months) ParseFilter(string? filter, string? startDate, string? endDate)
        {
            var now = DateTime.Now;
            var today = now.Date.AddHours(23).AddMinutes(59).AddSeconds(59);
            var year = now.Year;
            DateTime start;
            DateTime end2;
            int months;
            var fmtP = System.Globalization.CultureInfo.InvariantCulture;
            var style = System.Globalization.DateTimeStyles.None;

            if (!string.IsNullOrEmpty(startDate) && !string.IsNullOrEmpty(endDate)
                && DateTime.TryParseExact(startDate, "yyyy-MM-dd", fmtP, style, out var sd)
                && DateTime.TryParseExact(endDate, "yyyy-MM-dd", fmtP, style, out var ed))
            {
                filter = "range";
                start = sd.Date;
                end2 = ed.Date.AddHours(23).AddMinutes(59).AddSeconds(59);
                months = Math.Max(1, (end2.Year - start.Year) * 12 + end2.Month - start.Month + 1);
                return (start, end2, filter, months);
            }

            switch (filter?.ToLowerInvariant())
            {
                case "ytd":
                    // YTD = yıl başı → bulunduğu ayın SONU (tam ay).
                    // Bu Ay/Çeyrek period sonuna gittiği için YTD de aynı semantikte; aksi halde
                    // Bu Ay (ay sonu) ⊄ YTD (today) çelişkisi doğar.
                    start = new DateTime(year, 1, 1);
                    end2 = new DateTime(year, now.Month, DateTime.DaysInMonth(year, now.Month), 23, 59, 59);
                    months = now.Month;
                    break;
                case "q1":
                    start = new DateTime(year, 1, 1);
                    end2 = new DateTime(year, 3, 31, 23, 59, 59);
                    months = 3;
                    break;
                case "q2":
                    start = new DateTime(year, 4, 1);
                    end2 = new DateTime(year, 6, 30, 23, 59, 59);
                    months = 3;
                    break;
                case "q3":
                    start = new DateTime(year, 7, 1);
                    end2 = new DateTime(year, 9, 30, 23, 59, 59);
                    months = 3;
                    break;
                case "q4":
                    start = new DateTime(year, 10, 1);
                    end2 = new DateTime(year, 12, 31, 23, 59, 59);
                    months = 3;
                    break;
                case "lastmonth":
                    var lmMonth = now.Month == 1 ? 12 : now.Month - 1;
                    var lmYear = now.Month == 1 ? year - 1 : year;
                    start = new DateTime(lmYear, lmMonth, 1);
                    end2 = new DateTime(lmYear, lmMonth, DateTime.DaysInMonth(lmYear, lmMonth), 23, 59, 59);
                    months = 1;
                    break;
                case "all":
                    // Tümü = sınırsız aralık. Verinin tarihinden bağımsız her şey görünür
                    // (geçmiş + gelecek). SQL Server datetime aralığında güvenli sınırlar.
                    filter = "all";
                    start = new DateTime(2000, 1, 1);
                    end2 = new DateTime(2099, 12, 31, 23, 59, 59);
                    months = (end2.Year - start.Year) * 12 + end2.Month;
                    break;
                case "week":
                    var weekStart = today.AddDays(-(int)today.DayOfWeek + 1);
                    if (weekStart > today) weekStart = weekStart.AddDays(-7);
                    start = weekStart.Date;
                    end2 = today;
                    months = 1;
                    break;
                default: // "month" veya null → Bu ay
                    filter = "month";
                    start = new DateTime(year, now.Month, 1);
                    end2 = new DateTime(year, now.Month, DateTime.DaysInMonth(year, now.Month), 23, 59, 59);
                    months = 1;
                    break;
            }

            return (start, end2, filter ?? "month", months);
        }

        #endregion

        #region Status Helpers

        private static string StatusToTurkishStage(string? status) => status switch
        {
            "Draft" => "Taslak",
            "InReview" => "Incelemede",
            "Presented" => "Sunuldu",
            "Approved" => "Onaylandi",
            "Accepted" => "Kabul Edildi",
            "PartiallyOrdered" => "Kismen Siparis",
            "Reject" => "Reddedildi",
            "Denied" => "Reddedildi",
            "Closed" => "Kapatildi",
            _ => status ?? "Bilinmiyor"
        };

        private static string StatusToColor(string? status) => status switch
        {
            "Draft" => "#94a3b8",
            "InReview" => "#f59e0b",
            "Presented" => "#818cf8",
            "Approved" => "#60a5fa",
            "Accepted" => "#10b981",
            "PartiallyOrdered" => "#22c55e",
            "Reject" => "#ef4444",
            "Denied" => "#f87171",
            "Closed" => "#6b7280",
            _ => "#cbd5e1"
        };

        private static string StatusToIcon(string? status) => status switch
        {
            "Draft" => "bi-file-earmark",
            "InReview" => "bi-hourglass-split",
            "Presented" => "bi-send",
            "Approved" or "Accepted" or "PartiallyOrdered" => "bi-check-circle",
            "Reject" or "Denied" => "bi-x-circle",
            "Closed" => "bi-lock",
            _ => "bi-question-circle"
        };

        private static string SiparisStatusToTurkish(string? status) => status switch
        {
            "Open" => "Acik",
            "Closed" => "Kapali",
            "Canceled" => "Iptal",
            _ => status ?? "Bilinmiyor"
        };

        private static string SiparisStatusToColor(string? status) => status switch
        {
            "Open" => "#3b82f6",
            "Closed" => "#22c55e",
            "Cancelled" => "#ef4444",
            "Processing" => "#f59e0b",
            "Invoiced" => "#10b981",
            _ => "#94a3b8"
        };

        #endregion

        /// <summary>
        /// Converts email like "begum.hayta@accounts.univera.com.tr" to "Begüm Hayta"
        /// </summary>
        private static string EmailToDisplayName(string? email)
        {
            if (string.IsNullOrEmpty(email)) return "Bilinmiyor";
            var local = email.Split('@')[0]; // begum.hayta
            var parts = local.Split('.');
            return string.Join(" ", parts.Select(p =>
                System.Globalization.CultureInfo.GetCultureInfo("tr-TR").TextInfo.ToTitleCase(p)));
        }

        #region Product Mapping (TBLSOS_ANA_URUN + TBLSOS_URUN_ESLESTIRME)

        /// <summary>
        /// Loads StockCode -> AnaUrunAd mapping from TBLSOS_URUN_ESLESTIRME + TBLSOS_ANA_URUN.
        /// Cached for 5 minutes.
        /// </summary>
        private async Task<Dictionary<string, string>> GetUrunEslestirmeMapAsync()
        {
            if (_cache.TryGetValue(CACHE_KEY_URUN_ESLESTIRME, out Dictionary<string, string>? cached) && cached != null)
                return cached;

            await _cacheLock.WaitAsync();
            try
            {
                if (_cache.TryGetValue(CACHE_KEY_URUN_ESLESTIRME, out cached) && cached != null)
                    return cached;

                using var db = _contextFactory.CreateDbContext();
                var map = await db.TBLSOS_URUN_ESLESTIRMEs.AsNoTracking()
                    .Include(e => e.AnaUrun)
                    .Where(e => e.AnaUrun != null)
                    .GroupBy(e => e.StokKodu)
                    .Select(g => new { StokKodu = g.Key, AnaUrunAd = g.First().AnaUrun!.Ad })
                    .ToDictionaryAsync(x => x.StokKodu, x => x.AnaUrunAd);

                _cache.Set(CACHE_KEY_URUN_ESLESTIRME, map, CacheTTL);
                return map;
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        /// <summary>
        /// Aşama-bazlı tek-kaynak ürün filtresi resolver:
        ///   1) Sipariş varsa: sipariş kalemleri → TBLSOS_URUN_ESLESTIRME → TBLSOS_ANA_URUN.Ad
        ///   2) Sipariş yok, teklif kalemli: teklif kalemleri → TBLSOS_URUN_ESLESTIRME → TBLSOS_ANA_URUN.Ad
        ///   3) Yetim (kalem yok): ProductGroupId → PRODUCTGRUPS 1 seviye parent + sözlük (CallDesk → ServiceCore)
        /// Belirtilen ana ürün adına eşleşen Opportunity Id'lerini lowercase string olarak döner.
        /// </summary>
        private async Task<HashSet<string>> ResolveOppIdsByProductGroupAsync(MskDbContext db, string product)
        {
            var oppIds = await db.Database.SqlQueryRaw<string>(
                @"WITH PgResolved AS (
                    SELECT CAST(g.Id AS NVARCHAR(64)) AS Id,
                           CASE WHEN COALESCE(p.Name, g.Name) = 'CallDesk' THEN 'ServiceCore'
                                ELSE COALESCE(p.Name, g.Name) END AS Resolved
                    FROM TBL_VARUNA_PRODUCTGRUPS g
                    LEFT JOIN TBL_VARUNA_PRODUCTGRUPS p ON CAST(p.Id AS NVARCHAR(64)) = g.ParentGroupId
                    WHERE g.DeletedOn IS NULL
                  ),
                  OppsHavingSiparis AS (
                    SELECT DISTINCT LOWER(CAST(t.OpportunityId AS NVARCHAR(64))) AS OppId
                    FROM TBL_VARUNA_TEKLIF t
                    JOIN TBL_VARUNA_SIPARIS s ON LOWER(s.QuoteId) = LOWER(CAST(t.Id AS NVARCHAR(64)))
                    WHERE t.DeletedOn IS NULL AND s.OrderStatus = 'Closed' AND s.TotalNetAmount > 0
                      AND t.OpportunityId IS NOT NULL
                  ),
                  OppsHavingTeklifKalem AS (
                    SELECT DISTINCT LOWER(CAST(t.OpportunityId AS NVARCHAR(64))) AS OppId
                    FROM TBL_VARUNA_TEKLIF t
                    JOIN TBL_VARUNA_TEKLIF_URUNLERI u ON u.QuoteId = t.Id
                    WHERE t.DeletedOn IS NULL AND u.DeletedOn IS NULL AND t.OpportunityId IS NOT NULL
                  ),
                  SiparisChain AS (
                    SELECT DISTINCT LOWER(CAST(t.OpportunityId AS NVARCHAR(64))) AS OppId
                    FROM TBL_VARUNA_TEKLIF t
                    JOIN TBL_VARUNA_SIPARIS s ON LOWER(s.QuoteId) = LOWER(CAST(t.Id AS NVARCHAR(64)))
                    JOIN TBL_VARUNA_SIPARIS_URUNLERI su ON su.CrmOrderId = s.OrderId
                    JOIN TBLSOS_URUN_ESLESTIRME e ON e.StokKodu = su.StockCode
                    JOIN TBLSOS_ANA_URUN a ON a.Id = e.AnaUrunId
                    WHERE t.DeletedOn IS NULL AND s.OrderStatus = 'Closed' AND s.TotalNetAmount > 0
                      AND a.Ad = {0} AND t.OpportunityId IS NOT NULL
                  ),
                  TeklifChain AS (
                    SELECT DISTINCT LOWER(CAST(t.OpportunityId AS NVARCHAR(64))) AS OppId
                    FROM TBL_VARUNA_TEKLIF t
                    JOIN TBL_VARUNA_TEKLIF_URUNLERI u ON u.QuoteId = t.Id
                    JOIN TBLSOS_URUN_ESLESTIRME e ON e.StokKodu = u.StockCode
                    JOIN TBLSOS_ANA_URUN a ON a.Id = e.AnaUrunId
                    WHERE t.DeletedOn IS NULL AND u.DeletedOn IS NULL
                      AND a.Ad = {0} AND t.OpportunityId IS NOT NULL
                      AND LOWER(CAST(t.OpportunityId AS NVARCHAR(64))) NOT IN (SELECT OppId FROM OppsHavingSiparis)
                  ),
                  -- Fall-through: kalem zinciri eşleşmiyorsa ProductGroupId çözümü uygulanır
                  -- (Çift sayım UNION ile temizlenir; resolver C bloğu doğru tutar üretir)
                  ProductGroupChain AS (
                    SELECT LOWER(CAST(o.Id AS NVARCHAR(64))) AS OppId
                    FROM TBL_VARUNA_OPPORTUNITIES o
                    JOIN PgResolved pr ON pr.Id = o.ProductGroupId
                    WHERE o.DeletedOn IS NULL AND pr.Resolved = {0}
                  )
                  SELECT OppId FROM SiparisChain
                  UNION
                  SELECT OppId FROM TeklifChain
                  UNION
                  SELECT OppId FROM ProductGroupChain", product).ToListAsync();

            return oppIds
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s.ToLowerInvariant())
                .ToHashSet();
        }

        /// <summary>
        /// Loads all active TBLSOS_ANA_URUN records. Cached for 5 minutes.
        /// </summary>
        private async Task<List<TBLSOS_ANA_URUN>> GetAnaUrunlerAsync()
        {
            if (_cache.TryGetValue(CACHE_KEY_ANA_URUNLER, out List<TBLSOS_ANA_URUN>? cached) && cached != null)
                return cached;

            using var db = _contextFactory.CreateDbContext();
            var list = await db.TBLSOS_ANA_URUNs.AsNoTracking()
                .Where(u => u.Aktif)
                .OrderBy(u => u.Sira)
                .ToListAsync();

            _cache.Set(CACHE_KEY_ANA_URUNLER, list, CacheTTL);
            return list;
        }

        /// <summary>
        /// Given a StockCode, resolve to AnaUrun.Ad using the eslestirme map.
        /// Returns "Diger" if no match.
        /// </summary>
        private static string ResolveProductGroup(string? stockCode, Dictionary<string, string> eslestirmeMap)
        {
            if (string.IsNullOrEmpty(stockCode)) return "Diger";
            return eslestirmeMap.TryGetValue(stockCode, out var ad) ? ad : "Diger";
        }

        #endregion

        #region Filtered Queryables

        /// <summary>
        /// Base filtered teklifler query: non-deleted.
        /// NO date filter on fırsatlar/teklifler — pipeline always shows ALL open records.
        /// Date filter only applies to siparişler and trend charts.
        /// Optionally filtered by person (CreatedBy) and product (via TBLSOS_URUN_ESLESTIRME).
        /// </summary>
        private IQueryable<TBL_VARUNA_TEKLIF> GetFilteredTeklifler(MskDbContext db, DateTime start, DateTime end, string? person, string? product)
        {
            var q = ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking()
                .Where(t => t.DeletedOn == null))
                .Where(t => t.CreatedOn.HasValue && t.CreatedOn.Value >= start && t.CreatedOn.Value <= end);

            if (!string.IsNullOrEmpty(person))
                q = q.Where(t => t.CreatedBy == person);

            if (!string.IsNullOrEmpty(product))
            {
                // product = AnaUrunId (int) or AnaUrun.Kod
                // Find all StokKodu values that belong to this AnaUrun
                var matchingStockCodes = db.TBLSOS_URUN_ESLESTIRMEs.AsNoTracking()
                    .Where(e => e.AnaUrun != null && (e.AnaUrun.Kod == product || e.AnaUrunId.ToString() == product))
                    .Select(e => e.StokKodu);

                var teklifIdsWithProduct = db.TBL_VARUNA_TEKLIF_URUNLERIs.AsNoTracking()
                    .Where(u => u.DeletedOn == null && u.StockCode != null && matchingStockCodes.Contains(u.StockCode))
                    .Select(u => u.QuoteId)
                    .Distinct();

                q = q.Where(t => teklifIdsWithProduct.Contains(t.Id));
            }

            return q;
        }

        /// <summary>
        /// Tahakkuk-aware sipariş filtreleme:
        /// Closed → EfektifTarih (tahakkuk override) dönemde ise dahil
        /// Open → CreateOrderDate dönemde ise dahil
        /// </summary>
        private async Task<List<TBL_VARUNA_SIPARI>> GetFilteredSiparislerAsync(MskDbContext db, DateTime start, DateTime end)
        {
            var tahakkukMap = await _tahakkukService.GetTahakkukMapAsync();
            var raw = await ExcludeTestSiparis(db.TBL_VARUNA_SIPARIs.AsNoTracking())
                .Where(s => s.OrderStatus != "Canceled")
                .ToListAsync();
            return raw.Where(s =>
                (s.OrderStatus == "Closed"
                    && EfektifInvoice(s.SerialNumber, s.SAPOutReferenceCode, s.InvoiceDate, tahakkukMap) is DateTime ef
                    && ef >= start && ef <= end)
                || (s.OrderStatus != "Closed"
                    && s.CreateOrderDate.HasValue
                    && s.CreateOrderDate.Value >= start && s.CreateOrderDate.Value <= end)
            ).ToList();
        }

        #endregion

        // ===================================================================
        // GET /FirsatAnaliz  veya  /FirsatAnaliz/Index
        // ===================================================================
        [Route("FirsatAnaliz")]
        [Route("FirsatAnaliz/Index")]
        public IActionResult Index(string? filter, string? startDate, string? endDate)
        {
            // Tarayıcı cache'i yüzünden eski inline JS çalışmasın — dashboard HTML'i her istekte taze gelir.
            // (AJAX endpoint'leri kendi cache mekanizmalarını kullanır, onlar etkilenmez.)
            Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "0";

            var (start, end, activeFilter, _) = ParseFilter(filter, startDate, endDate);

            var vm = new FirsatAnalizViewModel
            {
                AktifFiltre = activeFilter,
                FiltreBaslangic = start,
                FiltreBitis = end
            };

            // Server-side preload — SADECE cache'ten okur, await YOK.
            // Warmer cache'i sıcaksa inline veri sayfada → ilk paint 0ms AJAX.
            // Cache miss ise null → JS normal AJAX ile doldurur (mevcut davranış).
            ViewBag.InitialKpiCore  = _cache.TryGetValue($"FirsatKpiCore_{start:yyyyMMdd}_{end:yyyyMMdd}_all", out var kpi) ? kpi : null;
            ViewBag.InitialSummary  = _cache.TryGetValue($"FirsatOppSummary_exclusive_{start:yyyyMMdd}_{end:yyyyMMdd}_all", out var sum) ? sum : null;
            ViewBag.InitialFunnelF2 = _cache.TryGetValue($"FirsatFunnel_{start:yyyyMMdd}_{end:yyyyMMdd}_2_____", out var f2) ? f2 : null;
            ViewBag.InitialFunnelF3 = _cache.TryGetValue($"FirsatFunnel_{start:yyyyMMdd}_{end:yyyyMMdd}_3_____", out var f3) ? f3 : null;

            return View(vm);
        }


        // ===================================================================
        // DEBUG: Tüm alanların doluluk oranı ve örnek değerler
        // (class-level [Authorize] devralır)
        // ===================================================================
        [HttpGet]
        public async Task<IActionResult> TestKpi(string? filter)
        {
            var (start, end, f, _) = ParseFilter(filter ?? "all", null, null);
            using var db = _contextFactory.CreateDbContext();
            var teklifler = GetFilteredTeklifler(db, start, end, null, null);
            var totalCount = await teklifler.CountAsync();
            var openList = new[] { "Draft", "Presented", "InReview" };
            var openCount = await teklifler.Where(t => t.Status != null && openList.Contains(t.Status)).CountAsync();
            var openSum = await teklifler.Where(t => t.Status != null && openList.Contains(t.Status)).SumAsync(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m);
            var wonCount = await teklifler.Where(t => t.Status != null && WonStatuses.Contains(t.Status)).CountAsync();
            var wonSum = await teklifler.Where(t => t.Status != null && WonStatuses.Contains(t.Status)).SumAsync(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m);
            return Json(new { filter = f, start, end, totalCount, openCount, openSum, wonCount, wonSum });
        }

        [HttpGet]
        public async Task<IActionResult> FieldAudit()
        {
            using var db = _contextFactory.CreateDbContext();
            var total = await db.TBL_VARUNA_TEKLIFs.AsNoTracking().Where(t => t.DeletedOn == null).CountAsync();

            // Her alanın doluluk oranı
            var fields = new {
                total,
                Status = await db.TBL_VARUNA_TEKLIFs.AsNoTracking().Where(t => t.DeletedOn == null && t.Status != null).CountAsync(),
                CreatedBy = await db.TBL_VARUNA_TEKLIFs.AsNoTracking().Where(t => t.DeletedOn == null && t.CreatedBy != null).CountAsync(),
                CreatedOn = await db.TBL_VARUNA_TEKLIFs.AsNoTracking().Where(t => t.DeletedOn == null && t.CreatedOn != null).CountAsync(),
                FirstCreatedByName = await db.TBL_VARUNA_TEKLIFs.AsNoTracking().Where(t => t.DeletedOn == null && t.FirstCreatedByName != null).CountAsync(),
                FirstCreatedDate = await db.TBL_VARUNA_TEKLIFs.AsNoTracking().Where(t => t.DeletedOn == null && t.FirstCreatedDate != null).CountAsync(),
                ModifiedBy = await db.TBL_VARUNA_TEKLIFs.AsNoTracking().Where(t => t.DeletedOn == null && t.ModifiedBy != null).CountAsync(),
                ModifiedOn = await db.TBL_VARUNA_TEKLIFs.AsNoTracking().Where(t => t.DeletedOn == null && t.ModifiedOn != null).CountAsync(),
                Account_Title = await db.TBL_VARUNA_TEKLIFs.AsNoTracking().Where(t => t.DeletedOn == null && t.Account_Title != null).CountAsync(),
                Name = await db.TBL_VARUNA_TEKLIFs.AsNoTracking().Where(t => t.DeletedOn == null && t.Name != null).CountAsync(),
                OpportunityId = await db.TBL_VARUNA_TEKLIFs.AsNoTracking().Where(t => t.DeletedOn == null && t.OpportunityId != null).CountAsync(),
                ProposalOwnerId = await db.TBL_VARUNA_TEKLIFs.AsNoTracking().Where(t => t.DeletedOn == null && t.ProposalOwnerId != null).CountAsync(),
                PersonId = await db.TBL_VARUNA_TEKLIFs.AsNoTracking().Where(t => t.DeletedOn == null && t.PersonId != null).CountAsync(),
                TeamId = await db.TBL_VARUNA_TEKLIFs.AsNoTracking().Where(t => t.DeletedOn == null && t.TeamId != null).CountAsync(),
                AccountId = await db.TBL_VARUNA_TEKLIFs.AsNoTracking().Where(t => t.DeletedOn == null && t.AccountId != null).CountAsync(),
                CrmOrderId = await db.TBL_VARUNA_TEKLIFs.AsNoTracking().Where(t => t.DeletedOn == null && t.CrmOrderId != null).CountAsync(),
                TotalNetAmount = await db.TBL_VARUNA_TEKLIFs.AsNoTracking().Where(t => t.DeletedOn == null && t.TotalNetAmountLocalCurrency_Amount != null && t.TotalNetAmountLocalCurrency_Amount > 0).CountAsync(),
                ExpirationDate = await db.TBL_VARUNA_TEKLIFs.AsNoTracking().Where(t => t.DeletedOn == null && t.ExpirationDate != null).CountAsync(),
                Number = await db.TBL_VARUNA_TEKLIFs.AsNoTracking().Where(t => t.DeletedOn == null && t.Number != null).CountAsync(),
                StockId = await db.TBL_VARUNA_TEKLIFs.AsNoTracking().Where(t => t.DeletedOn == null && t.StockId != null).CountAsync(),
            };

            // Status dağılımı
            var statuses = await db.TBL_VARUNA_TEKLIFs.AsNoTracking()
                .Where(t => t.DeletedOn == null)
                .GroupBy(t => t.Status)
                .Select(g => new { status = g.Key, count = g.Count(), sumNet = g.Sum(x => x.TotalNetAmountLocalCurrency_Amount ?? 0m) })
                .OrderByDescending(x => x.count).ToListAsync();

            // CreatedBy kişiler (email → isim dönüşümü test)
            var persons = await db.TBL_VARUNA_TEKLIFs.AsNoTracking()
                .Where(t => t.DeletedOn == null && t.CreatedBy != null)
                .GroupBy(t => t.CreatedBy)
                .Select(g => new { email = g.Key, count = g.Count(), pipeline = g.Where(x => x.Status == "Draft" || x.Status == "Presented" || x.Status == "InReview").Sum(x => x.TotalNetAmountLocalCurrency_Amount ?? 0m) })
                .OrderByDescending(x => x.count).Take(15).ToListAsync();

            // Ürün kalemleri - hangi tablo, kaç kayıt
            var teklifUrunCount = await db.TBL_VARUNA_TEKLIF_URUNLERIs.AsNoTracking().Where(u => u.DeletedOn == null).CountAsync();
            var teklifUrunSample = await db.TBL_VARUNA_TEKLIF_URUNLERIs.AsNoTracking()
                .Where(u => u.DeletedOn == null && u.StockCode != null)
                .Select(u => new { u.StockCode, u.StockName, u.Total_Amount, u.QuoteId })
                .Take(5).ToListAsync();

            // TBLSOS eşleştirme
            var eslestirmeCount = await db.TBLSOS_URUN_ESLESTIRMEs.AsNoTracking().CountAsync();
            var anaUrunler = await db.TBLSOS_ANA_URUNs.AsNoTracking().Where(u => u.Aktif).OrderBy(u => u.Sira).ToListAsync();
            var eslestirmeSample = await db.TBLSOS_URUN_ESLESTIRMEs.AsNoTracking()
                .Include(e => e.AnaUrun).Take(10)
                .Select(e => new { e.StokKodu, e.Mask, e.LisansTipi, AnaUrun = e.AnaUrun != null ? e.AnaUrun.Ad : null })
                .ToListAsync();

            // Sipariş bilgileri
            var siparisTotal = await db.TBL_VARUNA_SIPARIs.AsNoTracking().CountAsync();
            var siparisStatuses = await db.TBL_VARUNA_SIPARIs.AsNoTracking()
                .GroupBy(s => s.OrderStatus).Select(g => new { status = g.Key, count = g.Count(), sum = g.Sum(x => x.TotalNetAmount ?? 0m) })
                .OrderByDescending(x => x.count).ToListAsync();

            // 5 örnek teklif - TÜM önemli alanlar
            var samples = await db.TBL_VARUNA_TEKLIFs.AsNoTracking()
                .Where(t => t.DeletedOn == null && t.TotalNetAmountLocalCurrency_Amount > 0)
                .OrderByDescending(t => t.TotalNetAmountLocalCurrency_Amount)
                .Take(5)
                .Select(t => new {
                    t.Id, t.Number, t.Name, t.Status, t.Account_Title,
                    t.CreatedBy, t.ModifiedBy, t.CreatedOn, t.ModifiedOn,
                    t.FirstCreatedByName, t.FirstCreatedDate,
                    t.ProposalOwnerId, t.PersonId, t.TeamId, t.AccountId,
                    t.TotalNetAmountLocalCurrency_Amount,
                    t.TotalAmountWithTaxLocalCurrency_Amount,
                    t.TotalProfitAmount_Amount,
                    t.CrmOrderId, t.OpportunityId, t.ExpirationDate, t.StockId
                }).ToListAsync();

            return Json(new { fields, statuses, persons, teklifUrunCount, teklifUrunSample, eslestirmeCount, anaUrunler, eslestirmeSample, siparisTotal, siparisStatuses, samples });
        }

        // ===================================================================
        // GET /FirsatAnaliz/GetKpiSummary
        // ===================================================================
        [HttpGet]
        public async Task<IActionResult> GetKpiSummary(string? filter, string? startDate, string? endDate, string? person, string? product)
        {
            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);

            var cacheKey = $"FirsatKpi_{start:yyyyMMdd}_{end:yyyyMMdd}_{person ?? "all"}_{product ?? "all"}";
            if (_cache.TryGetValue(cacheKey, out object? cachedKpi) && cachedKpi != null)
                return Json(cachedKpi);

            using var db = _contextFactory.CreateDbContext();
            var teklifler = GetFilteredTeklifler(db, start, end, person, product);

            // Pipeline: Status IN open (1,2,3,6) = active pipeline
            var activeTeklifler = teklifler.Where(t => t.Status != null && OpenStatuses.Contains(t.Status));
            var pipelineToplam = await activeTeklifler.SumAsync(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m);
            var aktifFirsatAdet = await activeTeklifler.CountAsync();

            // Trend: compare current period vs previous period of same duration
            var duration = end - start;
            var prevStart = start.AddDays(-duration.TotalDays);
            var prevEnd = start.AddSeconds(-1);
            var prevTeklifler = ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null
                    && t.CreatedOn.HasValue
                    && t.CreatedOn.Value >= prevStart
                    && t.CreatedOn.Value <= prevEnd);
            if (!string.IsNullOrEmpty(person))
                prevTeklifler = prevTeklifler.Where(t => t.CreatedBy == person);

            var prevPipeline = await prevTeklifler
                .Where(t => t.Status != null && OpenStatuses.Contains(t.Status))
                .SumAsync(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m);
            var aktifFirsatTrend = prevPipeline > 0
                ? Math.Round((pipelineToplam - prevPipeline) / prevPipeline * 100, 1)
                : 0m;

            // Acik teklifler: Status IN ('1','2','3','6')
            var acikTeklifAdet = aktifFirsatAdet; // same as pipeline count
            var acikTeklifToplam = pipelineToplam;

            // Siparisler (tahakkuk-aware)
            var siparisler = await GetFilteredSiparislerAsync(db, start, end);
            var acikSiparisler = siparisler.Where(s => s.OrderStatus != null
                && !SiparisClosedStatuses.Contains(s.OrderStatus)).ToList();
            var acikSiparisAdet = acikSiparisler.Count;
            var acikSiparisToplam = acikSiparisler.Sum(s => s.TotalNetAmount ?? 0m);

            var kapaliSiparisler = siparisler.Where(s => s.OrderStatus != null
                && (s.OrderStatus == "Closed" || s.OrderStatus == "Completed")).ToList();
            var kapaliSiparisAdet = kapaliSiparisler.Count;
            var kapaliSiparisToplam = kapaliSiparisler.Sum(s => s.TotalNetAmount ?? 0m);

            // Kazanma oranlari
            var wonCount = await teklifler.Where(t => t.Status != null && WonStatuses.Contains(t.Status)).CountAsync();
            var lostCount = await teklifler.Where(t => t.Status != null && LostStatuses.Contains(t.Status)).CountAsync();
            var kazanmaOraniCount = (wonCount + lostCount) > 0
                ? Math.Round((decimal)wonCount / (wonCount + lostCount) * 100, 1)
                : 0m;

            var wonRevenue = await teklifler
                .Where(t => t.Status != null && WonStatuses.Contains(t.Status))
                .SumAsync(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m);
            var lostRevenue = await teklifler
                .Where(t => t.Status != null && LostStatuses.Contains(t.Status))
                .SumAsync(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m);
            var kazanmaOraniRevenue = (wonRevenue + lostRevenue) > 0
                ? Math.Round(wonRevenue / (wonRevenue + lostRevenue) * 100, 1)
                : 0m;

            var ortAnlasma = aktifFirsatAdet > 0
                ? Math.Round(pipelineToplam / aktifFirsatAdet, 2)
                : 0m;

            var result = new FirsatKpiDto(
                PipelineToplam: pipelineToplam,
                AktifFirsatAdet: aktifFirsatAdet,
                AktifFirsatTrend: aktifFirsatTrend,
                AcikTeklifAdet: acikTeklifAdet,
                AcikTeklifToplam: acikTeklifToplam,
                AcikSiparisAdet: acikSiparisAdet,
                AcikSiparisToplam: acikSiparisToplam,
                KapaliSiparisAdet: kapaliSiparisAdet,
                KapaliSiparisToplam: kapaliSiparisToplam,
                KazanmaOraniCount: kazanmaOraniCount,
                KazanmaOraniRevenue: kazanmaOraniRevenue,
                OrtAnlasma: ortAnlasma
            );
            _cache.Set(cacheKey, result, CacheTTL);
            return Json(result);
        }

        // ===================================================================
        // GET /FirsatAnaliz/GetFunnelData
        // ===================================================================
        [HttpGet]
        public async Task<IActionResult> GetFunnelData(string? filter, string? startDate, string? endDate, string? person, string? product)
        {
            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);

            var cacheKey = $"FirsatFunnel_{start:yyyyMMdd}_{end:yyyyMMdd}_{person ?? "all"}_{product ?? "all"}";
            if (_cache.TryGetValue(cacheKey, out object? cachedFunnel) && cachedFunnel != null)
                return Json(cachedFunnel);

            using var db = _contextFactory.CreateDbContext();
            var teklifler = GetFilteredTeklifler(db, start, end, person, product);

            // Stage 1: Toplam Firsat - ALL non-deleted teklifler in period
            var firsatCount = await teklifler.CountAsync();
            var firsatValue = await teklifler.SumAsync(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m);

            // Stage 2: Acik Pipeline - Status IN (1,2,3,6)
            var acikPipeline = teklifler.Where(t => t.Status != null && OpenStatuses.Contains(t.Status));
            var acikCount = await acikPipeline.CountAsync();
            var acikValue = await acikPipeline.SumAsync(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m);

            // Stage 3: Sunuldu - Status = '6' specifically
            var sunuldu = teklifler.Where(t => t.Status == "6");
            var sunulduCount = await sunuldu.CountAsync();
            var sunulduValue = await sunuldu.SumAsync(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m);

            // Stage 4: Kazanilan - Status IN (4,7,10)
            var wonTeklifler = teklifler.Where(t => t.Status != null && WonStatuses.Contains(t.Status));
            var wonCount = await wonTeklifler.CountAsync();
            var wonValue = await wonTeklifler.SumAsync(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m);

            // Stage 5: Siparis Olusan - COUNT TBL_VARUNA_SIPARI linked via CrmOrderId
            var teklifCrmOrderIds = await teklifler
                .Where(t => t.CrmOrderId != null)
                .Select(t => t.CrmOrderId!.Value.ToString())
                .ToListAsync();

            var teklifIds = await teklifler.Select(t => t.Id.ToString()).ToListAsync();

            var linkedSiparisler = ExcludeTestSiparis(db.TBL_VARUNA_SIPARIs.AsNoTracking())
                .Where(s => (s.QuoteId != null && teklifIds.Contains(s.QuoteId))
                    || (s.OrderId != null && teklifCrmOrderIds.Contains(s.OrderId)));
            var siparisCount = await linkedSiparisler.CountAsync();
            var siparisValue = await linkedSiparisler.SumAsync(s => s.TotalNetAmount ?? 0m);

            var stages = new List<FunnelStageDto>
            {
                new("Toplam Firsat", firsatCount, firsatValue, 100m, "#3b82f6"),
                new("Acik Pipeline", acikCount, acikValue,
                    firsatCount > 0 ? Math.Round((decimal)acikCount / firsatCount * 100, 1) : 0m, "#8b5cf6"),
                new("Sunuldu", sunulduCount, sunulduValue,
                    acikCount > 0 ? Math.Round((decimal)sunulduCount / acikCount * 100, 1) : 0m, "#f59e0b"),
                new("Kazanilan", wonCount, wonValue,
                    firsatCount > 0 ? Math.Round((decimal)wonCount / firsatCount * 100, 1) : 0m, "#22c55e"),
                new("Siparis Olusan", siparisCount, siparisValue,
                    wonCount > 0 ? Math.Round((decimal)siparisCount / wonCount * 100, 1) : 0m, "#10b981")
            };

            _cache.Set(cacheKey, stages, CacheTTL);
            return Json(stages);
        }

        // ===================================================================
        // GET /FirsatAnaliz/GetSubFunnelSummary?filter=month
        // [DEPRECATED — 2026-05-13] Sub-funnel verisi artık GetOpportunitySummary
        // içinde `subFunnel` field'ı olarak döner (kümülatif partition, üst funnel ile birebir).
        // Bu endpoint geriye dönük uyumluluk için var; frontend artık _oppData.subFunnel kullanır.
        // ===================================================================
        [HttpGet]
        public async Task<IActionResult> GetSubFunnelSummary(string? filter, string? startDate, string? endDate)
        {
            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);
            var cacheKey = $"SubFunnel_v3_{start:yyyyMMdd}_{end:yyyyMMdd}";
            if (_cache.TryGetValue(cacheKey, out object? cached) && cached != null)
                return Json(cached);

            using var db = _contextFactory.CreateDbContext();
            var yenSet = new[] { "Renovation", "AdditionalUsage" };
            var closedTeklifStatuses = new[] { "Denied", "Reject", "Closed" };

            // ─── 1) FIRSAT KADEMESİ — Stage filtresi YOK, tüm fırsatlar dahil (Won/Lost dahil) ───
            //    Funnel mantığı için fırsat en geniş havuz olmalı; sub-funnel kendi içinde tutarlı sıralama
            var firsatQ = ExcludeTestFirsat(db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking())
                .Where(o => o.CloseDate >= start && o.CloseDate <= end);

            var yenFirsatAdet  = await firsatQ.Where(o => o.DealType != null && yenSet.Contains(o.DealType)).CountAsync();
            var yenFirsatTutar = await firsatQ.Where(o => o.DealType != null && yenSet.Contains(o.DealType))
                                              .SumAsync(o => o.AmountAmount ?? 0m);
            var ysFirsatAdet   = await firsatQ.Where(o => o.DealType == null || !yenSet.Contains(o.DealType)).CountAsync();
            var ysFirsatTutar  = await firsatQ.Where(o => o.DealType == null || !yenSet.Contains(o.DealType))
                                              .SumAsync(o => o.AmountAmount ?? 0m);

            // ─── 3) TEKLİF KADEMESİ — SP K3 filtreleri + DealType inherit (fırsatsız Yeni Satış'a) ───
            //    Status NOT IN ('Denied','Reject','Closed'); OpportunityId NULL teklifler de dahil
            var oppDealMap = await ExcludeTestFirsat(db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking())
                .Where(o => o.DealType != null)
                .Select(o => new { o.Id, o.DealType })
                .ToDictionaryAsync(x => x.Id, x => x.DealType!);

            var teklifRaw = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.CreatedOn >= start && t.CreatedOn <= end
                         && (t.Status == null || !closedTeklifStatuses.Contains(t.Status)))
                .Select(t => new { OppId = t.OpportunityId, Tutar = t.TotalNetAmountLocalCurrency_Amount })
                .ToListAsync();

            int yenTeklifAdet = 0, ysTeklifAdet = 0;
            decimal yenTeklifTutar = 0m, ysTeklifTutar = 0m;
            foreach (var t in teklifRaw)
            {
                var tutar = t.Tutar ?? 0m;
                string? deal = null;
                if (t.OppId.HasValue)
                    oppDealMap.TryGetValue(t.OppId.Value.ToString(), out deal);
                // Fırsatsız teklif (OppId NULL) veya DealType yok/diğer → Yeni Satış
                if (deal != null && yenSet.Contains(deal)) { yenTeklifAdet++; yenTeklifTutar += tutar; }
                else                                        { ysTeklifAdet++;  ysTeklifTutar  += tutar; }
            }

            // ─── 4) SATIŞ KADEMESİ — SP K5 filtreleri × DocCode partition (zaten uyumluydu) ───
            //    OrderStatus=Closed, TotalNetAmount>0; DocCode='ZZ08'=Yenileme, fırsatsız dahil
            var satisQ =
                from s in ExcludeTestSiparis(db.TBL_VARUNA_SIPARIs.AsNoTracking())
                where s.OrderStatus == "Closed" && s.TotalNetAmount > 0
                      && s.CreateOrderDate >= start && s.CreateOrderDate <= end
                join dt in db.TBL_VARUNA_SALESDOCUMENTTYPESAPs on s.SalesDocumentTypeSapId equals dt.Id into dtj
                from dt in dtj.DefaultIfEmpty()
                select new { Code = dt.Code, Tutar = s.TotalNetAmount };

            var yenSatisAdet  = await satisQ.Where(x => x.Code == "ZZ08").CountAsync();
            var yenSatisTutar = await satisQ.Where(x => x.Code == "ZZ08").SumAsync(x => x.Tutar ?? 0m);
            var ysSatisAdet   = await satisQ.Where(x => x.Code != "ZZ08" || x.Code == null).CountAsync();
            var ysSatisTutar  = await satisQ.Where(x => x.Code != "ZZ08" || x.Code == null).SumAsync(x => x.Tutar ?? 0m);

            var result = new
            {
                donem = new { start = start.ToString("yyyy-MM-dd"), end = end.ToString("yyyy-MM-dd") },
                yenileme = new {
                    firsatAdet = yenFirsatAdet,  firsatTutar = yenFirsatTutar,
                    teklifAdet = yenTeklifAdet,  teklifTutar = yenTeklifTutar,
                    satisAdet  = yenSatisAdet,   satisTutar  = yenSatisTutar
                },
                yeniSatis = new {
                    firsatAdet = ysFirsatAdet,   firsatTutar = ysFirsatTutar,
                    teklifAdet = ysTeklifAdet,   teklifTutar = ysTeklifTutar,
                    satisAdet  = ysSatisAdet,    satisTutar  = ysSatisTutar
                }
            };

            _cache.Set(cacheKey, result, CacheTTL);
            return Json(result);
        }

        // ===================================================================
        // GET /FirsatAnaliz/GetStatusBreakdown?type=firsatlar|teklifler|siparisler
        // ===================================================================
        [HttpGet]
        public async Task<IActionResult> GetStatusBreakdown(string type, string? filter, string? startDate, string? endDate, string? person, string? product)
        {
            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);

            var cacheKey = $"FirsatStatus_{type}_{start:yyyyMMdd}_{end:yyyyMMdd}_{person ?? "all"}_{product ?? "all"}";
            if (_cache.TryGetValue(cacheKey, out object? cachedStatus) && cachedStatus != null)
                return Json(cachedStatus);

            using var db = _contextFactory.CreateDbContext();

            switch (type?.ToLowerInvariant())
            {
                case "firsatlar":
                case "teklifler":
                {
                    var teklifler = await GetFilteredTeklifler(db, start, end, person, product)
                        .GroupBy(t => t.Status ?? "0")
                        .Select(g => new { Status = g.Key, Count = g.Count(), Total = g.Sum(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m) })
                        .ToListAsync();

                    var items = teklifler.Select(g => new StatusBreakdownDto(
                        StatusName: StatusToTurkishStage(g.Status),
                        Count: g.Count,
                        TotalValue: g.Total,
                        Color: StatusToColor(g.Status),
                        Icon: StatusToIcon(g.Status)
                    )).OrderByDescending(i => i.TotalValue).ToList();

                    var group = new StatusBreakdownGroupDto(
                        GroupTitle: type == "firsatlar" ? "Firsat Durumlari" : "Teklif Durumlari",
                        GrandTotal: items.Sum(i => i.TotalValue),
                        GrandCount: items.Sum(i => i.Count),
                        Items: items
                    );
                    _cache.Set(cacheKey, group, CacheTTL);
                    return Json(group);
                }
                case "siparisler":
                {
                    var siparislerList = await GetFilteredSiparislerAsync(db, start, end);
                    var siparisler = siparislerList
                        .GroupBy(s => s.OrderStatus ?? "Bilinmiyor")
                        .Select(g => new { Status = g.Key, Count = g.Count(), Total = g.Sum(s => s.TotalNetAmount ?? 0m) })
                        .ToList();

                    var items = siparisler.Select(g => new StatusBreakdownDto(
                        StatusName: SiparisStatusToTurkish(g.Status),
                        Count: g.Count,
                        TotalValue: g.Total,
                        Color: SiparisStatusToColor(g.Status),
                        Icon: "fas fa-shopping-cart"
                    )).OrderByDescending(i => i.TotalValue).ToList();

                    var group2 = new StatusBreakdownGroupDto(
                        GroupTitle: "Siparis Durumlari",
                        GrandTotal: items.Sum(i => i.TotalValue),
                        GrandCount: items.Sum(i => i.Count),
                        Items: items
                    );
                    _cache.Set(cacheKey, group2, CacheTTL);
                    return Json(group2);
                }
                default:
                    return BadRequest(new { error = "Gecersiz tip. Kullanilabilir: firsatlar, teklifler, siparisler" });
            }
        }

        // ===================================================================
        // GET /FirsatAnaliz/GetChartData?chartType=trend|product|customer
        // ===================================================================
        [HttpGet]
        public async Task<IActionResult> GetChartData(string chartType, string? filter, string? startDate, string? endDate, string? person, string? product)
        {
            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);
            var cacheKey = $"FirsatChart_{chartType}_{start:yyyyMMdd}_{end:yyyyMMdd}_{person}_{product}";

            if (_cache.TryGetValue(cacheKey, out ChartResponseDto? cached) && cached != null)
                return Json(cached);

            ChartResponseDto result;

            switch (chartType?.ToLowerInvariant())
            {
                case "trend":
                {
                    // Last 6 months from end date
                    var trendStart = end.AddMonths(-5);
                    trendStart = new DateTime(trendStart.Year, trendStart.Month, 1);

                    var labels = new List<string>();
                    var pipelineData = new List<decimal>();
                    var wonData = new List<decimal>();
                    var siparisData = new List<decimal>();

                    using var db = _contextFactory.CreateDbContext();

                    for (int i = 0; i < 6; i++)
                    {
                        var monthStart = trendStart.AddMonths(i);
                        var monthEnd = new DateTime(monthStart.Year, monthStart.Month,
                            DateTime.DaysInMonth(monthStart.Year, monthStart.Month), 23, 59, 59);

                        labels.Add(monthStart.ToString("MMM yyyy", new System.Globalization.CultureInfo("tr-TR")));

                        var monthTeklifler = ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                            .Where(t => t.DeletedOn == null
                                && t.CreatedOn.HasValue
                                && t.CreatedOn.Value >= monthStart
                                && t.CreatedOn.Value <= monthEnd);

                        if (!string.IsNullOrEmpty(person))
                            monthTeklifler = monthTeklifler.Where(t => t.CreatedBy == person);

                        pipelineData.Add(await monthTeklifler
                            .Where(t => t.Status != null && OpenStatuses.Contains(t.Status))
                            .SumAsync(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m));

                        wonData.Add(await monthTeklifler
                            .Where(t => t.Status != null && WonStatuses.Contains(t.Status))
                            .SumAsync(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m));

                        siparisData.Add(await ExcludeTestSiparis(db.TBL_VARUNA_SIPARIs.AsNoTracking())
                            .Where(s => s.CreateOrderDate.HasValue
                                && s.CreateOrderDate.Value >= monthStart
                                && s.CreateOrderDate.Value <= monthEnd)
                            .SumAsync(s => s.TotalNetAmount ?? 0m));
                    }

                    result = new ChartResponseDto(
                        Labels: labels.ToArray(),
                        Datasets: new List<ChartDatasetDto>
                        {
                            new("Pipeline", pipelineData.ToArray(), "rgba(59,130,246,0.2)", "#3b82f6"),
                            new("Kazanilan", wonData.ToArray(), "rgba(34,197,94,0.2)", "#22c55e"),
                            new("Siparis", siparisData.ToArray(), "rgba(245,158,11,0.2)", "#f59e0b")
                        }
                    );
                    break;
                }
                case "product":
                {
                    // USE TBLSOS_ANA_URUN + TBLSOS_URUN_ESLESTIRME for product grouping
                    var eslestirmeMap = await GetUrunEslestirmeMapAsync();

                    using var db = _contextFactory.CreateDbContext();

                    var teklifUrunleri = await db.TBL_VARUNA_TEKLIF_URUNLERIs.AsNoTracking()
                        .Where(u => u.DeletedOn == null && u.QuoteId != null)
                        .Select(u => new { u.QuoteId, u.StockCode, Total = u.NetLineTotalAmountLocal_Amount ?? 0m })
                        .ToListAsync();

                    // Filter by date range via teklifler
                    var teklifIdsInRange = await GetFilteredTeklifler(db, start, end, person, product)
                        .Select(t => t.Id)
                        .ToListAsync();

                    var teklifIdSet = teklifIdsInRange.ToHashSet();

                    var grouped = teklifUrunleri
                        .Where(u => u.QuoteId.HasValue && teklifIdSet.Contains(u.QuoteId.Value))
                        .Select(u => new { GrupAdi = ResolveProductGroup(u.StockCode, eslestirmeMap), u.Total })
                        .GroupBy(x => x.GrupAdi)
                        .Select(g => new { Grup = g.Key, Total = g.Sum(x => x.Total) })
                        .OrderByDescending(x => x.Total)
                        .ToList();

                    // Top 5 + Diger
                    var top5 = grouped.Take(5).ToList();
                    var diger = grouped.Skip(5).Sum(x => x.Total);

                    var productLabels = top5.Select(x => x.Grup).ToList();
                    var productValues = top5.Select(x => x.Total).ToList();
                    if (diger > 0)
                    {
                        productLabels.Add("Diger");
                        productValues.Add(diger);
                    }

                    var colors = new[] { "#3b82f6", "#8b5cf6", "#f59e0b", "#10b981", "#ef4444", "#6b7280" };

                    result = new ChartResponseDto(
                        Labels: productLabels.ToArray(),
                        Datasets: new List<ChartDatasetDto>
                        {
                            new("Urun Grubu", productValues.ToArray(),
                                string.Join(",", colors.Take(productLabels.Count)),
                                string.Join(",", colors.Take(productLabels.Count)))
                        }
                    );
                    break;
                }
                case "customer":
                {
                    using var db = _contextFactory.CreateDbContext();

                    var customerData = await GetFilteredTeklifler(db, start, end, person, product)
                        .Where(t => t.Account_Title != null)
                        .GroupBy(t => t.Account_Title!)
                        .Select(g => new { Customer = g.Key, Total = g.Sum(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m) })
                        .OrderByDescending(x => x.Total)
                        .Take(10)
                        .ToListAsync();

                    result = new ChartResponseDto(
                        Labels: customerData.Select(c => c.Customer).ToArray(),
                        Datasets: new List<ChartDatasetDto>
                        {
                            new("Musteri Pipeline", customerData.Select(c => c.Total).ToArray(),
                                "rgba(59,130,246,0.6)", "#3b82f6")
                        }
                    );
                    break;
                }
                default:
                    return BadRequest(new { error = "Gecersiz chartType. Kullanilabilir: trend, product, customer" });
            }

            _cache.Set(cacheKey, result, CacheTTL);
            return Json(result);
        }

        // ===================================================================
        // GET /FirsatAnaliz/GetLeaderboard
        // ===================================================================
        [HttpGet]
        public async Task<IActionResult> GetLeaderboard(string? filter, string? startDate, string? endDate)
        {
            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);
            var cacheKey = $"FirsatLeaderboard_{start:yyyyMMdd}_{end:yyyyMMdd}";

            if (_cache.TryGetValue(cacheKey, out List<LeaderboardEntryDto>? cached) && cached != null)
                return Json(cached);

            using var db = _contextFactory.CreateDbContext();

            var teklifler = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null
                    && t.CreatedOn.HasValue
                    && t.CreatedOn.Value >= start
                    && t.CreatedOn.Value <= end
                    && t.CreatedBy != null)
                .Select(t => new
                {
                    t.CreatedBy,
                    t.Status,
                    Amount = t.TotalNetAmountLocalCurrency_Amount ?? 0m
                })
                .ToListAsync();

            var leaderboard = teklifler
                .GroupBy(t => t.CreatedBy!)
                .Select(g =>
                {
                    var pipeline = g.Where(t => t.Status != null && OpenStatuses.Contains(t.Status)).Sum(t => t.Amount);
                    var totalDeals = g.Count();
                    var wonDeals = g.Count(t => t.Status != null && WonStatuses.Contains(t.Status));
                    var lostDeals = g.Count(t => t.Status != null && LostStatuses.Contains(t.Status));
                    var winRate = (wonDeals + lostDeals) > 0
                        ? Math.Round((decimal)wonDeals / (wonDeals + lostDeals) * 100, 1)
                        : 0m;
                    var avgDealSize = totalDeals > 0 ? Math.Round(pipeline / totalDeals, 2) : 0m;

                    return new { Name = g.Key, Pipeline = pipeline, TotalDeals = totalDeals, WonDeals = wonDeals, WinRate = winRate, AvgDealSize = avgDealSize };
                })
                .OrderByDescending(x => x.Pipeline)
                .Take(10)
                .Select((x, i) => new LeaderboardEntryDto(
                    Rank: i + 1,
                    Name: x.Name,
                    PipelineValue: x.Pipeline,
                    TotalDeals: x.TotalDeals,
                    WonDeals: x.WonDeals,
                    WinRate: x.WinRate,
                    AvgDealSize: x.AvgDealSize
                ))
                .ToList();

            _cache.Set(cacheKey, leaderboard, CacheTTL);
            return Json(leaderboard);
        }

        // ===================================================================
        // GET /FirsatAnaliz/GetRiskAlerts
        // ===================================================================
        [HttpGet]
        public async Task<IActionResult> GetRiskAlerts(string? filter, string? startDate, string? endDate, string? person)
        {
            var now = DateTime.Now;
            var alerts = new List<RiskAlertDto>();

            using var db = _contextFactory.CreateDbContext();

            // Base query for open teklifler (no date filter -- risks are global)
            var openTeklifler = ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null
                    && t.Status != null
                    && OpenStatuses.Contains(t.Status));

            if (!string.IsNullOrEmpty(person))
                openTeklifler = openTeklifler.Where(t => t.CreatedBy == person);

            // 1. CRITICAL: Stale opportunities - ModifiedOn < 30 days ago AND still open
            var staleDate = now.AddDays(-30);
            var staleOpps = await openTeklifler
                .Where(t => t.ModifiedOn.HasValue && t.ModifiedOn.Value < staleDate)
                .Select(t => new { t.TotalNetAmountLocalCurrency_Amount })
                .ToListAsync();
            if (staleOpps.Count > 0)
            {
                alerts.Add(new RiskAlertDto(
                    Type: "stale_opportunity",
                    Severity: "critical",
                    Title: "Hareketsiz Firsatlar",
                    Message: $"30 gunden fazla suredir guncellenmeyen {staleOpps.Count} acik firsat bulunuyor.",
                    Count: staleOpps.Count,
                    Value: staleOpps.Sum(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m),
                    Icon: "fas fa-exclamation-triangle"
                ));
            }

            // 2. WARNING: Expired quotes - ExpirationDate < today AND open
            var expiredQuotes = await openTeklifler
                .Where(t => t.ExpirationDate.HasValue && t.ExpirationDate.Value < now.Date)
                .Select(t => new { t.TotalNetAmountLocalCurrency_Amount })
                .ToListAsync();
            if (expiredQuotes.Count > 0)
            {
                alerts.Add(new RiskAlertDto(
                    Type: "expired_quote",
                    Severity: "warning",
                    Title: "Suresi Dolmus Teklifler",
                    Message: $"Gecerlilik suresi dolmus {expiredQuotes.Count} acik teklif var.",
                    Count: expiredQuotes.Count,
                    Value: expiredQuotes.Sum(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m),
                    Icon: "fas fa-clock"
                ));
            }

            // 3. WARNING: Expiring soon - ExpirationDate < today+7 AND open AND not yet expired
            var soonDate = now.Date.AddDays(7);
            var expiringSoon = await openTeklifler
                .Where(t => t.ExpirationDate.HasValue
                    && t.ExpirationDate.Value >= now.Date
                    && t.ExpirationDate.Value < soonDate)
                .Select(t => new { t.TotalNetAmountLocalCurrency_Amount })
                .ToListAsync();
            if (expiringSoon.Count > 0)
            {
                alerts.Add(new RiskAlertDto(
                    Type: "expiring_soon",
                    Severity: "warning",
                    Title: "Suresi Dolmak Uzere Olan Teklifler",
                    Message: $"7 gun icinde suresi dolacak {expiringSoon.Count} teklif var.",
                    Count: expiringSoon.Count,
                    Value: expiringSoon.Sum(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m),
                    Icon: "fas fa-hourglass-half"
                ));
            }

            // 4. INFO: Aging orders - CreateOrderDate < 45 days ago AND open
            var agingDate = now.AddDays(-45);
            var agingOrders = await ExcludeTestSiparis(db.TBL_VARUNA_SIPARIs.AsNoTracking())
                .Where(s => s.CreateOrderDate.HasValue
                    && s.CreateOrderDate.Value < agingDate
                    && s.OrderStatus != null
                    && s.OrderStatus == "Open")
                .Select(s => new { s.TotalNetAmount })
                .ToListAsync();
            if (agingOrders.Count > 0)
            {
                alerts.Add(new RiskAlertDto(
                    Type: "aging_order",
                    Severity: "info",
                    Title: "Yaslanan Siparisler",
                    Message: $"45 gunden eski {agingOrders.Count} acik siparis bulunuyor.",
                    Count: agingOrders.Count,
                    Value: agingOrders.Sum(s => s.TotalNetAmount ?? 0m),
                    Icon: "fas fa-info-circle"
                ));
            }

            return Json(alerts);
        }

        // ===================================================================
        // GET /FirsatAnaliz/GetDetail?type=&status=&page=1&pageSize=20
        // ===================================================================
        [HttpGet]
        public async Task<IActionResult> GetDetail(string type, string? status, int page = 1, int pageSize = 20,
            string? filter = null, string? startDate = null, string? endDate = null, string? person = null, string? product = null)
        {
            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            using var db = _contextFactory.CreateDbContext();

            switch (type?.ToLowerInvariant())
            {
                case "firsatlar":
                case "teklifler":
                {
                    var q = GetFilteredTeklifler(db, start, end, person, product);
                    if (!string.IsNullOrEmpty(status))
                        q = q.Where(t => t.Status == status);

                    var totalCount = await q.CountAsync();
                    var rows = await q
                        .OrderByDescending(t => t.CreatedOn)
                        .Skip((page - 1) * pageSize)
                        .Take(pageSize)
                        .Select(t => new DetailRowDto(
                            t.Id.ToString(),
                            t.Number ?? "-",
                            t.Account_Title ?? "-",
                            t.Name ?? "-",
                            StatusToTurkishStage(t.Status),
                            t.TotalNetAmountLocalCurrency_Amount ?? 0m,
                            t.TotalProfitAmount_Amount,
                            t.CreatedOn,
                            t.CreatedBy ?? "-",
                            StatusToColor(t.Status)
                        ))
                        .ToListAsync();

                    return Json(new DetailResponseDto(rows, totalCount, page, pageSize));
                }
                case "siparisler":
                {
                    var sipList = await GetFilteredSiparislerAsync(db, start, end);
                    if (!string.IsNullOrEmpty(status))
                        sipList = sipList.Where(s => s.OrderStatus == status).ToList();

                    var totalCount = sipList.Count;
                    var rows = sipList
                        .OrderByDescending(s => s.CreateOrderDate)
                        .Skip((page - 1) * pageSize)
                        .Take(pageSize)
                        .Select(s => new DetailRowDto(
                            s.LNGKOD.ToString(),
                            s.SerialNumber ?? "-",
                            s.AccountTitle ?? "-",
                            "-",
                            SiparisStatusToTurkish(s.OrderStatus),
                            s.TotalNetAmount ?? 0m,
                            s.TotalProfitAmount,
                            s.CreateOrderDate,
                            s.CreatedBy ?? "-",
                            SiparisStatusToColor(s.OrderStatus)
                        ))
                        .ToList();

                    return Json(new DetailResponseDto(rows, totalCount, page, pageSize));
                }
                default:
                    return BadRequest(new { error = "Gecersiz tip" });
            }
        }

        // ===================================================================
        // GET /FirsatAnaliz/GetFilterOptions
        // ===================================================================
        [HttpGet]
        public async Task<IActionResult> GetFilterOptions()
        {
            using var db = _contextFactory.CreateDbContext();

            var kisiler = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.CreatedBy != null)
                .Select(t => t.CreatedBy!)
                .Distinct()
                .OrderBy(n => n)
                .ToListAsync();

            // Use TBLSOS_ANA_URUN for product filter options
            var anaUrunler = await GetAnaUrunlerAsync();

            return Json(new
            {
                kisiler = kisiler.Select(k => new FilterOption(k, k)),
                urunler = anaUrunler.Select(u => new FilterOption(u.Kod, u.Ad))
            });
        }

        // ===================================================================
        // GET /FirsatAnaliz/GetProductPerformance
        // ===================================================================
        [HttpGet]
        public async Task<IActionResult> GetProductPerformance(string? filter, string? startDate, string? endDate, string? person)
        {
            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);

            var cacheKey = $"FirsatProduct_{start:yyyyMMdd}_{end:yyyyMMdd}_{person ?? "all"}";
            if (_cache.TryGetValue(cacheKey, out object? cachedProduct) && cachedProduct != null)
                return Json(cachedProduct);

            // Use TBLSOS_ANA_URUN + TBLSOS_URUN_ESLESTIRME
            var eslestirmeMap = await GetUrunEslestirmeMapAsync();

            using var db = _contextFactory.CreateDbContext();

            // Teklif IDs + statuses in range
            var teklifIdsInRange = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null
                    && t.CreatedOn.HasValue
                    && t.CreatedOn.Value >= start
                    && t.CreatedOn.Value <= end
                    && (string.IsNullOrEmpty(person) || t.CreatedBy == person))
                .Select(t => new { t.Id, t.Status })
                .ToListAsync();

            var teklifIdSet = teklifIdsInRange.Select(t => t.Id).ToHashSet();
            var teklifStatusMap = teklifIdsInRange.ToDictionary(t => t.Id, t => t.Status);

            var teklifUrunleri = await db.TBL_VARUNA_TEKLIF_URUNLERIs.AsNoTracking()
                .Where(u => u.DeletedOn == null && u.QuoteId != null)
                .Select(u => new
                {
                    u.QuoteId,
                    u.StockCode,
                    Total = u.NetLineTotalAmountLocal_Amount ?? 0m,
                    Profit = u.TotalProfitAmountLocal_Amount ?? 0m
                })
                .ToListAsync();

            var filteredUrunler = teklifUrunleri
                .Where(u => u.QuoteId.HasValue && teklifIdSet.Contains(u.QuoteId.Value))
                .Select(u =>
                {
                    var grupAdi = ResolveProductGroup(u.StockCode, eslestirmeMap);
                    var status = teklifStatusMap.GetValueOrDefault(u.QuoteId!.Value);
                    return new
                    {
                        GrupAdi = grupAdi,
                        u.Total,
                        u.Profit,
                        IsWon = status != null && WonStatuses.Contains(status),
                        IsLost = status != null && LostStatuses.Contains(status),
                        IsDecided = status != null && (WonStatuses.Contains(status) || LostStatuses.Contains(status))
                    };
                })
                .ToList();

            // Siparis urunleri in range (tahakkuk-aware)
            var siparislerInRange = await GetFilteredSiparislerAsync(db, start, end);
            var siparisOrderIds = siparislerInRange.Select(s => s.OrderId).Where(o => o != null).ToHashSet();

            var siparisUrunleri = await db.TBL_VARUNA_SIPARIS_URUNLERIs.AsNoTracking()
                .Where(u => u.CrmOrderId != null)
                .Select(u => new { u.CrmOrderId, u.StockCode, Total = u.Total ?? 0m })
                .ToListAsync();

            var filteredSiparisUrunleri = siparisUrunleri
                .Where(u => siparisOrderIds.Contains(u.CrmOrderId))
                .Select(u => new { GrupAdi = ResolveProductGroup(u.StockCode, eslestirmeMap), u.Total })
                .GroupBy(x => x.GrupAdi)
                .ToDictionary(g => g.Key, g => new { Count = g.Count(), Total = g.Sum(x => x.Total) });

            var productPerformance = filteredUrunler
                .GroupBy(x => x.GrupAdi)
                .Select(g =>
                {
                    var teklifCount = g.Count();
                    var teklifAmount = g.Sum(x => x.Total);
                    var wonCount = g.Count(x => x.IsWon);
                    var decidedCount = g.Count(x => x.IsDecided);
                    var winRate = decidedCount > 0 ? Math.Round((decimal)wonCount / decidedCount * 100, 1) : 0m;
                    var profitMargin = teklifAmount > 0
                        ? Math.Round(g.Sum(x => x.Profit) / teklifAmount * 100, 1)
                        : 0m;

                    filteredSiparisUrunleri.TryGetValue(g.Key, out var sipData);

                    return new
                    {
                        urunGrubu = g.Key,
                        teklifAdet = teklifCount,
                        teklifTutar = teklifAmount,
                        siparisAdet = sipData?.Count ?? 0,
                        siparisTutar = sipData?.Total ?? 0m,
                        kazanmaOrani = winRate,
                        karMarji = profitMargin
                    };
                })
                .OrderByDescending(x => x.teklifTutar)
                .ToList();

            _cache.Set(cacheKey, productPerformance, CacheTTL);
            return Json(productPerformance);
        }

        // ===================================================================
        // GET /FirsatAnaliz/GetPersonScorecard?person=X
        // ===================================================================
        [HttpGet]
        public async Task<IActionResult> GetPersonScorecard(string person, string? filter, string? startDate, string? endDate)
        {
            if (string.IsNullOrEmpty(person))
                return BadRequest(new { error = "person parametresi gerekli" });

            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);

            using var db = _contextFactory.CreateDbContext();

            // All teklifler for this person in date range
            var personTeklifler = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null
                    && t.CreatedBy == person
                    && t.CreatedOn.HasValue
                    && t.CreatedOn.Value >= start
                    && t.CreatedOn.Value <= end)
                .ToListAsync();

            // Funnel metrics
            var totalFirsat = personTeklifler.Count;
            var totalPipeline = personTeklifler.Sum(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m);
            var activeCount = personTeklifler.Count(t => t.Status != null && OpenStatuses.Contains(t.Status));
            var activePipeline = personTeklifler.Where(t => t.Status != null && OpenStatuses.Contains(t.Status)).Sum(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m);
            var wonCount = personTeklifler.Count(t => t.Status != null && WonStatuses.Contains(t.Status));
            var wonValue = personTeklifler.Where(t => t.Status != null && WonStatuses.Contains(t.Status)).Sum(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m);
            var lostCount = personTeklifler.Count(t => t.Status != null && LostStatuses.Contains(t.Status));
            var winRate = (wonCount + lostCount) > 0
                ? Math.Round((decimal)wonCount / (wonCount + lostCount) * 100, 1)
                : 0m;
            var avgDealSize = activeCount > 0 ? Math.Round(activePipeline / activeCount, 2) : 0m;

            // Monthly trend (6 months)
            var trendStart = end.AddMonths(-5);
            trendStart = new DateTime(trendStart.Year, trendStart.Month, 1);
            var monthlyTrend = new List<object>();

            for (int i = 0; i < 6; i++)
            {
                var ms = trendStart.AddMonths(i);
                var me = new DateTime(ms.Year, ms.Month, DateTime.DaysInMonth(ms.Year, ms.Month), 23, 59, 59);
                var monthData = personTeklifler.Where(t => t.CreatedOn >= ms && t.CreatedOn <= me).ToList();

                monthlyTrend.Add(new
                {
                    ay = ms.ToString("MMM yyyy", new System.Globalization.CultureInfo("tr-TR")),
                    firsatAdet = monthData.Count,
                    pipeline = monthData.Where(t => t.Status != null && OpenStatuses.Contains(t.Status)).Sum(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m),
                    kazanilan = monthData.Where(t => t.Status != null && WonStatuses.Contains(t.Status)).Sum(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m)
                });
            }

            // Open deals list
            var openDeals = personTeklifler
                .Where(t => t.Status != null && OpenStatuses.Contains(t.Status))
                .OrderByDescending(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m)
                .Take(20)
                .Select(t => new
                {
                    id = t.Id.ToString(),
                    teklifNo = t.Number ?? "-",
                    musteriAdi = t.Account_Title ?? "-",
                    ad = t.Name ?? "-",
                    tutar = t.TotalNetAmountLocalCurrency_Amount ?? 0m,
                    durum = StatusToTurkishStage(t.Status),
                    tarih = t.CreatedOn,
                    sonGuncelleme = t.ModifiedOn
                })
                .ToList();

            // Customer distribution
            var customerDist = personTeklifler
                .Where(t => t.Account_Title != null)
                .GroupBy(t => t.Account_Title!)
                .Select(g => new { musteri = g.Key, adet = g.Count(), tutar = g.Sum(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m) })
                .OrderByDescending(x => x.tutar)
                .Take(10)
                .ToList();

            // Product performance using TBLSOS_ANA_URUN + TBLSOS_URUN_ESLESTIRME
            var eslestirmeMap = await GetUrunEslestirmeMapAsync();
            var teklifIds = personTeklifler.Select(t => t.Id).ToHashSet();

            var teklifUrunleri = await db.TBL_VARUNA_TEKLIF_URUNLERIs.AsNoTracking()
                .Where(u => u.DeletedOn == null && u.QuoteId != null)
                .Select(u => new { u.QuoteId, u.StockCode, Total = u.NetLineTotalAmountLocal_Amount ?? 0m })
                .ToListAsync();

            var personUrunler = teklifUrunleri
                .Where(u => u.QuoteId.HasValue && teklifIds.Contains(u.QuoteId.Value))
                .Select(u => new { GrupAdi = ResolveProductGroup(u.StockCode, eslestirmeMap), u.Total })
                .GroupBy(x => x.GrupAdi)
                .Select(g => new { urunGrubu = g.Key, adet = g.Count(), tutar = g.Sum(x => x.Total) })
                .OrderByDescending(x => x.tutar)
                .ToList();

            return Json(new
            {
                kisi = person,
                funnel = new
                {
                    toplamFirsat = totalFirsat,
                    toplamPipeline = totalPipeline,
                    aktifAdet = activeCount,
                    aktifPipeline = activePipeline,
                    kazanilanAdet = wonCount,
                    kazanilanTutar = wonValue,
                    kaybedilenAdet = lostCount,
                    kazanmaOrani = winRate,
                    ortAnlasma = avgDealSize
                },
                aylikTrend = monthlyTrend,
                acikAnlasmalar = openDeals,
                musteriDagilimi = customerDist,
                urunPerformansi = personUrunler
            });
        }

        // ═══════════════════════════════════════════════════════════════════
        // OPPORTUNITIES BAZLI ANALİZ ENDPOİNTLERİ
        // OwnerId = Satış Temsilcisi, TBLSOS_CRM_KULLANICI_GECICI ile isim çözümle
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// OwnerId → PersonNameSurname mapping from TBLSOS_CRM_PERSON_ODATA (cached)
        /// </summary>
        private async Task<Dictionary<string, string>> GetOwnerMapAsync()
        {
            if (_cache.TryGetValue("opp_owner_map_v2", out Dictionary<string, string>? cached) && cached != null)
                return cached;

            using var db = _contextFactory.CreateDbContext();
            var map = await db.TBLSOS_CRM_PERSON_ODATAs.AsNoTracking()
                .Where(p => p.PersonNameSurname != null)
                .ToDictionaryAsync(p => p.Id, p => p.PersonNameSurname!);

            _cache.Set("opp_owner_map_v2", map, CacheTTL);
            return map;
        }

        /// <summary>
        /// Dönem hedef tutarı — yeni TBLSOS_HEDEF_URUN_AYLIK (senaryo bazlı) toplamı.
        /// Yoksa eski TBLSOS_HEDEF_AYLIK (Tip=GENEL) fallback. HedefService kendi cache'ini yönetir.
        /// FA bant + KPI core ile Cockpit aynı kaynaktan beslenir.
        /// </summary>
        private async Task<decimal> GetDonemHedefAsync(DateTime start, DateTime end)
        {
            // Aralık tek yıl içindeyse o yıl, aksi halde hem start hem end yılı için topla.
            if (start.Year == end.Year)
                return await _hedef.GetGenelHedefRangeAsync(start.Year, start, end);

            decimal toplam = 0m;
            for (int yil = start.Year; yil <= end.Year; yil++)
            {
                var s = new DateTime(yil, 1, 1);
                var e = new DateTime(yil, 12, 31);
                if (yil == start.Year) s = start;
                if (yil == end.Year)   e = end;
                toplam += await _hedef.GetGenelHedefRangeAsync(yil, s, e);
            }
            return toplam;
        }

        private string ResolveOwnerName(string? ownerId, Dictionary<string, string> ownerMap)
        {
            if (string.IsNullOrEmpty(ownerId)) return "Bilinmiyor";
            return ownerMap.TryGetValue(ownerId, out var name) ? name : ownerId[..8] + "…";
        }

        // ───────────────────────────────────────────────────────────────
        // GET /FirsatAnaliz/GetKpiCore
        // ⚡ Perf: 5 KPI kartı için hızlı payload (sadece SP çağrıları).
        // Tab değişiminde ilk paint bunu bekler; ağır GetOpportunitySummary idle'da yüklenir.
        // ───────────────────────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> GetKpiCore(string? filter, string? startDate, string? endDate, string? owner)
        {
            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);
            var cacheKey = $"FirsatKpiCore_{start:yyyyMMdd}_{end:yyyyMMdd}_{owner ?? "all"}";
            var force = IsForceRefresh();
            if (force) _cockpitData.InvalidateRange(start, end, owner);
            if (!force && _cache.TryGetValue(cacheKey, out object? cached) && cached != null)
                return Json(cached);

            var pipeTask   = _cockpitData.GetPipelineAsync(start, end, owner);
            var faturaTask = _cockpitData.GetFaturaOzetAsync(start, end, owner);
            var hedefTask  = GetDonemHedefAsync(start, end);
            await Task.WhenAll(pipeTask, faturaTask, hedefTask);

            var pipe    = pipeTask.Result;
            var fatura  = faturaTask.Result;
            var hedef   = hedefTask.Result;

            var result = new
            {
                // Kart 1: Tüm fırsatlar (açık havuz) — SP'den
                tumFirsatAdet = pipe.TumFirsatAdet,
                tumFirsatTutar = pipe.TumFirsatTutar,
                // Kart 2: Dönem fırsat (SP) — exclusive pipeline proxy; gerçek exFirsat idle full'de rafine edilir
                exFirsatAdet = pipe.FirsatAdet,
                exFirsatTutar = pipe.FirsatTutar,
                // Kart 3: Dönem teklif (SP)
                exTeklifAdet = pipe.TeklifAdet,
                exTeklifTutar = pipe.TeklifTutar,
                // Kart 4: Açık sipariş (SP)
                exSiparisAdet = pipe.AcikSiparisAdet,
                exSiparisTutar = pipe.AcikSiparisTutar,
                // Kart 5: Faturalanan (SP_COCKPIT_FATURA)
                kapaliSiparisAdet = fatura.Adet,
                kapaliSiparisTutar = fatura.Toplam,
                // Hedef
                hedefTutar = hedef
            };
            _cache.Set(cacheKey, result, CacheTTL);
            return Json(result);
        }

        // ───────────────────────────────────────────────────────────────
        // GET /FirsatAnaliz/GetOpportunitySummary
        // ───────────────────────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> GetOpportunitySummary(string? filter, string? startDate, string? endDate, string? owner)
        {
            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);

            // ── Cache kontrolü ──
            var cacheKey = $"FirsatOppSummary_v3_{start:yyyyMMdd}_{end:yyyyMMdd}_{owner ?? "all"}";
            var summaryForce = IsForceRefresh();
            if (summaryForce) _cockpitData.InvalidateRange(start, end, owner);
            if (!summaryForce && _cache.TryGetValue(cacheKey, out object? cachedResult) && cachedResult != null)
                return Json(cachedResult);



            using var db = _contextFactory.CreateDbContext();
            var ownerMap = await GetOwnerMapAsync();

            // TBL_VARUNA_OPPORTUNITIES — fırsat verileri
            var query = ExcludeTestFirsat(db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking())
                .Where(o => o.CloseDate.HasValue
                    && o.CloseDate.Value >= start && o.CloseDate.Value <= end);

            if (!string.IsNullOrEmpty(owner))
                query = query.Where(o => o.OwnerId == owner);

            var data = await query.Select(o => new
            {
                o.Id,
                o.OwnerId,
                o.OpportunityStageName,
                DealType = o.DealType,
                DealTypeTR = (string?)null,
                o.Probability,
                o.CloseDate,
                CreatedOn = (DateTime?)null,
                o.AmountAmount,
                o.AccountId,
                o.Name
            }).ToListAsync();

            // Lost fırsatları ayır (kartlardan düş, analiz için ayrı tut)
            var donemLost = data.Where(d => d.OpportunityStageName == "Lost"
                || (d.OpportunityStageName != null && d.OpportunityStageName.Contains("Closed"))).ToList();

            // ── SATIŞ HUNİSİ: Fırsat → Teklif → Sipariş → Fatura zinciri ──

            // ── Tahakkuk-aware fırsat filtresi (havuz-seviye, dönem-bağımsız, 5dk cache) ──
            // Kapalı siparişli fırsatların efektif kapanış tarihleri.
            // GetFunnelBreakdown ile aynı haritayı paylaşır — duplicate EF query yok.
            var kapaliOppEfektif = await GetKapaliOppEfektifMapCachedAsync(summaryForce);
            // Aşağıda EfektifInvoice çağrılarında lazım — TahakkukService kendi cache'ini tutar
            var tahakkukMap = await _tahakkukService.GetTahakkukMapAsync();

            // Dönem DIŞINA kayan kapalı fırsatlar → bu dönemden çıkar
            var kapaliSet = kapaliOppEfektif
                .Where(kv => kv.Value.HasValue && (kv.Value.Value < start || kv.Value.Value > end))
                .Select(kv => kv.Key).ToHashSet();
            // Dönem İÇİNE kayan kapalı fırsatlar → bu döneme ekle (CloseDate dönem dışı ama efektif tarih dönem içi)
            var eklenecekSet = kapaliOppEfektif
                .Where(kv => kv.Value.HasValue && kv.Value.Value >= start && kv.Value.Value <= end)
                .Select(kv => kv.Key).ToHashSet();

            // Dönem fırsatları — tahakkukla dönem dışına kayanlar hariç
            var dataAktif = data.Where(d => d.OpportunityStageName != "Lost"
                && (d.OpportunityStageName == null || !d.OpportunityStageName.Contains("Closed"))
                && !kapaliSet.Contains((d.Id ?? "").ToLower()))
                .ToList();

            // Tahakkukla bu döneme kayan Won fırsatları ekle (CloseDate dönem dışı olanlar)
            var dataIdSet = dataAktif.Select(d => (d.Id ?? "").ToLower()).ToHashSet();
            var eklenecekFirsatlar = await ExcludeTestFirsat(db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking())
                .Where(o => o.OpportunityStageName == "Won" && eklenecekSet.Contains(o.Id!.ToLower()))
                .Select(o => new { o.Id, o.OwnerId, o.OpportunityStageName, DealType = o.DealType,
                    DealTypeTR = (string?)null, o.Probability, o.CloseDate,
                    CreatedOn = (DateTime?)null, o.AmountAmount, o.AccountId, o.Name })
                .ToListAsync();
            foreach (var ef in eklenecekFirsatlar)
            {
                if (!dataIdSet.Contains((ef.Id ?? "").ToLower()))
                    dataAktif.Add(ef);
            }

            var toplam = dataAktif.Count;
            var wonList = dataAktif.Where(d => d.OpportunityStageName == "Won").ToList();
            var lostList = donemLost;
            var activeList = dataAktif.Where(d => d.OpportunityStageName != null
                && d.OpportunityStageName != "Won").ToList();
            var kazanmaOrani = (wonList.Count + lostList.Count) > 0
                ? Math.Round((decimal)wonList.Count / (wonList.Count + lostList.Count) * 100, 1) : 0m;
            var toplamFirsatTutar = dataAktif.Sum(d => d.AmountAmount ?? 0m);
            var wonTutar = wonList.Sum(d => d.AmountAmount ?? 0m);
            var lostTutar = lostList.Sum(d => d.AmountAmount ?? 0m);
            var aktivTutar = activeList.Sum(d => d.AmountAmount ?? 0m);
            var kazanmaOraniRevenue = (wonTutar + lostTutar) > 0
                ? Math.Round(wonTutar / (wonTutar + lostTutar) * 100, 1) : 0m;

            // ── PARALEL SORGULAR: Bağımsız sorguları aynı anda çalıştır ──
            using var db2 = _contextFactory.CreateDbContext();
            using var db3 = _contextFactory.CreateDbContext();
            using var db_exSip = _contextFactory.CreateDbContext();
            using var db_exTek = _contextFactory.CreateDbContext();
            using var db_cockpitFat = _contextFactory.CreateDbContext();

            var tumFirsatQuery = ExcludeTestFirsat(db2.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking())
                .Select(o => new { o.Id, o.AmountAmount, o.OpportunityStageName, o.Probability, o.OwnerId });
            if (!string.IsNullOrEmpty(owner))
                tumFirsatQuery = tumFirsatQuery.Where(o => o.OwnerId == owner);
            var tumFirsatTask = tumFirsatQuery.ToListAsync();

            var firsatsizTeklifTask = ExcludeTest(db3.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId == null)
                .GroupBy(t => 1)
                .Select(g => new { Adet = g.Count(), Tutar = g.Sum(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m) })
                .FirstOrDefaultAsync();

            var donemFirsatIdsTask = query
                .Where(o => o.OpportunityStageName != "Lost"
                    && (o.OpportunityStageName == null || !o.OpportunityStageName.Contains("Closed")))
                .Select(o => o.Id).ToListAsync();

            // Exclusive pipeline: Sipariş → fırsat bağlantısı (Open VEYA Closed).
            // Closed siparişli ama faturası dönem dışına kayan fırsatlar, exFatura'da olmadığı için
            // exSipariş'e (Kabul edildi) atanır — exTeklif'te (Beklemede) mükerrer sayılmaz.
            // GetExclusiveSetsAsync ile birebir tutarlı.
            var openSiparisOppTask = ExcludeTest(db_exSip.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue)
                .Join(ExcludeTestSiparis(db_exSip.TBL_VARUNA_SIPARIs.AsNoTracking())
                    .Where(s => (s.OrderStatus == "Open" || s.OrderStatus == "Closed") && s.QuoteId != null),
                    t => t.Id.ToString(), s => s.QuoteId,
                    (t, s) => t.OpportunityId!.Value.ToString().ToLower())
                .Distinct()
                .ToListAsync();

            // Exclusive pipeline: Aktif teklif → fırsat bağlantısı (tüm zamanlar)
            var aktifTeklifOppTask = ExcludeTest(db_exTek.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue
                    && (t.Status == null || (t.Status != "Reject" && t.Status != "Denied" && t.Status != "Closed")))
                .Select(t => t.OpportunityId!.Value.ToString().ToLower())
                .Distinct()
                .ToListAsync();

            // Exclusive pipeline K5: Cockpit fatura → fırsat bağlantısı (+ sipariş SAP tipi)
            var cockpitFaturaTask = _cockpitData.GetFaturalarAsync(start, end, owner);
            var cockpitFaturaOppTask =
                // Dual-key: sentetik faturalar (SerialNumber NULL, SAPOutReferenceCode dolu) da yakalanmalı.
                (from t in ExcludeTest(db_cockpitFat.TBL_VARUNA_TEKLIFs.AsNoTracking())
                 where t.DeletedOn == null && t.OpportunityId.HasValue
                 join s in ExcludeTestSiparis(db_cockpitFat.TBL_VARUNA_SIPARIs.AsNoTracking())
                        .Where(s => s.OrderStatus == "Closed"
                                    && (s.SerialNumber != null || s.SAPOutReferenceCode != null))
                     on t.Id.ToString() equals s.QuoteId
                 join d in db_cockpitFat.TBL_VARUNA_SALESDOCUMENTTYPESAPs.AsNoTracking()
                     on s.SalesDocumentTypeSapId equals d.Id into dJ
                 from d in dJ.DefaultIfEmpty()
                 select new {
                     OppId = t.OpportunityId!.Value.ToString().ToLower(),
                     s.SerialNumber,
                     s.SAPOutReferenceCode,
                     SapCode = d != null ? d.Code : null
                 }).ToListAsync();

            // TÜM-ZAMAN fatura kesilmiş opportunity'ler — exTeklif/exSiparis'ten çıkarmak için.
            // Cache'li helper (10 dk TTL); paralel çağrı yapmıyoruz, cache hit sonrası anlık dönüyor.
            var allTimeFaturaOppTask = GetAllTimeFaturaOppSetAsync();

            await Task.WhenAll(tumFirsatTask, firsatsizTeklifTask, donemFirsatIdsTask, openSiparisOppTask, aktifTeklifOppTask, cockpitFaturaTask, cockpitFaturaOppTask, allTimeFaturaOppTask);

            var tumFirsatlar = tumFirsatTask.Result;
            var firsatsizData = firsatsizTeklifTask.Result;
            var firsatsizTeklifAdet = firsatsizData?.Adet ?? 0;
            var firsatsizTeklifTutar = firsatsizData?.Tutar ?? 0m;

            // Açık havuz: Lost + Won + Closed hariç (gerçek açık fırsatlar)
            var excludeStages = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Lost", "Won" };
            var acikFirsatlar = tumFirsatlar
                .Where(o => !kapaliSet.Contains((o.Id ?? "").ToLower())
                    && !excludeStages.Contains(o.OpportunityStageName ?? "")
                    && (o.OpportunityStageName == null || !o.OpportunityStageName.Contains("Closed")))
                .ToList();
            var tumFirsatAdet = acikFirsatlar.Count;
            var tumFirsatTutar = acikFirsatlar.Sum(o => o.AmountAmount ?? 0m);

            // Kaybedilen + Kazanılan analizi (ayrı veri — UI'da gösterilecek)
            var lostFirsatlar = tumFirsatlar.Where(o => string.Equals(o.OpportunityStageName, "Lost", StringComparison.OrdinalIgnoreCase)).ToList();
            var lostAdet = lostFirsatlar.Count;
            var lostHavuzTutar = lostFirsatlar.Sum(o => o.AmountAmount ?? 0m);
            var wonFirsatlar = tumFirsatlar.Where(o => string.Equals(o.OpportunityStageName, "Won", StringComparison.OrdinalIgnoreCase)).ToList();
            var wonHavuzAdet = wonFirsatlar.Count;
            var wonHavuzTutar = wonFirsatlar.Sum(o => o.AmountAmount ?? 0m);

            // Dönemdeki fırsatların ID'leri — kapalı siparişli olanları düş + tahakkukla bu döneme kayanları ekle
            var donemFirsatIds = donemFirsatIdsTask.Result
                .Where(id => !kapaliSet.Contains((id ?? "").ToLower())).ToList();
            // Tahakkukla bu döneme kayan Won fırsatları ekle
            var donemIdSetCheck = donemFirsatIds.Select(id => (id ?? "").ToLower()).ToHashSet();
            foreach (var ekId in eklenecekSet)
                if (!donemIdSetCheck.Contains(ekId)) donemFirsatIds.Add(ekId);
            var donemFirsatGuidSet = donemFirsatIds
                .Where(id => Guid.TryParse(id, out _))
                .Select(id => Guid.Parse(id))
                .ToHashSet();

            // Dönemdeki TÜM teklifler (fırsata bağlı olsun olmasın — zincir zorunlu değil)
            var donemTeklifler = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null
                    && t.CreatedOn.HasValue && t.CreatedOn.Value >= start && t.CreatedOn.Value <= end)
                .Select(t => new { t.Id, t.TotalNetAmountLocalCurrency_Amount, t.Status, t.OpportunityId, t.CreatedOn })
                .ToListAsync();
            // Teklif: Reject/Denied/Closed hariç
            var lostTeklifStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Reject", "Denied", "Closed" };
            var aktifTeklifler = donemTeklifler.Where(t => !lostTeklifStatuses.Contains(t.Status ?? "")).ToList();
            var lostTeklifler = donemTeklifler.Where(t => lostTeklifStatuses.Contains(t.Status ?? "")).ToList();
            var teklifToplam = aktifTeklifler.Count;
            var teklifTutar = aktifTeklifler.Sum(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m);
            var lostTeklifAdet = lostTeklifler.Count;
            var lostTeklifTutar = lostTeklifler.Sum(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m);

            // Ağırlıklı potansiyel: tumFirsatlar zaten memory'de — DB'ye gitmeden filtrele
            var donemIdSet = donemFirsatIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var firsatProbMap = tumFirsatlar
                .Where(o => o.Id != null && donemIdSet.Contains(o.Id))
                .ToDictionary(o => o.Id!, o => o.Probability ?? 0m);

            // Ağırlıklı potansiyel: sadece AKTİF teklifler (Reject/Denied/Closed hariç)
            // ÖNEMLİ: Yalnızca olasılığı ≥ %90 olan teklifler hesaba katılır (kullanıcı talebi).
            //   Yüksek güvenli (yakında kapanacak) tekliflere odaklanır; gürültüyü temizler.
            const decimal HIGH_PROB_THRESHOLD = 90m;
            var aktifTeklifProb = aktifTeklifler.Select(t => new
            {
                Tutar = t.TotalNetAmountLocalCurrency_Amount ?? 0m,
                Prob = t.OpportunityId.HasValue && firsatProbMap.TryGetValue(t.OpportunityId.Value.ToString(), out var p) ? p : 0m
            }).ToList();
            var yuksekOlasilikli = aktifTeklifProb.Where(x => x.Prob >= HIGH_PROB_THRESHOLD).ToList();
            var agirlikliPotansiyel = yuksekOlasilikli.Sum(x => x.Tutar * x.Prob / 100m);
            var agirlikliPotansiyelAdet = yuksekOlasilikli.Count;
            var yuksekOlasilikliTutar = yuksekOlasilikli.Sum(x => x.Tutar);  // Ham tutar (ağırlıksız)
            var ortOlasilik = 0m;  // Artık kullanılmıyor — ortalama yerine sayı gösterilir

            // Sipariş kartı: tahakkuk tutarlı filtreleme
            // Closed → EfektifTarih (tahakkuk override) dönemde ise dahil
            // Open → CreateOrderDate dönemde ise dahil (henüz fatura yok)
            // ⚡ Perf: DB-side tarih pencere filtresi — tahakkuk override payı için Closed'larda -6/+1 ay
            var tahFrom = start.AddMonths(-6);
            var tahTo   = end.AddMonths(1);
            var donemSiparislerRaw = await ExcludeTestSiparis(db.TBL_VARUNA_SIPARIs.AsNoTracking())
                .Where(s => s.OrderStatus != "Canceled"
                    && (
                        (s.OrderStatus == "Closed"
                            && s.InvoiceDate.HasValue
                            && s.InvoiceDate.Value >= tahFrom && s.InvoiceDate.Value <= tahTo)
                        ||
                        (s.OrderStatus != "Closed"
                            && s.CreateOrderDate.HasValue
                            && s.CreateOrderDate.Value >= start && s.CreateOrderDate.Value <= end)
                    ))
                .Select(s => new { s.SerialNumber, s.SAPOutReferenceCode, s.TotalNetAmount, s.OrderStatus, s.InvoiceDate, s.CreateOrderDate })
                .ToListAsync();
            var donemSiparisler = donemSiparislerRaw.Select(s => new {
                s.SerialNumber,
                s.TotalNetAmount,
                s.OrderStatus,
                EfektifTarih = EfektifInvoice(s.SerialNumber, s.SAPOutReferenceCode, s.InvoiceDate, tahakkukMap),
                s.CreateOrderDate
            })
            .Where(s =>
                // Closed sipariş: sadece efektif fatura tarihi dönemde (tahakkuk override geçerli)
                (s.OrderStatus == "Closed" && s.EfektifTarih.HasValue && s.EfektifTarih.Value >= start && s.EfektifTarih.Value <= end)
                // Open sipariş: CreateOrderDate dönemde (henüz faturası yok)
                || (s.OrderStatus == "Open" && s.CreateOrderDate.HasValue && s.CreateOrderDate.Value >= start && s.CreateOrderDate.Value <= end))
            .ToList();
            var acikSiparisAdet = donemSiparisler.Count(s => s.OrderStatus == "Open");
            var acikSiparisTutar = donemSiparisler.Where(s => s.OrderStatus == "Open").Sum(s => s.TotalNetAmount ?? 0m);
            var faturalanmisAdet = donemSiparisler.Count(s => s.OrderStatus == "Closed");
            var faturalanmisTutar = donemSiparisler.Where(s => s.OrderStatus == "Closed").Sum(s => s.TotalNetAmount ?? 0m);
            var toplamSiparisAdet = donemSiparisler.Count;
            var toplamSiparisTutar = donemSiparisler.Sum(s => s.TotalNetAmount ?? 0m);

            // ── EXCLUSIVE PIPELINE SETLERI ──
            // Her fırsat sadece EN İLERİ aşamasındaki kartta görünür
            // K5 cockpit'ten, K2-K4 o.CloseDate bazlı
            var openSiparisOppSet = openSiparisOppTask.Result.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var aktifTeklifOppSet = aktifTeklifOppTask.Result.ToHashSet(StringComparer.OrdinalIgnoreCase);

            // K5 — FaturaSet: Cockpit faturalarına bağlı fırsatlar
            // Cockpit FaturaNo → TBL_VARUNA_SIPARIS.SerialNumber → TBL_VARUNA_TEKLIF.OpportunityId
            var cockpitFaturalar = cockpitFaturaTask.Result;
            var cockpitFaturaNoSet = cockpitFaturalar.Select(f => f.FaturaNo).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var cockpitFaturaOppMap = cockpitFaturaOppTask.Result;
            // Dual-key: cockpitFaturaNoSet hem gerçek SerialNumber hem sentetik "SAP:<ref>" formatı taşıyor.
            var directSerialSet_xF = cockpitFaturaNoSet
                .Where(f => !string.IsNullOrEmpty(f) && !f.StartsWith("SAP:", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sapRefSet_xF = cockpitFaturaNoSet
                .Where(f => !string.IsNullOrEmpty(f) && f.StartsWith("SAP:", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Substring(4))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var cockpitFaturaOppIds = cockpitFaturaOppMap
                .Where(x =>
                    (x.SerialNumber != null && directSerialSet_xF.Contains(x.SerialNumber))
                    || (x.SAPOutReferenceCode != null && sapRefSet_xF.Contains(x.SAPOutReferenceCode)))
                .Select(x => x.OppId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var exFaturaIds = dataAktif
                .Where(d => cockpitFaturaOppIds.Contains((d.Id ?? "").ToLower()))
                .Select(d => (d.Id ?? "").ToLower())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Tüm-zaman fatura kesilmiş opp'lar — bu opp'lar hiçbir dönemde teklif/sipariş sayılmamalı.
            var allTimeFaturaOppSet = allTimeFaturaOppTask.Result;

            // K4 — SiparisSet: Open siparişi olan, FaturaSet (dönem + tüm-zaman) hariç
            var exSiparisIds = dataAktif
                .Where(d => openSiparisOppSet.Contains((d.Id ?? "").ToLower())
                    && !exFaturaIds.Contains((d.Id ?? "").ToLower())
                    && !allTimeFaturaOppSet.Contains((d.Id ?? "").ToLower()))
                .Select(d => (d.Id ?? "").ToLower())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // K3 — TeklifSet: Aktif teklifi olan, Fatura (dönem + tüm-zaman) + Sipariş hariç
            var exTeklifIds = dataAktif
                .Where(d => aktifTeklifOppSet.Contains((d.Id ?? "").ToLower())
                    && !exFaturaIds.Contains((d.Id ?? "").ToLower())
                    && !exSiparisIds.Contains((d.Id ?? "").ToLower())
                    && !allTimeFaturaOppSet.Contains((d.Id ?? "").ToLower()))
                .Select(d => (d.Id ?? "").ToLower())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // K2 — FirsatSet: Hiçbir bağlantısı yok (teklif, sipariş, fatura yok)
            var exFirsatIds = dataAktif
                .Where(d => !exFaturaIds.Contains((d.Id ?? "").ToLower())
                    && !exSiparisIds.Contains((d.Id ?? "").ToLower())
                    && !exTeklifIds.Contains((d.Id ?? "").ToLower()))
                .Select(d => (d.Id ?? "").ToLower())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Exclusive tutarlar (K5 tutar cockpit'ten, K2-K4 fırsat tutarından)
            var exFaturaTutar = cockpitFaturalar.Sum(f => f.NetTutar);
            var exSiparisTutar = dataAktif.Where(d => exSiparisIds.Contains((d.Id ?? "").ToLower())).Sum(d => d.AmountAmount ?? 0m);
            var exTeklifTutar = dataAktif.Where(d => exTeklifIds.Contains((d.Id ?? "").ToLower())).Sum(d => d.AmountAmount ?? 0m);
            var exFirsatTutar = dataAktif.Where(d => exFirsatIds.Contains((d.Id ?? "").ToLower())).Sum(d => d.AmountAmount ?? 0m);

            // ── FUNNEL: Cockpit fatura BASE + o.CloseDate aşamaları yukarı birikir ──
            // K5 = cockpit fatura (sabit)
            // K4 = K5 + exclusive sipariş
            // K3 = K4 + exclusive teklif
            // K2 = K3 + exclusive fırsat
            // → K2 > K3 > K4 > K5 her zaman garanti
            var fnlK5Adet = cockpitFaturalar.Count;
            var fnlK5Tutar = cockpitFaturalar.Sum(f => f.NetTutar);
            var fnlK4Adet = fnlK5Adet + exSiparisIds.Count;
            var fnlK4Tutar = fnlK5Tutar + exSiparisTutar;
            var fnlK3Adet = fnlK4Adet + exTeklifIds.Count;
            var fnlK3Tutar = fnlK4Tutar + exTeklifTutar;
            var fnlK2Adet = fnlK3Adet + exFirsatIds.Count;
            var fnlK2Tutar = fnlK3Tutar + exFirsatTutar;

            // Aşama dağılımı
            var stageDagilim = data
                .GroupBy(d => d.OpportunityStageName ?? "Bilinmiyor")
                .Select(g => new { asama = g.Key, adet = g.Count() })
                .OrderByDescending(x => x.adet)
                .ToList();

            // Anlaşma tipi dağılımı
            var dealTypeDagilim = data
                .GroupBy(d => d.DealTypeTR ?? d.DealType ?? "Bilinmiyor")
                .Select(g => new { tip = g.Key, adet = g.Count() })
                .OrderByDescending(x => x.adet)
                .ToList();

            // ── Fatura × Sipariş SAP tipi breakdown (Yenileme vs Yeni Satış) ──
            // Her SP fatura → sipariş.SalesDocumentTypeSapId → SAPDocumentType.Code zinciri.
            // Yenileme = ZZ08 (Yenileme Satış) + ZZ12 (Yeni Mevcut Ref.); diğer tüm ZZxx = Yeni Satış.
            // Zincir kopuksa (SerialNumber eşleşmiyorsa) → Yeni Satış varsayılan.
            var yenilemeKodlari = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ZZ08", "ZZ12" };
            // FaturaNo formatı dual-key olduğu için lookup hem gerçek SerialNumber hem "SAP:<ref>" ile çalışmalı.
            var serialToSapCode = cockpitFaturaOppMap
                .Select(x => new
                {
                    Key = !string.IsNullOrEmpty(x.SerialNumber)
                        ? x.SerialNumber!
                        : (!string.IsNullOrEmpty(x.SAPOutReferenceCode) ? "SAP:" + x.SAPOutReferenceCode : null),
                    x.SapCode
                })
                .Where(x => !string.IsNullOrEmpty(x.Key))
                .GroupBy(x => x.Key!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => x.SapCode).FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? "",
                    StringComparer.OrdinalIgnoreCase);

            decimal faturaYenilemeTutar = 0m;  int faturaYenilemeAdet  = 0;
            decimal faturaYeniSatisTutar= 0m;  int faturaYeniSatisAdet = 0;
            foreach (var f in cockpitFaturalar)
            {
                if (string.IsNullOrEmpty(f.FaturaNo)) continue;
                var code = serialToSapCode.GetValueOrDefault(f.FaturaNo, "");
                if (yenilemeKodlari.Contains(code))
                {
                    faturaYenilemeTutar += f.NetTutar;
                    faturaYenilemeAdet++;
                }
                else
                {
                    faturaYeniSatisTutar += f.NetTutar;
                    faturaYeniSatisAdet++;
                }
            }
            decimal faturaDigerTutar = 0m; int faturaDigerAdet = 0;  // eski alan — artık kullanılmıyor

            // ─── SUB-FUNNEL PARTITION (Yenileme + Yeni Satış) ───
            // Üst funnel'ın aynı kümülatif K5→K2 mantığını DealType + DocCode bazında 2'ye böl.
            // Garantili matematik: Yen K2/K3/K5 + YS K2/K3/K5 = Üst K2/K3/K5 (birebir).
            //   K5 partition: cockpit fatura DocCode bazlı (ZZ08 = Yenileme, diğer = Yeni Satış)
            //   K4/K3/K2 partition: exSiparis/exTeklif/exFirsat × DealType (Renovation+AdditionalUsage = Yenileme)
            // Kanonik Yenileme = ZZ08 only (ZZ12 hariç — FA tutarsızlığı ayrı issue).
            var renovationDealTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Renovation", "AdditionalUsage" };
            var dataDealTypeMap = dataAktif
                .Where(d => !string.IsNullOrEmpty(d.Id))
                .ToDictionary(d => d.Id!.ToLower(), d => d.DealType ?? "", StringComparer.OrdinalIgnoreCase);
            bool IsYenOpp(string id) => dataDealTypeMap.TryGetValue(id, out var dt) && renovationDealTypes.Contains(dt);

            // ─── Cockpit fatura DocCode lookup — TEKLIF aracısız, direkt SİPARİŞ tablosundan ───
            // serialToSapCode TEKLIF.OpportunityId.HasValue filtreli; fırsatsız ZZ08 faturaları yakalayamıyor.
            // Bu blok cockpit fatura'ları doğrudan TBL_VARUNA_SIPARIS ile eşleştirip DocCode çeker.
            var faturaSerialKeys = cockpitFaturalar
                .Where(f => !string.IsNullOrEmpty(f.FaturaNo) && !f.FaturaNo.StartsWith("SAP:", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.FaturaNo).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var faturaSapKeys = cockpitFaturalar
                .Where(f => !string.IsNullOrEmpty(f.FaturaNo) && f.FaturaNo.StartsWith("SAP:", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.FaturaNo.Substring(4)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            using var db_docLookup = _contextFactory.CreateDbContext();
            var docCodeRows = await (
                from s in ExcludeTestSiparis(db_docLookup.TBL_VARUNA_SIPARIs.AsNoTracking())
                join dt in db_docLookup.TBL_VARUNA_SALESDOCUMENTTYPESAPs.AsNoTracking()
                    on s.SalesDocumentTypeSapId equals dt.Id into dtj
                from dt in dtj.DefaultIfEmpty()
                where s.OrderStatus == "Closed"
                      && ((s.SerialNumber != null && faturaSerialKeys.Contains(s.SerialNumber))
                       || (s.SAPOutReferenceCode != null && faturaSapKeys.Contains(s.SAPOutReferenceCode)))
                select new { s.SerialNumber, s.SAPOutReferenceCode, Code = dt != null ? dt.Code : null }
            ).ToListAsync();

            var faturaDocCodeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in docCodeRows)
            {
                var key = !string.IsNullOrEmpty(r.SerialNumber) && faturaSerialKeys.Contains(r.SerialNumber)
                    ? r.SerialNumber
                    : (!string.IsNullOrEmpty(r.SAPOutReferenceCode) ? "SAP:" + r.SAPOutReferenceCode : null);
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(r.Code))
                    faturaDocCodeMap[key!] = r.Code!;
            }

            // K5 partition: cockpit fatura DocCode (ZZ08 only)
            int subYenK5Adet = 0, subYsK5Adet = 0;
            decimal subYenK5Tutar = 0m, subYsK5Tutar = 0m;
            foreach (var f in cockpitFaturalar)
            {
                if (string.IsNullOrEmpty(f.FaturaNo)) continue;
                var code = faturaDocCodeMap.GetValueOrDefault(f.FaturaNo, "");
                if (string.Equals(code, "ZZ08", StringComparison.OrdinalIgnoreCase))
                { subYenK5Adet++; subYenK5Tutar += f.NetTutar; }
                else
                { subYsK5Adet++;  subYsK5Tutar  += f.NetTutar; }
            }

            // K4/K3/K2 partition: exSet × DealType
            int subYenExSiparisAdet = exSiparisIds.Count(id => IsYenOpp(id));
            int subYsExSiparisAdet  = exSiparisIds.Count - subYenExSiparisAdet;
            decimal subYenExSiparisTutar = dataAktif
                .Where(d => exSiparisIds.Contains((d.Id ?? "").ToLower()) && IsYenOpp((d.Id ?? "").ToLower()))
                .Sum(d => d.AmountAmount ?? 0m);
            decimal subYsExSiparisTutar  = exSiparisTutar - subYenExSiparisTutar;

            int subYenExTeklifAdet = exTeklifIds.Count(id => IsYenOpp(id));
            int subYsExTeklifAdet  = exTeklifIds.Count - subYenExTeklifAdet;
            decimal subYenExTeklifTutar = dataAktif
                .Where(d => exTeklifIds.Contains((d.Id ?? "").ToLower()) && IsYenOpp((d.Id ?? "").ToLower()))
                .Sum(d => d.AmountAmount ?? 0m);
            decimal subYsExTeklifTutar  = exTeklifTutar - subYenExTeklifTutar;

            int subYenExFirsatAdet = exFirsatIds.Count(id => IsYenOpp(id));
            int subYsExFirsatAdet  = exFirsatIds.Count - subYenExFirsatAdet;
            decimal subYenExFirsatTutar = dataAktif
                .Where(d => exFirsatIds.Contains((d.Id ?? "").ToLower()) && IsYenOpp((d.Id ?? "").ToLower()))
                .Sum(d => d.AmountAmount ?? 0m);
            decimal subYsExFirsatTutar  = exFirsatTutar - subYenExFirsatTutar;

            // Kümülatif: K4 = K5 + exSiparis; K3 = K4 + exTeklif; K2 = K3 + exFirsat
            int subYenK4Adet = subYenK5Adet + subYenExSiparisAdet;
            int subYsK4Adet  = subYsK5Adet  + subYsExSiparisAdet;
            decimal subYenK4Tutar = subYenK5Tutar + subYenExSiparisTutar;
            decimal subYsK4Tutar  = subYsK5Tutar  + subYsExSiparisTutar;

            int subYenK3Adet = subYenK4Adet + subYenExTeklifAdet;
            int subYsK3Adet  = subYsK4Adet  + subYsExTeklifAdet;
            decimal subYenK3Tutar = subYenK4Tutar + subYenExTeklifTutar;
            decimal subYsK3Tutar  = subYsK4Tutar  + subYsExTeklifTutar;

            int subYenK2Adet = subYenK3Adet + subYenExFirsatAdet;
            int subYsK2Adet  = subYsK3Adet  + subYsExFirsatAdet;
            decimal subYenK2Tutar = subYenK3Tutar + subYenExFirsatTutar;
            decimal subYsK2Tutar  = subYsK3Tutar  + subYsExFirsatTutar;

            // ── PARALEL GRUP 2: Yıllık trend sorguları ──
            var yil = DateTime.Now.Year;
            using var db4 = _contextFactory.CreateDbContext();
            using var db5 = _contextFactory.CreateDbContext();

            var tumYilFirsatTask = ExcludeTestFirsat(db4.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking())
                .Where(o => o.CloseDate.HasValue && o.CloseDate.Value.Year == yil
                    && o.OpportunityStageName != "Lost")
                .Select(o => new { o.Id, o.CloseDate, o.OpportunityStageName, o.AmountAmount })
                .ToListAsync();

            // Tüm yıl teklif/sipariş — fırsata bağlı; gruplama fırsatın CloseDate'i üzerinden yapılacak
            var tumYilTeklifTask = ExcludeTest(db5.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue)
                .Select(t => new { t.Id, t.OpportunityId, t.TotalNetAmountLocalCurrency_Amount })
                .ToListAsync();
            using var db6 = _contextFactory.CreateDbContext();
            var tumYilSiparisTask = ExcludeTestSiparis(db6.TBL_VARUNA_SIPARIs.AsNoTracking())
                .Where(s => s.OrderStatus != "Canceled" && s.QuoteId != null)
                .Select(s => new { s.QuoteId, s.OrderStatus, s.TotalNetAmount })
                .ToListAsync();

            await Task.WhenAll(tumYilFirsatTask, tumYilTeklifTask, tumYilSiparisTask);

            var tumYilFirsatlar = tumYilFirsatTask.Result;
            // Kapalı siparişi olanları da düş
            var tumYilAktif = tumYilFirsatlar.Where(o => !kapaliSet.Contains((o.Id ?? "").ToLower())).ToList();

            // Fırsat → ay anahtarı (CloseDate)
            string OppAyKey(DateTime closeDate) => $"{closeDate.Year}-{closeDate.Month:D2}";
            var oppCloseAy = tumYilAktif
                .Where(o => o.CloseDate.HasValue && !string.IsNullOrEmpty(o.Id))
                .ToDictionary(o => o.Id!.ToLower(), o => OppAyKey(o.CloseDate!.Value));

            var aylikFirsatlar = tumYilAktif
                .GroupBy(d => OppAyKey(d.CloseDate!.Value))
                .ToDictionary(g => g.Key, g => new { toplam = g.Count(), won = g.Count(d => d.OpportunityStageName == "Won"), lost = 0, tutar = g.Sum(d => d.AmountAmount ?? 0m) });

            // TEKLİF: bağlı fırsatın CloseDate'i ay anahtarı
            var tumYilTeklifler = tumYilTeklifTask.Result;
            var teklifAyMap = new Dictionary<Guid, string>();   // Teklif.Id → ay
            var aylikTeklifAccum = new Dictionary<string, (int adet, decimal tutar)>();
            foreach (var t in tumYilTeklifler)
            {
                if (!t.OpportunityId.HasValue) continue;
                var oppKey = t.OpportunityId.Value.ToString().ToLower();
                if (!oppCloseAy.TryGetValue(oppKey, out var ay)) continue; // bağlı fırsat aktif yıl listesinde değilse atla
                teklifAyMap[t.Id] = ay;
                var tutar = t.TotalNetAmountLocalCurrency_Amount ?? 0m;
                if (aylikTeklifAccum.TryGetValue(ay, out var cur)) aylikTeklifAccum[ay] = (cur.adet + 1, cur.tutar + tutar);
                else aylikTeklifAccum[ay] = (1, tutar);
            }
            var aylikTeklifData = aylikTeklifAccum.ToDictionary(kv => kv.Key, kv => new { adet = kv.Value.adet, tutar = kv.Value.tutar });

            // SİPARİŞ: QuoteId → Teklif → bağlı fırsatın CloseDate'i ay anahtarı
            var tumYilSiparisler = tumYilSiparisTask.Result;
            var aylikSiparisAccum = new Dictionary<string, (int acik, int kapali, decimal acikT, decimal kapaliT)>();
            foreach (var s in tumYilSiparisler)
            {
                if (string.IsNullOrEmpty(s.QuoteId)) continue;
                if (!Guid.TryParse(s.QuoteId, out var qid)) continue;
                if (!teklifAyMap.TryGetValue(qid, out var ay)) continue;
                var tutar = s.TotalNetAmount ?? 0m;
                var isAcik = s.OrderStatus == "Open";
                var isKapali = s.OrderStatus == "Closed";
                aylikSiparisAccum.TryGetValue(ay, out var cur);
                aylikSiparisAccum[ay] = (
                    cur.acik + (isAcik ? 1 : 0),
                    cur.kapali + (isKapali ? 1 : 0),
                    cur.acikT + (isAcik ? tutar : 0m),
                    cur.kapaliT + (isKapali ? tutar : 0m)
                );
            }
            var aylikSiparisData = aylikSiparisAccum.ToDictionary(kv => kv.Key, kv => new {
                acik = kv.Value.acik,
                kapali = kv.Value.kapali,
                acikTutar = kv.Value.acikT,
                kapaliTutar = kv.Value.kapaliT
            });

            // Ocak-Aralık tüm aylar (boş olanlar da dahil)
            var allMonths = Enumerable.Range(1, 12).Select(m => $"{yil}-{m:D2}").ToList();
            var aylikTrend = allMonths.Select(ay => new {
                ay,
                firsatAdet = aylikFirsatlar.TryGetValue(ay, out var f) ? f.toplam : 0,
                firsatTutar = aylikFirsatlar.TryGetValue(ay, out var f2) ? f2.tutar : 0m,
                won = aylikFirsatlar.TryGetValue(ay, out var f3) ? f3.won : 0,
                lost = aylikFirsatlar.TryGetValue(ay, out var f4) ? f4.lost : 0,
                teklifAdet = aylikTeklifData.TryGetValue(ay, out var t) ? t.adet : 0,
                teklifTutar = aylikTeklifData.TryGetValue(ay, out var t2) ? t2.tutar : 0m,
                acikSiparis = aylikSiparisData.TryGetValue(ay, out var s) ? s.acik : 0,
                acikSiparisTutar = aylikSiparisData.TryGetValue(ay, out var s2) ? s2.acikTutar : 0m,
                kapaliSiparis = aylikSiparisData.TryGetValue(ay, out var s3) ? s3.kapali : 0,
                kapaliSiparisTutar = aylikSiparisData.TryGetValue(ay, out var s4) ? s4.kapaliTutar : 0m
            }).ToList();

            // ── Pipeline + Fatura + Hedef paralel ──
            var pipeTask2 = _cockpitData.GetPipelineAsync(start, end, owner);
            var faturaOzetTask2 = _cockpitData.GetFaturaOzetAsync(start, end, owner);
            var hedefTask2 = GetDonemHedefAsync(start, end);
            await Task.WhenAll(pipeTask2, faturaOzetTask2, hedefTask2);
            var pipe = pipeTask2.Result;
            var faturaOzet = faturaOzetTask2.Result;
            var hedefTutar = hedefTask2.Result;

            // Exclusive set kontrolü
            var exTotal = exFaturaIds.Count + exSiparisIds.Count + exTeklifIds.Count + exFirsatIds.Count;
            if (exTotal != dataAktif.Count)
            {
                var logger = HttpContext.RequestServices.GetService<ILogger<FirsatAnalizController>>();
                logger?.LogWarning("Exclusive pipeline uyumsuzluk: {ExTotal} != {DonemToplam} (Fatura:{F} Sipariş:{S} Teklif:{T} Fırsat:{Fi})",
                    exTotal, dataAktif.Count, exFaturaIds.Count, exSiparisIds.Count, exTeklifIds.Count, exFirsatIds.Count);
            }

            var result = new
            {
                // Kart 1: Tüm fırsatlar (SP'den)
                tumFirsatAdet = pipe.TumFirsatAdet,
                tumFirsatTutar = pipe.TumFirsatTutar,
                firsatsizTeklifAdet,
                firsatsizTeklifTutar,
                gecenDonemFatura = 0m,
                // Kart 1 footer: Kaybedilen + Kazanılan analizi (mevcut sorgulardan)
                lostAdet,
                lostTutar = lostHavuzTutar,
                wonHavuzAdet,
                wonHavuzTutar,
                donemLostAdet = donemLost.Count,
                donemLostTutar = donemLost.Sum(d => d.AmountAmount ?? 0m),
                // Kart 2: Dönem fırsat (SP'den)
                toplam = pipe.FirsatAdet,
                aktif = activeList.Count,
                won = wonList.Count,
                lost = lostList.Count,
                kazanmaOrani,
                toplamTutar = pipe.FirsatTutar,
                wonTutar,
                donemLostRevenue = lostTutar,
                aktivTutar,
                kazanmaOraniRevenue,
                // Kart 3: Dönem teklif (SP'den)
                teklifToplam = pipe.TeklifAdet,
                teklifTutar = pipe.TeklifTutar,
                lostTeklifAdet,
                lostTeklifTutar,
                // Potansiyel (sadece olasılığı ≥ %90 olan aktif teklifler)
                agirlikliPotansiyel,
                agirlikliPotansiyelAdet,
                yuksekOlasilikliTutar,
                ortOlasilik = Math.Round(ortOlasilik, 1),
                // Kart 4: Dönem sipariş (SP'den)
                toplamSiparisAdet = pipe.AcikSiparisAdet,
                toplamSiparisTutar = pipe.AcikSiparisTutar,
                acikSiparisAdet = pipe.AcikSiparisAdet,
                acikSiparisTutar = pipe.AcikSiparisTutar,
                faturalanmisAdet,
                faturalanmisTutar,
                // Kart 5: Faturalanan (Cockpit fatura — tahakkuk düşülmüş, Varuna dışı hariç)
                kapaliSiparisAdet = faturaOzet.Adet,
                kapaliSiparisTutar = faturaOzet.Toplam,
                gecenDonemFaturaOzet = 0m,
                // Fatura DealType breakdown
                faturaYenilemeTutar,  faturaYenilemeAdet,
                faturaYeniSatisTutar, faturaYeniSatisAdet,
                faturaDigerTutar,     faturaDigerAdet,
                // Hedef (DB'den)
                hedefTutar,
                // ── Exclusive pipeline ──
                exFirsatAdet = exFirsatIds.Count,
                exFirsatTutar,
                exTeklifAdet = exTeklifIds.Count,
                exTeklifTutar,
                exSiparisAdet = exSiparisIds.Count,
                exSiparisTutar,
                exFaturaAdet = faturaOzet.Adet,
                exFaturaTutar,
                exFaturaSPTutar = faturaOzet.Toplam,
                exFaturaSPAdet = faturaOzet.Adet,
                exDonemToplam = dataAktif.Count,
                teklifeGecen = exTeklifIds.Count + exSiparisIds.Count + exFaturaIds.Count,
                sipariseGecen = exSiparisIds.Count + exFaturaIds.Count,
                faturayaGecen = exFaturaIds.Count,
                // ── Funnel (cumulative: büyükten küçüğe) ──
                fnlK2Adet, fnlK2Tutar,
                fnlK3Adet, fnlK3Tutar,
                fnlK4Adet, fnlK4Tutar,
                fnlK5Adet, fnlK5Tutar,
                // ── Sub-funnel (Yenileme + Yeni Satış) — Üst funnel ile birebir uyumlu partition ──
                subFunnel = new
                {
                    yenileme = new
                    {
                        fnlK2Adet = subYenK2Adet, fnlK2Tutar = subYenK2Tutar,
                        fnlK3Adet = subYenK3Adet, fnlK3Tutar = subYenK3Tutar,
                        fnlK5Adet = subYenK5Adet, fnlK5Tutar = subYenK5Tutar
                    },
                    yeniSatis = new
                    {
                        fnlK2Adet = subYsK2Adet, fnlK2Tutar = subYsK2Tutar,
                        fnlK3Adet = subYsK3Adet, fnlK3Tutar = subYsK3Tutar,
                        fnlK5Adet = subYsK5Adet, fnlK5Tutar = subYsK5Tutar
                    }
                },
                // Detaylar (mevcut sorgulardan)
                stageDagilim,
                dealTypeDagilim,
                aylikTrend
            };
            _cache.Set(cacheKey, result, CacheTTL);
            return Json(result);
        }

        // ───────────────────────────────────────────────────────────────
        // DEBUG: K2 vs K3 fark analizi — geçici endpoint
        // ───────────────────────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> DebugK2K3(string? filter, string? startDate, string? endDate)
        {
            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);
            using var db = _contextFactory.CreateDbContext();
            var tahakkukMap = await _tahakkukService.GetTahakkukMapAsync();

            // Kart 3 teklifleri: CreatedOn dönemde
            var lostStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Reject", "Denied", "Closed" };
            var k3Teklifler = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null
                    && t.CreatedOn.HasValue && t.CreatedOn.Value >= start && t.CreatedOn.Value <= end)
                .Select(t => new { t.Id, t.TotalNetAmountLocalCurrency_Amount, t.Status, t.OpportunityId, t.CreatedOn })
                .ToListAsync();
            var aktifK3 = k3Teklifler.Where(t => !lostStatuses.Contains(t.Status ?? "")).ToList();

            // Fırsatsız
            var firsatsiz = aktifK3.Where(t => !t.OpportunityId.HasValue).ToList();
            // Fırsatlı
            var firsatli = aktifK3.Where(t => t.OpportunityId.HasValue).ToList();

            // Fırsat bilgilerini çek
            var oppIds = firsatli.Select(t => t.OpportunityId!.Value.ToString()).Distinct().ToList();
            var oppIdSet = oppIds.ToHashSet();
            var firsatlar = await ExcludeTestFirsat(db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking())
                .Where(o => oppIdSet.Contains(o.Id))
                .Select(o => new { o.Id, o.CloseDate, o.OpportunityStageName })
                .ToListAsync();
            var firsatMap = firsatlar.ToDictionary(o => o.Id ?? "", o => o);

            // Kapali siparis zinciri: Won firsatlar icin efektif tarih
            var kapaliZincir = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue)
                .Join(ExcludeTestSiparis(db.TBL_VARUNA_SIPARIs.AsNoTracking())
                    .Where(s => s.OrderStatus == "Closed" && s.QuoteId != null),
                    t => t.Id.ToString(), s => s.QuoteId,
                    (t, s) => new { OppId = t.OpportunityId!.Value.ToString().ToLower(), s.SerialNumber, s.SAPOutReferenceCode, s.InvoiceDate })
                .ToListAsync();
            var oppEfektifMap = kapaliZincir.GroupBy(x => x.OppId)
                .ToDictionary(g => g.Key, g => EfektifInvoice(g.First().SerialNumber, g.First().SAPOutReferenceCode, g.First().InvoiceDate, tahakkukMap));

            // Kategorize et
            var kategori = firsatli.Select(t => {
                var oppId = t.OpportunityId!.Value.ToString();
                var opp = firsatMap.GetValueOrDefault(oppId);
                var stage = opp?.OpportunityStageName ?? "?";
                var closeDate = opp?.CloseDate;
                var closeDateInRange = closeDate.HasValue && closeDate.Value >= start && closeDate.Value <= end;
                var efektif = oppEfektifMap.GetValueOrDefault(oppId.ToLower());
                var efektifInRange = efektif.HasValue && efektif.Value >= start && efektif.Value <= end;
                var isWon = stage == "Won";
                var isLost = stage == "Lost" || (stage != null && stage.Contains("Closed"));
                return new {
                    TeklifId = t.Id,
                    Tutar = t.TotalNetAmountLocalCurrency_Amount ?? 0m,
                    OppId = oppId,
                    Stage = stage,
                    CloseDate = closeDate,
                    CloseDateInRange = closeDateInRange,
                    EfektifTarih = efektif,
                    EfektifInRange = efektifInRange,
                    IsWon = isWon,
                    IsLost = isLost
                };
            }).ToList();

            var result = new {
                donem = $"{start:dd.MM.yyyy} - {end:dd.MM.yyyy}",
                k3_toplam_adet = aktifK3.Count,
                k3_toplam_tutar = aktifK3.Sum(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m),
                firsatsiz_adet = firsatsiz.Count,
                firsatsiz_tutar = firsatsiz.Sum(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m),
                firsatli_adet = firsatli.Count,
                firsatli_tutar = firsatli.Sum(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m),
                // CloseDate dönemde
                closeDate_icinde_adet = kategori.Count(k => k.CloseDateInRange),
                closeDate_icinde_tutar = kategori.Where(k => k.CloseDateInRange).Sum(k => k.Tutar),
                closeDate_disinda_adet = kategori.Count(k => !k.CloseDateInRange),
                closeDate_disinda_tutar = kategori.Where(k => !k.CloseDateInRange).Sum(k => k.Tutar),
                // Won firsatlar
                won_adet = kategori.Count(k => k.IsWon),
                won_tutar = kategori.Where(k => k.IsWon).Sum(k => k.Tutar),
                won_closeDate_icinde = kategori.Count(k => k.IsWon && k.CloseDateInRange),
                won_closeDate_disinda = kategori.Count(k => k.IsWon && !k.CloseDateInRange),
                won_efektif_icinde = kategori.Count(k => k.IsWon && k.EfektifInRange),
                won_efektif_icinde_tutar = kategori.Where(k => k.IsWon && k.EfektifInRange).Sum(k => k.Tutar),
                won_efektif_disinda = kategori.Count(k => k.IsWon && !k.EfektifInRange),
                won_efektif_disinda_tutar = kategori.Where(k => k.IsWon && !k.EfektifInRange).Sum(k => k.Tutar),
                // Lost firsatlar
                lost_adet = kategori.Count(k => k.IsLost),
                lost_tutar = kategori.Where(k => k.IsLost).Sum(k => k.Tutar),
                // Aktif (ne Won ne Lost) + CloseDate disinda
                aktif_closeDisinda_adet = kategori.Count(k => !k.IsWon && !k.IsLost && !k.CloseDateInRange),
                aktif_closeDisinda_tutar = kategori.Where(k => !k.IsWon && !k.IsLost && !k.CloseDateInRange).Sum(k => k.Tutar),
                // Detay: CloseDate dönem dışında olanların stage dağılımı
                stage_dagilim = kategori.Where(k => !k.CloseDateInRange)
                    .GroupBy(k => k.Stage)
                    .Select(g => new { stage = g.Key, adet = g.Count(), tutar = g.Sum(x => x.Tutar) })
                    .OrderByDescending(x => x.tutar).ToList()
            };
            return Json(result);
        }

        // ───────────────────────────────────────────────────────────────
        // GET /FirsatAnaliz/GetPipelineOzet — SP tabanlı pipeline (K1..K4 + fatura)
        // 4 SP'yi paralel çalıştırır, tek özet döner.
        // K1 (havuz): SP_PIPELINE_FIRSAT @StartDate=NULL, @EndDate=NULL
        // K2 (dönem fırsat): SP_PIPELINE_FIRSAT + tarih
        // K3 (dönem teklif): SP_PIPELINE_TEKLIF
        // K4 (dönem sipariş): SP_PIPELINE_SIPARIS
        // K5 (fatura): SP_COCKPIT_FATURA via ICockpitDataService
        // ───────────────────────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> GetPipelineOzet(string? filter, string? startDate, string? endDate, string? owner)
        {
            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);
            var cacheKey = $"PipelineOzet_{start:yyyyMMdd}_{end:yyyyMMdd}_{owner ?? "all"}";
            var pipeForce = IsForceRefresh();
            if (pipeForce) _cockpitData.InvalidateRange(start, end, owner);
            if (!pipeForce && _cache.TryGetValue(cacheKey, out object? cached) && cached != null)
                return Json(cached);

            using var db1 = _contextFactory.CreateDbContext();
            using var db2 = _contextFactory.CreateDbContext();
            using var db3 = _contextFactory.CreateDbContext();
            using var db4 = _contextFactory.CreateDbContext();

            // Paralel 4 SP + fatura özeti
            // NOT: SP raw SQL üzerinden çekildiği için EF "non-composable" hatası vermesin diye
            // FirstOrDefaultAsync yerine ToListAsync + FirstOrDefault kullanılıyor.
            object ownerParam = (object?)owner ?? DBNull.Value;
            var k1Task = db1.Database.SqlQueryRaw<PipelineFirsatRow>(
                "EXEC SP_PIPELINE_FIRSAT @p0, @p1, @p2",
                DBNull.Value, DBNull.Value, ownerParam).ToListAsync();
            var k2Task = db2.Database.SqlQueryRaw<PipelineFirsatRow>(
                "EXEC SP_PIPELINE_FIRSAT @p0, @p1, @p2",
                start, end, ownerParam).ToListAsync();
            var k3Task = db3.Database.SqlQueryRaw<PipelineTeklifRow>(
                "EXEC SP_PIPELINE_TEKLIF @p0, @p1, @p2",
                start, end, ownerParam).ToListAsync();
            var k4Task = db4.Database.SqlQueryRaw<PipelineSiparisRow>(
                "EXEC SP_PIPELINE_SIPARIS @p0, @p1, @p2",
                start, end, ownerParam).ToListAsync();
            var k5Task = _cockpitData.GetFaturaOzetAsync(start, end, owner);

            await Task.WhenAll(k1Task, k2Task, k3Task, k4Task, k5Task);

            var k1 = k1Task.Result.FirstOrDefault() ?? new PipelineFirsatRow();
            var k2 = k2Task.Result.FirstOrDefault() ?? new PipelineFirsatRow();
            var k3 = k3Task.Result.FirstOrDefault() ?? new PipelineTeklifRow();
            var k4 = k4Task.Result.FirstOrDefault() ?? new PipelineSiparisRow();
            var k5 = k5Task.Result;

            var result = new
            {
                donem = new { start, end },
                k1 = new { tutar = k1.TutarAcik, adet = k1.AdetAcik,
                           wonAdet = k1.AdetWon, wonTutar = k1.TutarWon,
                           lostAdet = k1.AdetLost, lostTutar = k1.TutarLost },
                k2 = new { tutar = k2.TutarAcik, adet = k2.AdetAcik,
                           wonAdet = k2.AdetWon, wonTutar = k2.TutarWon,
                           lostAdet = k2.AdetLost, lostTutar = k2.TutarLost },
                k3 = new { tutar = k3.TutarAktif, adet = k3.AdetAktif,
                           redAdet = k3.AdetRed, redTutar = k3.TutarRed },
                k4 = new { tutar = k4.TutarAcik, adet = k4.AdetAcik,
                           kapaliAdet = k4.AdetKapali, kapaliTutar = k4.TutarKapali },
                k5 = new { tutar = k5?.Toplam ?? 0m, adet = k5?.Adet ?? 0 }
            };

            _cache.Set(cacheKey, result, CacheTTL);
            return Json(result);
        }

        // ───────────────────────────────────────────────────────────────
        // GET /FirsatAnaliz/GetFaturaKarti — SP fatura (ağır sorgu, ayrı endpoint)
        // ───────────────────────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> GetFaturaKarti(string? filter, string? startDate, string? endDate, string? owner)
        {
            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);
            var cacheKey = $"FirsatFaturaKarti_{start:yyyyMMdd}_{end:yyyyMMdd}_{owner ?? "all"}";
            var fatForce = IsForceRefresh();
            if (fatForce) _cockpitData.InvalidateRange(start, end, owner);
            if (!fatForce && _cache.TryGetValue(cacheKey, out object? cached) && cached != null)
                return Json(cached);

            var prevDuration = end - start;
            var prevStart = start.AddDays(-prevDuration.TotalDays);
            var prevEnd = start.AddSeconds(-1);

            var spFaturaTask = _cockpitData.GetFaturaOzetAsync(start, end, owner);
            var spPrevFaturaTask = _cockpitData.GetFaturaOzetAsync(prevStart, prevEnd, owner);
            await Task.WhenAll(spFaturaTask, spPrevFaturaTask);

            var result = new
            {
                kapaliSiparisAdet = spFaturaTask.Result.Adet,
                kapaliSiparisTutar = spFaturaTask.Result.Toplam,
                gecenDonemFatura = spPrevFaturaTask.Result.Toplam
            };
            _cache.Set(cacheKey, result, CacheTTL);
            return Json(result);
        }

        // ───────────────────────────────────────────────────────────────
        // GET /FirsatAnaliz/GetSalesCycleData — Satış döngüsü süre analizi
        // Fırsat → Teklif → Sipariş → Fatura (4 aşama, 3 geçiş süresi)
        // Filtre: InvoiceDate dönemde olan (faturası kesilen) deal'ler
        // ───────────────────────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> GetSalesCycleData(string? filter, string? startDate, string? endDate, string? person)
        {
            // Satış hızı kartı — filter'dan bağımsız, HER ZAMAN son 3 ay'da faturalanmış deal'ların ortalaması
            var end = DateTime.Today.AddDays(1).AddSeconds(-1);
            var start = DateTime.Today.AddMonths(-3);
            var cacheKey = $"FirsatCycleLast3M_{DateTime.Today:yyyyMMdd}_{person ?? "all"}";

            if (!IsForceRefresh() && _cache.TryGetValue(cacheKey, out object? cached) && cached != null)
                return Json(cached);

            using var db = _contextFactory.CreateDbContext();

            // 1) Fırsatlar: CreatedOn dolu (TBL_VARUNA_OPPORTUNITIES — detaylı tablo)
            var firsatlar = await db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking()
                .Where(o => o.DeletedOn == null && o.CreatedOn.HasValue)
                .Select(o => new { o.Id, FirsatCreatedOn = o.CreatedOn!.Value })
                .ToListAsync();
            var firsatMap = firsatlar
                .GroupBy(o => o.Id.ToLower())
                .ToDictionary(g => g.Key, g => g.First().FirsatCreatedOn);

            // 2) Teklifler: OpportunityId + CreatedOn dolu
            var teklifler = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.CreatedOn.HasValue && t.OpportunityId.HasValue)
                .Select(t => new
                {
                    TeklifId = t.Id.ToString().ToLower(),
                    OppId = t.OpportunityId!.Value.ToString().ToLower(),
                    TeklifCreatedOn = t.CreatedOn!.Value,
                    t.CreatedBy
                })
                .ToListAsync();
            // Teklif → QuoteId eşleşmesi için map
            var teklifMap = teklifler
                .GroupBy(t => t.TeklifId)
                .ToDictionary(g => g.Key, g => g.First());

            // 3) Kapalı siparişler: QuoteId + CreateOrderDate + InvoiceDate dolu
            var siparisQuery = ExcludeTestSiparis(db.TBL_VARUNA_SIPARIs.AsNoTracking())
                .Where(s => s.OrderStatus == "Closed"
                    && s.QuoteId != null
                    && s.CreateOrderDate.HasValue
                    && s.InvoiceDate.HasValue
                    && s.TotalNetAmount > 0);

            // InvoiceDate dönem filtresi — "bu dönemde faturası kesilen deal'ler"
            siparisQuery = siparisQuery.Where(s => s.InvoiceDate!.Value >= start && s.InvoiceDate!.Value <= end);

            var siparisler = await siparisQuery
                .Select(s => new
                {
                    QuoteId = s.QuoteId!.ToLower(),
                    CreateOrderDate = s.CreateOrderDate!.Value,
                    InvoiceDate = s.InvoiceDate!.Value
                })
                .ToListAsync();

            // 4) 4-aşamalı join: Sipariş → Teklif → Fırsat
            var joined = siparisler
                .Where(s => teklifMap.ContainsKey(s.QuoteId))
                .Select(s =>
                {
                    var teklif = teklifMap[s.QuoteId];
                    var hasFirsat = firsatMap.TryGetValue(teklif.OppId, out var firsatCreatedOn);

                    var firsatTeklifGun = hasFirsat ? (teklif.TeklifCreatedOn - firsatCreatedOn).TotalDays : -1;
                    var teklifSiparisGun = (s.CreateOrderDate - teklif.TeklifCreatedOn).TotalDays;
                    var siparisFaturaGun = (s.InvoiceDate - s.CreateOrderDate).TotalDays;
                    var toplamGun = hasFirsat
                        ? (s.InvoiceDate - firsatCreatedOn).TotalDays
                        : (s.InvoiceDate - teklif.TeklifCreatedOn).TotalDays;

                    return new
                    {
                        FirsatTeklifGun = firsatTeklifGun,
                        TeklifSiparisGun = teklifSiparisGun,
                        SiparisFaturaGun = siparisFaturaGun,
                        ToplamGun = toplamGun,
                        HasFirsat = hasFirsat,
                        FaturaAy = s.InvoiceDate.ToString("yyyy-MM"),
                        CreatedBy = teklif.CreatedBy
                    };
                })
                .Where(x => x.ToplamGun >= 0 && x.TeklifSiparisGun >= 0 && x.SiparisFaturaGun >= 0)
                .ToList();

            // Person filtresi (teklif sahibi bazlı)
            if (!string.IsNullOrEmpty(person))
                joined = joined.Where(x => x.CreatedBy == person).ToList();

            var emptyMonths = Enumerable.Range(1, 12)
                .Select(m => new { ay = $"{DateTime.Now.Year}-{m:D2}", ortGun = 0.0, adet = 0 }).ToList();

            if (joined.Count == 0)
            {
                var emptyResult = new
                {
                    ortFirsatKapanis = 0.0,
                    medyanFirsatKapanis = 0.0,
                    firsatKapanisAdet = 0,
                    ortFirsatTeklif = 0.0,
                    ortTeklifSiparis = 0.0,
                    ortSiparisFatura = 0.0,
                    ortToplamDongu = 0.0,
                    medyanToplamDongu = 0.0,
                    minDongu = 0,
                    maxDongu = 0,
                    toplamKapanan = 0,
                    firsatEslesen = 0,
                    aylikOrtalama = emptyMonths
                };
                _cache.Set(cacheKey, emptyResult, CacheTTL);
                return Json(emptyResult);
            }

            // 5a) Fırsat kapanış süresi: Fırsat.CreatedOn → Sipariş.InvoiceDate (uçtan uca toplam döngü)
            // Sadece fırsatı eşleşen joined kayıtları kullan (firsatCreatedOn bilindiğinden zincir tam)
            var toplamDonguSirali = joined.Where(x => x.HasFirsat && x.ToplamGun >= 0)
                .Select(x => x.ToplamGun).OrderBy(d => d).ToList();
            var ortFirsatKapanis = toplamDonguSirali.Count > 0 ? Math.Round(toplamDonguSirali.Average(), 1) : 0.0;
            var medyanFirsatKapanis = toplamDonguSirali.Count > 0
                ? (toplamDonguSirali.Count % 2 == 0
                    ? Math.Round((toplamDonguSirali[toplamDonguSirali.Count / 2 - 1] + toplamDonguSirali[toplamDonguSirali.Count / 2]) / 2.0, 1)
                    : Math.Round(toplamDonguSirali[toplamDonguSirali.Count / 2], 1))
                : 0.0;
            var firsatKapanisAdet = toplamDonguSirali.Count;

            // 5b) Detay aşama metrikleri
            var firsatlilar = joined.Where(x => x.HasFirsat && x.FirsatTeklifGun >= 0).ToList();
            var ortFirsatTeklif = firsatlilar.Count > 0
                ? Math.Round(firsatlilar.Average(x => x.FirsatTeklifGun), 1) : 0.0;
            var ortTeklifSiparis = Math.Round(joined.Average(x => x.TeklifSiparisGun), 1);
            var ortSiparisFatura = Math.Round(joined.Average(x => x.SiparisFaturaGun), 1);
            var ortToplamDongu = Math.Round(joined.Average(x => x.ToplamGun), 1);

            var sorted = joined.Select(x => x.ToplamGun).OrderBy(x => x).ToList();
            var medyan = sorted.Count % 2 == 0
                ? Math.Round((sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0, 1)
                : Math.Round(sorted[sorted.Count / 2], 1);

            var minDongu = (int)Math.Round(sorted.First());
            var maxDongu = (int)Math.Round(sorted.Last());

            // 6) Aylık trend
            var yil = DateTime.Now.Year;
            var aylikGrup = joined
                .GroupBy(x => x.FaturaAy)
                .ToDictionary(g => g.Key, g => new { ortGun = Math.Round(g.Average(x => x.ToplamGun), 1), adet = g.Count() });

            var aylikOrtalama = Enumerable.Range(1, 12).Select(m =>
            {
                var ayKey = $"{yil}-{m:D2}";
                return aylikGrup.TryGetValue(ayKey, out var d)
                    ? new { ay = ayKey, d.ortGun, d.adet }
                    : new { ay = ayKey, ortGun = 0.0, adet = 0 };
            }).ToList();

            var result = new
            {
                ortFirsatKapanis,
                medyanFirsatKapanis,
                firsatKapanisAdet,
                ortFirsatTeklif,
                ortTeklifSiparis,
                ortSiparisFatura,
                ortToplamDongu,
                medyanToplamDongu = medyan,
                minDongu,
                maxDongu,
                toplamKapanan = joined.Count,
                firsatEslesen = firsatlilar.Count,
                aylikOrtalama
            };

            _cache.Set(cacheKey, result, CacheTTL);
            return Json(result);
        }

        // ───────────────────────────────────────────────────────────────
        // GET /FirsatAnaliz/GetAgingData — Açık fırsat/teklif yaşlandırma
        // Bucket'lar: 30-60, 60-90, 90+ gün (CreatedOn → bugün)
        // Filtre'den bağımsız (her zaman güncel açık havuzu gösterir)
        // ───────────────────────────────────────────────────────────────
        // ───────────────────────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> GetAgingData(string? person)
        {
            var cacheKey = $"FirsatAging_{DateTime.Today:yyyyMMdd}_{person ?? "all"}";
            if (!IsForceRefresh() && _cache.TryGetValue(cacheKey, out object? cachedAging) && cachedAging != null)
                return Json(cachedAging);

            using var db = _contextFactory.CreateDbContext();
            var now = DateTime.Today;

            // ──────────── AÇIK FIRSATLAR ────────────
            // OpportunityStageName ∉ {Lost, Won} ve "Closed" içermiyor
            // CreatedOn epoch sanity: ≥ MinValidCreatedOn (2020-01-01)
            var firsatlar = await db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking()
                .Where(o => o.DeletedOn == null && o.CreatedOn.HasValue && o.CreatedOn.Value >= MinValidCreatedOn)
                .Where(o => o.OpportunityStageName != "Lost" && o.OpportunityStageName != "Won")
                .Select(o => new
                {
                    o.Id,
                    o.CreatedOn,
                    o.OpportunityStageName,
                    o.AmountAmount,
                    o.OwnerId
                })
                .ToListAsync();

            var acikFirsatlar = firsatlar
                .Where(o => o.OpportunityStageName == null || !o.OpportunityStageName.Contains("Closed", StringComparison.OrdinalIgnoreCase))
                .ToList();

            int firsat30 = 0, firsat60 = 0, firsat90 = 0;
            decimal firsat30Tutar = 0m, firsat60Tutar = 0m, firsat90Tutar = 0m;
            foreach (var f in acikFirsatlar)
            {
                var age = (now - f.CreatedOn!.Value.Date).TotalDays;
                var tutar = f.AmountAmount ?? 0m;
                if (age >= 90) { firsat90++; firsat90Tutar += tutar; }
                else if (age >= 60) { firsat60++; firsat60Tutar += tutar; }
                else if (age >= 30) { firsat30++; firsat30Tutar += tutar; }
            }

            // ──────────── AÇIK TEKLIFLER ────────────
            // Aktif: Status ∈ ActiveTeklifStatuses (Presented/InReview) — Draft hariç
            // CreatedOn epoch sanity: ≥ MinValidCreatedOn
            var activeSet = ActiveTeklifStatuses;
            var teklifler = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.CreatedOn.HasValue && t.CreatedOn.Value >= MinValidCreatedOn)
                .Where(t => t.Status != null && activeSet.Contains(t.Status))
                .Select(t => new
                {
                    t.CreatedOn,
                    t.TotalNetAmountLocalCurrency_Amount,
                    t.CreatedBy
                })
                .ToListAsync();

            if (!string.IsNullOrEmpty(person))
                teklifler = teklifler.Where(t => t.CreatedBy == person).ToList();

            int teklif30 = 0, teklif60 = 0, teklif90 = 0;
            decimal teklif30Tutar = 0m, teklif60Tutar = 0m, teklif90Tutar = 0m;
            foreach (var t in teklifler)
            {
                var age = (now - t.CreatedOn!.Value.Date).TotalDays;
                var tutar = t.TotalNetAmountLocalCurrency_Amount ?? 0m;
                if (age >= 90) { teklif90++; teklif90Tutar += tutar; }
                else if (age >= 60) { teklif60++; teklif60Tutar += tutar; }
                else if (age >= 30) { teklif30++; teklif30Tutar += tutar; }
            }

            var result = new
            {
                firsat = new
                {
                    g30 = new { adet = firsat30, tutar = firsat30Tutar },
                    g60 = new { adet = firsat60, tutar = firsat60Tutar },
                    g90 = new { adet = firsat90, tutar = firsat90Tutar }
                },
                teklif = new
                {
                    g30 = new { adet = teklif30, tutar = teklif30Tutar },
                    g60 = new { adet = teklif60, tutar = teklif60Tutar },
                    g90 = new { adet = teklif90, tutar = teklif90Tutar }
                }
            };
            _cache.Set(cacheKey, result, CacheTTL);
            return Json(result);
        }

        // ───────────────────────────────────────────────────────────────
        // GET /FirsatAnaliz/GetYaslananFirsatlarDetay — Rapor detay tablo
        // Açık fırsatlar (Stage ∉ {Lost, Won, Closed*}) — sadece tahmini kapanışı GEÇMİŞ olanlar.
        // vadeAsimi = (bugün - CloseDate) gün. Sıralama küçükten büyüğe (en eski geçen üstte).
        // ───────────────────────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> GetYaslananFirsatlarDetay(string? yas)
        {
            // QW2: Detay rapor cache (CacheTTL — 30dk). 'yas' parametresi geriye dönük uyumluluk için kabul edilir ama yok sayılır.
            var cacheKey = $"FirsatRaporKapanisGecen_{DateTime.Today:yyyyMMdd}";
            if (_cache.TryGetValue(cacheKey, out object? cachedFs) && cachedFs != null)
                return Json(cachedFs);

            using var db = _contextFactory.CreateDbContext();
            var now = DateTime.Today;

            var firsatlar = await db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking()
                .Where(o => o.DeletedOn == null && o.CreatedOn.HasValue && o.CreatedOn.Value >= MinValidCreatedOn)
                .Where(o => o.OpportunityStageName != "Lost" && o.OpportunityStageName != "Won")
                .Where(o => o.CloseDate.HasValue && o.CloseDate.Value < now)
                .Select(o => new
                {
                    o.Id, o.Name, o.CreatedOn, o.CloseDate,
                    o.OpportunityStageName, o.OpportunityStageNameTr, o.AmountAmount,
                    o.OwnerId, o.AccountId, o.CustomerRepresentativeId, o.Probability
                })
                .ToListAsync();

            var acik = firsatlar
                .Where(o => o.OpportunityStageName == null || !o.OpportunityStageName.Contains("Closed", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var ownerMap = await GetOwnerMapAsync();
            var personMap = await GetPersonMapAsync(db);
            var accountTitleMap = await GetAccountTitleMapAsync(db);
            var accRep = await GetAccountToRepMapAsync(db);

            var rows = acik.Select(o =>
            {
                var vadeAsimi = (int)(now - o.CloseDate!.Value.Date).TotalDays;
                var ownerName = ResolveOwnerName(o.OwnerId, ownerMap);
                if (!string.IsNullOrEmpty(o.OwnerId) && personMap.TryGetValue(o.OwnerId.ToLower(), out var pn))
                    ownerName = pn;
                var salesRep = ResolveSalesRepName(o.AccountId, o.CustomerRepresentativeId, o.OwnerId,
                    accRep, personMap, ownerMap);
                var musteri = (o.AccountId != null && accountTitleMap.TryGetValue(o.AccountId.ToLower(), out var an)) ? an : "—";
                return new
                {
                    kapanisTarihi = o.CloseDate,
                    firsatSahibi = ownerName,
                    satisTemsilcisi = salesRep,
                    musteri,
                    firsatAdi = o.Name ?? "—",
                    asama = o.OpportunityStageNameTr ?? o.OpportunityStageName ?? "—",
                    tutar = o.AmountAmount ?? 0m,
                    vadeAsimi,
                    olasilik = o.Probability ?? 0m
                };
            })
            .OrderBy(r => r.kapanisTarihi)  // küçükten büyüğe — en eski kapanışı geçen üstte
            .ToList();

            var ozet = new
            {
                adet = rows.Count,
                tutar = rows.Sum(r => r.tutar)
            };

            var fResult = new { ozet, kayitlar = rows };
            _cache.Set(cacheKey, fResult, CacheTTL);
            return Json(fResult);
        }

        // ───────────────────────────────────────────────────────────────
        // GET /FirsatAnaliz/GetYaslananTekliflerDetay — Rapor detay tablo
        // Bekleyen teklifler (Status ∈ ActiveTeklifStatuses) — sadece bağlı fırsatın
        // CloseDate'i bugünden ÖNCE olanlar. vadeAsimi = (bugün - CloseDate). Sıralama ASC.
        // ───────────────────────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> GetYaslananTekliflerDetay(string? yas)
        {
            var activeSet = ActiveTeklifStatuses;
            // 'yas' parametresi geriye dönük uyumluluk için kabul edilir ama yok sayılır.
            var cacheKey = $"TeklifRaporKapanisGecen_{DateTime.Today:yyyyMMdd}";
            if (_cache.TryGetValue(cacheKey, out object? cachedTk) && cachedTk != null)
                return Json(cachedTk);

            using var db = _contextFactory.CreateDbContext();
            var now = DateTime.Today;

            // Aktif teklif: Status ∈ {Presented, InReview} — Draft hariç. CreatedOn epoch sanity'li.
            var teklifler = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.CreatedOn.HasValue && t.CreatedOn.Value >= MinValidCreatedOn)
                .Where(t => t.Status != null && activeSet.Contains(t.Status))
                .Select(t => new
                {
                    t.Id, t.Name, t.Number, t.CreatedOn, t.Status,
                    t.TotalNetAmountLocalCurrency_Amount,
                    t.OpportunityId, t.AccountId, t.CreatedBy
                })
                .ToListAsync();

            // Bağlı fırsatların CloseDate + Owner bilgisini topla
            var oppIds = teklifler.Where(t => t.OpportunityId.HasValue)
                .Select(t => t.OpportunityId!.Value.ToString().ToLower())
                .Distinct().ToList();
            var oppDict = await db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking()
                .Where(o => oppIds.Contains(o.Id))
                .Select(o => new { o.Id, o.CloseDate, o.OwnerId, o.AccountId, o.CustomerRepresentativeId })
                .ToListAsync();
            var oppLookup = oppDict.GroupBy(o => o.Id.ToLower())
                .ToDictionary(g => g.Key, g => g.First());

            // Sadece bağlı fırsatın CloseDate'i bugünden önce olan teklifler
            var bekleyen = teklifler
                .Where(t =>
                {
                    if (!t.OpportunityId.HasValue) return false;
                    if (!oppLookup.TryGetValue(t.OpportunityId.Value.ToString().ToLower(), out var op)) return false;
                    return op.CloseDate.HasValue && op.CloseDate.Value < now;
                })
                .ToList();

            var ownerMap = await GetOwnerMapAsync();
            var personMap = await GetPersonMapAsync(db);
            var accountTitleMap = await GetAccountTitleMapAsync(db);
            var accRep = await GetAccountToRepMapAsync(db);

            var rows = bekleyen.Select(t =>
            {
                DateTime? kapanis = null;
                string? oppOwnerId = null, oppCustRepId = null, oppAccountId = null;
                if (t.OpportunityId.HasValue && oppLookup.TryGetValue(t.OpportunityId.Value.ToString().ToLower(), out var op))
                {
                    kapanis = op.CloseDate;
                    oppOwnerId = op.OwnerId;
                    oppCustRepId = op.CustomerRepresentativeId;
                    oppAccountId = op.AccountId;
                }
                var vadeAsimi = kapanis.HasValue ? (int)(now - kapanis.Value.Date).TotalDays : 0;
                var ownerName = !string.IsNullOrEmpty(oppOwnerId) && personMap.TryGetValue(oppOwnerId.ToLower(), out var pn) ? pn : ResolveOwnerName(oppOwnerId, ownerMap);
                var accIdForRep = oppAccountId ?? (t.AccountId.HasValue ? t.AccountId.Value.ToString() : null);
                var salesRep = ResolveSalesRepName(accIdForRep, oppCustRepId, oppOwnerId,
                    accRep, personMap, ownerMap);
                var accId = oppAccountId ?? (t.AccountId.HasValue ? t.AccountId.Value.ToString() : null);
                var musteri = (accId != null && accountTitleMap.TryGetValue(accId.ToLower(), out var an)) ? an : "—";
                return new
                {
                    kapanisTarihi = kapanis,
                    firsatSahibi = ownerName ?? "—",
                    satisTemsilcisi = salesRep,
                    musteri,
                    teklifNo = t.Number ?? "—",
                    teklifAdi = t.Name ?? "—",
                    durum = t.Status ?? "—",
                    tutar = t.TotalNetAmountLocalCurrency_Amount ?? 0m,
                    vadeAsimi
                };
            })
            .OrderBy(r => r.kapanisTarihi)
            .ToList();

            var ozet = new
            {
                adet = rows.Count,
                tutar = rows.Sum(r => r.tutar)
            };

            var tResult = new { ozet, kayitlar = rows };
            _cache.Set(cacheKey, tResult, CacheTTL);
            return Json(tResult);
        }

        // ───────────────────────────────────────────────────────────────
        // GET /FirsatAnaliz/GetAcikFirsatlarDetay — Birleşik Rapor tablosu
        // Açık fırsatlar (Stage ∉ {Lost, Won, Closed*}). Filtreler:
        //   kapanisGecti=true  → CloseDate < bugün
        //   minYas=30/60/90    → fırsat yaşı (bugün - CreatedOn) ≥ minYas
        //   ownerName / firsatSahibi  → kişi filtreleri (tutarlı isimle)
        // Her satır 'firsatYasi' (gün), 'kapanisGecti' (bool), 'teklifVar' (bool), 'teklifAdet' içerir.
        // ───────────────────────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> GetAcikFirsatlarDetay(
            string? kapanisGecti = null,
            int? minYas = null,
            string? ownerName = null,
            string? firsatSahibi = null,
            string? kapanisOnceTarihi = null)
        {
            var sadeceKapanisGecti = string.Equals(kapanisGecti, "true", StringComparison.OrdinalIgnoreCase) || kapanisGecti == "1";
            var min = minYas.GetValueOrDefault(0);
            DateTime? kapanisOnce = null;
            if (!string.IsNullOrWhiteSpace(kapanisOnceTarihi)
                && DateTime.TryParse(kapanisOnceTarihi, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var ko))
                kapanisOnce = ko.Date;
            var cacheKey = $"AcikFirsatlarDetay_{DateTime.Today:yyyyMMdd}_{(sadeceKapanisGecti ? "kg" : "all")}_{min}_{ownerName ?? "_"}_{firsatSahibi ?? "_"}_{(kapanisOnce.HasValue ? kapanisOnce.Value.ToString("yyyyMMdd") : "_")}";
            if (_cache.TryGetValue(cacheKey, out object? cached) && cached != null)
                return Json(cached);

            using var db = _contextFactory.CreateDbContext();
            var now = DateTime.Today;
            // "Tahmini kapanış geçti" eşiği = içinde bulunduğumuz ayın ilk günü.
            // Bu ay içindeki tarihler henüz "geçmiş" sayılmaz (popup mantığıyla aynı).
            var ayBasi = new DateTime(now.Year, now.Month, 1);

            // PERF: ownerName/firsatSahibi parametre varsa, ilgili Person → PersonId(ler) çıkarıp
            // OwnerId IN (...) DB-level prefilter uygulanır. Tam tutarlılık (4-step ResolveSalesRepName)
            // resolved sonrası in-memory filter ile yine sağlanır; DB-level sadece kabaca daraltma.
            // Back-off: prefilter 0 personId döndürürse (isim DB'de bulunamadı / fuzzy mismatch) atlanır,
            // eski full-scan davranışı korunur — kayıt kaçırma yok, sadece perf kazancı eksilir.
            HashSet<string>? prefilterOwnerIds = null;
            var kisiAdlari = new[] { ownerName, firsatSahibi }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim())
                .Distinct()
                .ToList();
            if (kisiAdlari.Count > 0)
            {
                // PersonNameSurname tam eşleşme + (Name + ' ' + SurName) Trim eşleşme
                // (TR karakter / ekstra ad farkları için)
                var personRows = await db.TBL_VARUNA_PERSONs.AsNoTracking()
                    .Where(p => p.DeletedOn == null && p.PersonNameSurname != null
                        && kisiAdlari.Contains(p.PersonNameSurname))
                    .Select(p => p.Id)
                    .ToListAsync();
                if (personRows.Count > 0)
                {
                    // SQL Server collation case-insensitive (Turkish_CI_AS) — orijinal ID'lerle
                    // doğrudan IN sorgusu index kullanır. ToLower() çevirisi yapma — sargable kalsın.
                    prefilterOwnerIds = new HashSet<string>(
                        personRows.Where(id => !string.IsNullOrEmpty(id)),
                        StringComparer.OrdinalIgnoreCase);
                }
            }

            var firsatlarQ = db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking()
                .Where(o => o.DeletedOn == null && o.CreatedOn.HasValue && o.CreatedOn.Value >= MinValidCreatedOn)
                .Where(o => o.OpportunityStageName != "Lost" && o.OpportunityStageName != "Won");

            if (prefilterOwnerIds != null && prefilterOwnerIds.Count > 0)
            {
                var ids = prefilterOwnerIds.ToList();
                // OwnerId IN (...) — index kullanır; collation CI olduğundan ToLower'a gerek yok.
                firsatlarQ = firsatlarQ.Where(o => o.OwnerId != null && ids.Contains(o.OwnerId));
            }

            var firsatlar = await firsatlarQ
                .Select(o => new
                {
                    o.Id, o.Name, o.CreatedOn, o.CloseDate,
                    o.OpportunityStageName, o.OpportunityStageNameTr, o.AmountAmount,
                    o.OwnerId, o.AccountId, o.CustomerRepresentativeId, o.Probability
                })
                .ToListAsync();

            var acik = firsatlar
                .Where(o => o.OpportunityStageName == null || !o.OpportunityStageName.Contains("Closed", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (sadeceKapanisGecti)
                acik = acik.Where(o => o.CloseDate.HasValue && o.CloseDate.Value < ayBasi).ToList();

            if (kapanisOnce.HasValue)
                acik = acik.Where(o => o.CloseDate.HasValue && o.CloseDate.Value < kapanisOnce.Value).ToList();

            if (min > 0)
                acik = acik.Where(o => (now - o.CreatedOn!.Value.Date).TotalDays >= min).ToList();

            // Teklif lookup: hangi fırsatların teklifi var (Draft hariç tüm aktif/bitmiş teklifler dahil)
            var oppIds = acik.Select(o => o.Id).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
            var teklifRows = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue)
                .Select(t => new { OppId = t.OpportunityId!.Value.ToString().ToLower(), t.Status })
                .ToListAsync();
            var teklifLookup = teklifRows
                .GroupBy(t => t.OppId)
                .ToDictionary(g => g.Key, g => g.Count());

            var ownerMap = await GetOwnerMapAsync();
            var personMap = await GetPersonMapAsync(db);
            var accountTitleMap = await GetAccountTitleMapAsync(db);
            var accRep = await GetAccountToRepMapAsync(db);

            var rows = acik.Select(o =>
            {
                var firsatYasi = (int)(now - o.CreatedOn!.Value.Date).TotalDays;
                var ownerNameRow = ResolveOwnerName(o.OwnerId, ownerMap);
                if (!string.IsNullOrEmpty(o.OwnerId) && personMap.TryGetValue(o.OwnerId.ToLower(), out var pn))
                    ownerNameRow = pn;
                var salesRep = ResolveSalesRepName(o.AccountId, o.CustomerRepresentativeId, o.OwnerId,
                    accRep, personMap, ownerMap);
                var musteri = (o.AccountId != null && accountTitleMap.TryGetValue(o.AccountId.ToLower(), out var an)) ? an : "—";
                var teklifAdet = teklifLookup.TryGetValue((o.Id ?? "").ToLower(), out var tc) ? tc : 0;
                return new
                {
                    kapanisTarihi = o.CloseDate,
                    firsatSahibi = ownerNameRow,
                    satisTemsilcisi = salesRep,
                    musteri,
                    firsatAdi = o.Name ?? "—",
                    asama = o.OpportunityStageNameTr ?? o.OpportunityStageName ?? "—",
                    tutar = o.AmountAmount ?? 0m,
                    firsatYasi,
                    kapanisGecti = o.CloseDate.HasValue && o.CloseDate.Value < ayBasi,
                    teklifAdet,
                    teklifVar = teklifAdet > 0,
                    olasilik = o.Probability ?? 0m
                };
            }).ToList();

            // Kişi filtreleri — render değeri ile birebir eşleşir
            if (!string.IsNullOrEmpty(ownerName))
                rows = rows.Where(r => r.satisTemsilcisi == ownerName).ToList();
            if (!string.IsNullOrEmpty(firsatSahibi))
                rows = rows.Where(r => r.firsatSahibi == firsatSahibi).ToList();

            // Default sort: kapanış tarihi ASC (en eski → en yeni). Boş kapanış sona.
            var sorted = rows
                .OrderBy(r => r.kapanisTarihi.HasValue ? 0 : 1)
                .ThenBy(r => r.kapanisTarihi)
                .ToList();

            var ozet = new
            {
                adet = sorted.Count,
                tutar = sorted.Sum(r => r.tutar),
                kapanisGectiAdet = sorted.Count(r => r.kapanisGecti),
                kapanisGectiTutar = sorted.Where(r => r.kapanisGecti).Sum(r => r.tutar),
                teklifVarAdet = sorted.Count(r => r.teklifVar)
            };

            var result = new { ozet, kayitlar = sorted };
            _cache.Set(cacheKey, result, CacheTTL);
            return Json(result);
        }

        [HttpGet]
        public async Task<IActionResult> ExportAcikFirsatlar(string? kapanisGecti = null, int? minYas = null, string? ownerName = null, string? firsatSahibi = null, string? kapanisOnceTarihi = null)
        {
            var jr = await GetAcikFirsatlarDetay(kapanisGecti, minYas, ownerName, firsatSahibi, kapanisOnceTarihi) as JsonResult;
            dynamic data = jr!.Value!;
            var sb = new System.Text.StringBuilder();
            sb.Append('﻿'); // UTF-8 BOM
            sb.AppendLine("Tahmini Kapanış;Fırsat Sahibi;Satış Temsilcisi;Müşteri;Fırsat Adı;Aşama;Tutar;Fırsat Yaşı (gün);Kapanışı Geçti;Teklif Sayısı;Olasılık %");
            foreach (var r in (System.Collections.IEnumerable)data.kayitlar)
            {
                dynamic row = r;
                string kt = row.kapanisTarihi == null ? "" : ((DateTime)row.kapanisTarihi).ToString("dd.MM.yyyy");
                sb.Append(CsvEscape(kt)).Append(';');
                sb.Append(CsvEscape((string)row.firsatSahibi)).Append(';');
                sb.Append(CsvEscape((string)row.satisTemsilcisi)).Append(';');
                sb.Append(CsvEscape((string)row.musteri)).Append(';');
                sb.Append(CsvEscape((string)row.firsatAdi)).Append(';');
                sb.Append(CsvEscape((string)row.asama)).Append(';');
                sb.Append(((decimal)row.tutar).ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))).Append(';');
                sb.Append(((int)row.firsatYasi)).Append(';');
                sb.Append(((bool)row.kapanisGecti) ? "Evet" : "Hayır").Append(';');
                sb.Append(((int)row.teklifAdet)).Append(';');
                sb.AppendLine(((decimal)row.olasilik).ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("tr-TR")));
            }
            var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            return File(bytes, "text/csv; charset=utf-8", $"AcikFirsatlar_{DateTime.Today:yyyyMMdd}.csv");
        }

        // ───────────────────────────────────────────────────────────────
        // CSV Export helpers
        // ───────────────────────────────────────────────────────────────
        private static string CsvEscape(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var needsQuote = s.Contains(';') || s.Contains('"') || s.Contains('\n') || s.Contains('\r');
            return needsQuote ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        [HttpGet]
        public async Task<IActionResult> ExportYaslananFirsatlar(string? yas)
        {
            // Aynı veriyi yeniden hesapla (cache'lemeye gerek yok — manuel indir nadir)
            var jr = await GetYaslananFirsatlarDetay(yas) as JsonResult;
            dynamic data = jr!.Value!;
            var sb = new System.Text.StringBuilder();
            sb.Append('﻿'); // UTF-8 BOM (Excel Türkçe karakterler için)
            sb.AppendLine("Tahmini Kapanış Tarihi;Fırsat Sahibi;Satış Temsilcisi;Müşteri;Fırsat Adı;Aşama;Tutar;Vade Aşımı (gün);Olasılık %");
            foreach (var r in (System.Collections.IEnumerable)data.kayitlar)
            {
                dynamic row = r;
                string kt = row.kapanisTarihi == null ? "" : ((DateTime)row.kapanisTarihi).ToString("dd.MM.yyyy");
                sb.Append(CsvEscape(kt)).Append(';');
                sb.Append(CsvEscape((string)row.firsatSahibi)).Append(';');
                sb.Append(CsvEscape((string)row.satisTemsilcisi)).Append(';');
                sb.Append(CsvEscape((string)row.musteri)).Append(';');
                sb.Append(CsvEscape((string)row.firsatAdi)).Append(';');
                sb.Append(CsvEscape((string)row.asama)).Append(';');
                sb.Append(((decimal)row.tutar).ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))).Append(';');
                sb.Append(((int)row.vadeAsimi)).Append(';');
                sb.AppendLine(((decimal)row.olasilik).ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("tr-TR")));
            }
            var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            return File(bytes, "text/csv; charset=utf-8", $"KapanisGecenFirsatlar_{DateTime.Today:yyyyMMdd}.csv");
        }

        [HttpGet]
        public async Task<IActionResult> ExportYaslananTeklifler(string? yas)
        {
            var jr = await GetYaslananTekliflerDetay(yas) as JsonResult;
            dynamic data = jr!.Value!;
            var sb = new System.Text.StringBuilder();
            sb.Append('﻿');
            sb.AppendLine("Tahmini Kapanış Tarihi;Fırsat Sahibi;Satış Temsilcisi;Müşteri;Teklif No;Teklif Adı;Durum;Tutar;Vade Aşımı (gün)");
            foreach (var r in (System.Collections.IEnumerable)data.kayitlar)
            {
                dynamic row = r;
                string kt = row.kapanisTarihi == null ? "" : ((DateTime)row.kapanisTarihi).ToString("dd.MM.yyyy");
                sb.Append(CsvEscape(kt)).Append(';');
                sb.Append(CsvEscape((string)row.firsatSahibi)).Append(';');
                sb.Append(CsvEscape((string)row.satisTemsilcisi)).Append(';');
                sb.Append(CsvEscape((string)row.musteri)).Append(';');
                sb.Append(CsvEscape((string)row.teklifNo)).Append(';');
                sb.Append(CsvEscape((string)row.teklifAdi)).Append(';');
                sb.Append(CsvEscape((string)row.durum)).Append(';');
                sb.Append(((decimal)row.tutar).ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))).Append(';');
                sb.AppendLine(((int)row.vadeAsimi).ToString());
            }
            var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            return File(bytes, "text/csv; charset=utf-8", $"KapanisGecenTeklifler_{DateTime.Today:yyyyMMdd}.csv");
        }

        // ───────────────────────────────────────────────────────────────
        // GET /FirsatAnaliz/GetOwnerPerformance — Satış temsilcisi bazlı
        // Öncelik: Teklif varsa ProposalOwnerId, yoksa fırsat OwnerId
        // ───────────────────────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> GetOwnerPerformance(string? filter, string? startDate, string? endDate)
        {
            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);

            var cacheKey = $"FirsatOwnerPerf_{start:yyyyMMdd}_{end:yyyyMMdd}";
            if (_cache.TryGetValue(cacheKey, out object? cachedOwner) && cachedOwner != null)
                return Json(cachedOwner);

            var (salesReps, _) = await BuildOwnerPerformanceDataAsync(start, end);

            _cache.Set(cacheKey, salesReps, CacheTTL);
            return Json(salesReps);
        }

        // ───────────────────────────────────────────────────────────────
        // GET /FirsatAnaliz/GetFirsatSahipleri — Account Rep olmayan fırsat sahipleri
        // ───────────────────────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> GetFirsatSahipleri(string? filter, string? startDate, string? endDate)
        {
            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);

            var cacheKey = $"FirsatSahipleri_{start:yyyyMMdd}_{end:yyyyMMdd}";
            if (_cache.TryGetValue(cacheKey, out object? cachedFs) && cachedFs != null)
                return Json(cachedFs);

            var (_, firsatSahipleri) = await BuildOwnerPerformanceDataAsync(start, end);

            _cache.Set(cacheKey, firsatSahipleri, CacheTTL);
            return Json(firsatSahipleri);
        }

        /// <summary>
        /// Ortak veri: satış temsilcileri (account rep) ve fırsat sahipleri (account rep olmayan) ayrıştırılır.
        /// </summary>
        private async Task<(object salesReps, object firsatSahipleri)> BuildOwnerPerformanceDataAsync(DateTime start, DateTime end)
        {
            using var db = _contextFactory.CreateDbContext();
            var personMap = await GetAccountRepPersonMapAsync(db);
            var ownerMap = await GetOwnerMapAsync();

            // Dönem fırsatları
            var firsatlar = await ExcludeTestFirsat(db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking())
                .Where(o => o.CloseDate.HasValue
                    && o.CloseDate.Value >= start && o.CloseDate.Value <= end
                    && o.OwnerId != null)
                .Select(o => new { o.Id, o.OwnerId, o.OpportunityStageName, o.AmountAmount })
                .ToListAsync();

            // Fırsat Id → Teklif ProposalOwnerId lookup
            var teklifOwnerMap = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue && t.ProposalOwnerId.HasValue)
                .Select(t => new { OppId = t.OpportunityId!.Value.ToString().ToLower(), t.ProposalOwnerId })
                .ToListAsync();
            var oppToProposalOwner = teklifOwnerMap
                .GroupBy(t => t.OppId)
                .ToDictionary(g => g.Key, g => g.First().ProposalOwnerId!.Value.ToString().ToLower());

            var data = firsatlar.Select(f =>
            {
                var firsatId = f.Id?.ToLower() ?? "";
                var efektifOwner = oppToProposalOwner.TryGetValue(firsatId, out var proposalOwner)
                    ? proposalOwner
                    : f.OwnerId!;
                return new { OwnerId = efektifOwner, f.OpportunityStageName, f.AmountAmount };
            }).ToList();

            var allGrouped = data
                .GroupBy(d => d.OwnerId)
                .Select(g =>
                {
                    var total = g.Count();
                    var won = g.Count(d => d.OpportunityStageName == "Won");
                    var lost = g.Count(d => d.OpportunityStageName == "Lost"
                        || (d.OpportunityStageName != null && d.OpportunityStageName.Contains("Closed")));
                    var active = total - won - lost;
                    var winRate = (won + lost) > 0
                        ? Math.Round((decimal)won / (won + lost) * 100, 1) : 0m;

                    var toplamTutar = g.Sum(d => d.AmountAmount ?? 0m);
                    var wonTutar = g.Where(d => d.OpportunityStageName == "Won").Sum(d => d.AmountAmount ?? 0m);

                    var isAccountRep = personMap.ContainsKey(g.Key);
                    var adSoyad = isAccountRep
                        ? personMap[g.Key]
                        : (ownerMap.TryGetValue(g.Key, out var n) ? n : g.Key[..Math.Min(8, g.Key.Length)] + "…");

                    return new
                    {
                        ownerId = g.Key,
                        adSoyad,
                        toplam = total,
                        aktif = active,
                        won,
                        lost,
                        kazanmaOrani = winRate,
                        toplamTutar,
                        wonTutar,
                        isAccountRep
                    };
                })
                .Where(x => !x.adSoyad.Contains("GMAIL", StringComparison.OrdinalIgnoreCase)
                    && !x.adSoyad.Contains("TEST", StringComparison.OrdinalIgnoreCase)
                    && !x.adSoyad.Contains("DENEME", StringComparison.OrdinalIgnoreCase)
                    && !x.adSoyad.StartsWith("Bilinmiyor", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var salesReps = allGrouped.Where(x => x.isAccountRep)
                .OrderByDescending(x => x.wonTutar).ThenByDescending(x => x.toplam).ToList();
            var firsatSahipleri = allGrouped.Where(x => !x.isAccountRep)
                .OrderByDescending(x => x.toplamTutar).ThenByDescending(x => x.toplam).ToList();

            return (salesReps, firsatSahipleri);
        }

        /// <summary>
        /// RepId (lower) → PersonNameSurname — kanonik kaynak TBL_VARUNA_ACCOUNT_REPRESENTATIVES (State=Active).
        /// Kullanım: bir PersonId'nin satış temsilcisi olup olmadığını kontrol (isAccountRep) ve isim çözümleme.
        /// </summary>
        private async Task<Dictionary<string, string>> GetAccountRepPersonMapAsync(MskDbContext db)
        {
            var mapCacheKey = "account_rep_person_map_v3";
            if (_cache.TryGetValue(mapCacheKey, out Dictionary<string, string>? cached) && cached != null)
                return cached;

            var repIdList = await db.TBL_VARUNA_ACCOUNT_REPRESENTATIVESs.AsNoTracking()
                .Where(r => r.AccountOwnerId.HasValue && r.State == "Active")
                .Select(r => r.AccountOwnerId!.Value.ToString())
                .ToListAsync();
            var repIdStrings = repIdList
                .Select(r => r.ToLower())
                .ToHashSet();

            // TBL_VARUNA_PERSON'dan isim çözümle
            var persons = await db.TBL_VARUNA_PERSONs.AsNoTracking()
                .Where(p => p.PersonNameSurname != null && p.DeletedOn == null)
                .Select(p => new { p.Id, p.PersonNameSurname })
                .ToListAsync();

            var map = persons
                .Where(p => repIdStrings.Contains(p.Id.ToLower()))
                .GroupBy(p => p.Id.ToLower())
                .ToDictionary(g => g.Key, g => g.First().PersonNameSurname!);

            _cache.Set(mapCacheKey, map, CacheTTL);
            return map;
        }

        /// <summary>
        /// TBL_VARUNA_PERSON: Id → PersonNameSurname (tüm aktif kişiler)
        /// </summary>
        private async Task<Dictionary<string, string>> GetPersonMapAsync(MskDbContext db)
        {
            var cacheKey = "person_map_all";
            if (_cache.TryGetValue(cacheKey, out Dictionary<string, string>? cached) && cached != null)
                return cached;

            var map = await db.TBL_VARUNA_PERSONs.AsNoTracking()
                .Where(p => p.PersonNameSurname != null && p.DeletedOn == null)
                .Select(p => new { p.Id, p.PersonNameSurname })
                .ToListAsync();

            var dict = map.GroupBy(p => p.Id.ToLower())
                .ToDictionary(g => g.Key, g => g.First().PersonNameSurname!);

            _cache.Set(cacheKey, dict, CacheTTL);
            return dict;
        }

        /// <summary>
        /// AccountId (lowercase) → Müşteri Adı (Title öncelikli, yoksa Name+SurName) map
        /// Kaynak: TBL_VARUNA_ACCOUNTS tablosu — modelde yok, raw SQL ile okunur (15 dk cache)
        /// </summary>
        private async Task<Dictionary<string, string>> GetAccountTitleMapAsync(MskDbContext db)
        {
            var cacheKey = "account_title_map_v1";
            if (_cache.TryGetValue(cacheKey, out Dictionary<string, string>? cached) && cached != null)
                return cached;

            var rows = await db.Database.SqlQueryRaw<ProductGroupNameDto>(
                @"SELECT CAST(Id AS NVARCHAR(64)) AS Id,
                         COALESCE(
                             NULLIF(LTRIM(RTRIM(Title)), ''),
                             NULLIF(LTRIM(RTRIM(ISNULL(Name,'') + ' ' + ISNULL(SurName,''))), ''),
                             CAST(Id AS NVARCHAR(64))
                         ) AS Name
                  FROM TBL_VARUNA_ACCOUNTS").ToListAsync();

            var dict = rows
                .Where(r => !string.IsNullOrEmpty(r.Id))
                .GroupBy(r => r.Id!.ToLower())
                .ToDictionary(g => g.Key, g => g.First().Name ?? "Tanımsız");

            _cache.Set(cacheKey, dict, TimeSpan.FromMinutes(15));
            return dict;
        }

        /// <summary>
        /// AccountId (lower) → AccountOwnerId (lower) — TBL_VARUNA_ACCOUNT_REPRESENTATIVES (State=Active).
        /// 3-kademeli fallback zincirinin 1. adımı (kanonik müşteri portföy ataması).
        /// </summary>
        private async Task<Dictionary<string, string>> GetAccountToRepMapAsync(MskDbContext db)
        {
            var cacheKey = "account_to_rep_map";
            if (_cache.TryGetValue(cacheKey, out Dictionary<string, string>? cached) && cached != null)
                return cached;

            var reps = await db.TBL_VARUNA_ACCOUNT_REPRESENTATIVESs.AsNoTracking()
                .Where(r => r.AccountOwnerId.HasValue && r.AccountId.HasValue && r.State == "Active")
                .Select(r => new { AccountId = r.AccountId!.Value.ToString().ToLower(), RepId = r.AccountOwnerId!.Value.ToString().ToLower() })
                .ToListAsync();

            var dict = reps.GroupBy(r => r.AccountId)
                .ToDictionary(g => g.Key, g => g.First().RepId);

            _cache.Set(cacheKey, dict, CacheTTL);
            return dict;
        }

        /// <summary>
        /// Satış temsilcisi adını 3-kademeli fallback ile çözümler:
        ///   1) TBL_VARUNA_ACCOUNT_REPRESENTATIVES (State=Active) — müşteri portföy ataması (kanonik)
        ///   2) Fırsat.CustomerRepresentativeId — fırsata özel atama
        ///   3) Fırsat.OwnerId — son çare (sahip)
        /// Tüm breakdown/filter/detay çağrıları bu helper üzerinden geçer.
        /// </summary>
        private string ResolveSalesRepName(
            string? accountId,
            string? customerRepresentativeId,
            string? ownerId,
            Dictionary<string, string> accountRepIdMap,
            Dictionary<string, string> personMap,
            Dictionary<string, string> ownerMap)
        {
            // 1) ACCOUNT_REPRESENTATIVES
            if (accountId != null
                && accountRepIdMap.TryGetValue(accountId.ToLower(), out var repId)
                && personMap.TryGetValue(repId, out var repName))
                return repName;
            // 2) Fırsat.CustomerRepresentativeId
            if (!string.IsNullOrEmpty(customerRepresentativeId)
                && personMap.TryGetValue(customerRepresentativeId.ToLower(), out var crn))
                return crn;
            // 3) Fırsat.OwnerId
            if (!string.IsNullOrEmpty(ownerId))
                return personMap.TryGetValue(ownerId.ToLower(), out var on)
                    ? on
                    : ResolveOwnerName(ownerId, ownerMap);
            return "Bilinmiyor";
        }

        // ───────────────────────────────────────────────────────────────
        // GEÇİCİ DEBUG: Müşterinin TBL_VARUNA_ACCOUNT_REPRESENTATIVES kayıtlarını döker
        // Sadece okuma — veriye dokunmaz.
        [HttpGet]
        public async Task<IActionResult> DebugAccountReps(string accountTitle)
        {
            using var db = _contextFactory.CreateDbContext();
            // 1) Müşteriyi bul (Title veya Name+SurName'de LIKE)
            var accounts = await db.Database.SqlQueryRaw<ProductGroupNameDto>(
                @"SELECT TOP 10 CAST(Id AS NVARCHAR(64)) AS Id,
                         COALESCE(NULLIF(LTRIM(RTRIM(Title)), ''),
                                  NULLIF(LTRIM(RTRIM(ISNULL(Name,'') + ' ' + ISNULL(SurName,''))), ''),
                                  CAST(Id AS NVARCHAR(64))) AS Name
                  FROM TBL_VARUNA_ACCOUNTS
                  WHERE Title LIKE '%' + {0} + '%' OR Name LIKE '%' + {0} + '%'",
                accountTitle).ToListAsync();

            var personMap = await GetPersonMapAsync(db);
            var result = new List<object>();
            foreach (var acc in accounts)
            {
                var reps = await db.TBL_VARUNA_ACCOUNT_REPRESENTATIVESs.AsNoTracking()
                    .Where(r => r.AccountId.HasValue && r.AccountId.Value.ToString().ToLower() == acc.Id!.ToLower())
                    .Select(r => new { r.AccountOwnerId, r.State })
                    .ToListAsync();
                result.Add(new
                {
                    accountId = acc.Id,
                    accountTitle = acc.Name,
                    repCount = reps.Count,
                    reps = reps.Select(r => new
                    {
                        state = r.State,
                        repId = r.AccountOwnerId?.ToString(),
                        repName = r.AccountOwnerId.HasValue && personMap.TryGetValue(r.AccountOwnerId.Value.ToString().ToLower(), out var n) ? n : null
                    })
                });
            }
            return Json(result);
        }

        // GET /FirsatAnaliz/GetOwnerFilterOptions — Filtre dropdown
        // ───────────────────────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> GetOwnerFilterOptions()
        {
            var cacheKey = "FirsatOwnerFilterOptions";
            if (_cache.TryGetValue(cacheKey, out object? cachedOptions) && cachedOptions != null)
                return Json(cachedOptions);

            using var db = _contextFactory.CreateDbContext();
            var ownerMap = await GetOwnerMapAsync();

            var owners = await ExcludeTestFirsat(db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking())
                .Where(o => o.OwnerId != null)
                .GroupBy(o => o.OwnerId!)
                .Select(g => new { ownerId = g.Key, adet = g.Count() })
                .OrderByDescending(x => x.adet)
                .ToListAsync();

            var result = owners.Select(o => new
            {
                o.ownerId,
                adSoyad = ResolveOwnerName(o.ownerId, ownerMap),
                o.adet
            }).ToList();

            _cache.Set(cacheKey, result, CacheTTL);
            return Json(result);
        }

        // ───────────────────────────────────────────────────────────────
        // GET /FirsatAnaliz/GetOpportunityDetail — Detay listesi
        // ───────────────────────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> GetOpportunityDetail(string? filter, string? startDate, string? endDate,
            string? owner, string? stage, string? customer, string? product, string? ownerName, string? firsatSahibi = null, int page = 1, int pageSize = 20, int? funnel = null, string? sort = null, string? dir = null)
        {
            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);
            // Sıralama: musteri (default) | name | temsilci | kategori | tutar | kapanis
            var sortKey = (sort ?? "musteri").ToLowerInvariant();
            if (sortKey != "musteri" && sortKey != "name" && sortKey != "temsilci"
                && sortKey != "kategori" && sortKey != "tutar" && sortKey != "kapanis"
                && sortKey != "teklif")
                sortKey = "musteri";
            // Yön: asc (default) | desc
            var sortDesc = string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase);

            // ── Cache kontrolü ──
            // v3 prefix: dual-key sentetik fatura fix — VIEW_CP_EXCEL_FATURA dışı (SAP: prefix'li) tüm faturaları kapsar.
            var detailCacheKey = $"FirsatDetail_v3_{start:yyyyMMdd}_{end:yyyyMMdd}_{stage ?? ""}_{owner ?? ""}_{customer ?? ""}_{product ?? ""}_{ownerName ?? ""}_{firsatSahibi ?? ""}_{funnel?.ToString() ?? ""}_{sortKey}_{(sortDesc ? "desc" : "asc")}_{page}";
            if (!IsForceRefresh() && _cache.TryGetValue(detailCacheKey, out object? cachedDetail) && cachedDetail != null)
                return Json(cachedDetail);

            using var db = _contextFactory.CreateDbContext();
            var ownerMap = await GetOwnerMapAsync();

            // ───────────────────────────────────────────────────────────────
            // Funnel 5 (Faturalandı) — sipariş/fatura bazlı detay.
            // Breakdown (4218–4369) ile BIREBIR aynı kaynak: SP fatura listesi → sipariş
            // eşleşmesi. Aynı opportunity birden çok faturaya bölünmüşse her fatura ayrı satır
            // olarak döner; toplam adet ve tutar müşteri kartıyla birebir eşleşir.
            // ───────────────────────────────────────────────────────────────
            if (funnel.HasValue && funnel.Value == 5)
            {
                var spFaturalarF5 = await _cockpitData.GetFaturalarAsync(start, end);
                var spFaturaNoSetF5 = spFaturalarF5.Select(f => f.FaturaNo).ToHashSet();
                var spTutarMapF5 = spFaturalarF5
                    .GroupBy(f => f.FaturaNo)
                    .ToDictionary(g => g.Key, g => g.Sum(f => f.NetTutar));

                // Dual-key: SP fatura listesi iki tip taşır — gerçek SerialNumber + sentetik "SAP:<SAPOutReferenceCode>".
                var directSerialSetF5 = spFaturaNoSetF5
                    .Where(f => !string.IsNullOrEmpty(f) && !f.StartsWith("SAP:", StringComparison.OrdinalIgnoreCase))
                    .ToHashSet();
                var sapRefSetF5 = spFaturaNoSetF5
                    .Where(f => !string.IsNullOrEmpty(f) && f.StartsWith("SAP:", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.Substring(4))
                    .ToHashSet();

                var f5SipQ = ExcludeTestSiparis(db.TBL_VARUNA_SIPARIs.AsNoTracking())
                    .Where(s =>
                        (s.SerialNumber != null && directSerialSetF5.Contains(s.SerialNumber))
                        || (s.SAPOutReferenceCode != null && sapRefSetF5.Contains(s.SAPOutReferenceCode)));

                if (!string.IsNullOrEmpty(customer))
                {
                    if (customer == "Tanımsız Müşteri")
                        f5SipQ = f5SipQ.Where(s => s.AccountTitle == null || s.AccountTitle == "");
                    else
                        f5SipQ = f5SipQ.Where(s => s.AccountTitle == customer);
                }

                var f5SipRaw = await f5SipQ
                    .Select(s => new
                    {
                        s.OrderId,
                        s.SerialNumber,
                        s.SAPOutReferenceCode,
                        s.AccountTitle,
                        s.AccountId,
                        s.QuoteId,
                        s.ProposalOwnerId,
                        s.InvoiceDate,
                        s.ModifiedOn
                    })
                    .ToListAsync();

                // MapKey: downstream her yerde fatura anahtarı bu — gerçek SerialNumber veya "SAP:<ref>".
                // Anonymous type shape eski f5Sip ile aynı (SerialNumber field name korundu); downstream kod değişmez.
                var f5Sip = f5SipRaw.Select(s => new
                {
                    s.OrderId,
                    SerialNumber = (s.SerialNumber != null && directSerialSetF5.Contains(s.SerialNumber))
                        ? s.SerialNumber
                        : "SAP:" + s.SAPOutReferenceCode,
                    s.AccountTitle,
                    s.AccountId,
                    s.QuoteId,
                    s.ProposalOwnerId,
                    s.InvoiceDate,
                    s.ModifiedOn
                }).ToList();

                // Ürün filtresi — sipariş ürünleri üzerinden
                if (!string.IsNullOrEmpty(product))
                {
                    var eslestirmeMapF5 = await GetUrunEslestirmeMapAsync();
                    var f5OrderIds = f5Sip.Select(s => s.OrderId).Where(o => o != null).Distinct().ToList();
                    var f5UrunRows = await db.TBL_VARUNA_SIPARIS_URUNLERIs.AsNoTracking()
                        .Where(u => u.CrmOrderId != null && f5OrderIds.Contains(u.CrmOrderId))
                        .Select(u => new { u.CrmOrderId, u.StockCode })
                        .ToListAsync();
                    var f5OrderProducts = f5UrunRows
                        .GroupBy(u => u.CrmOrderId!)
                        .ToDictionary(
                            g => g.Key,
                            g => g.Select(u => ResolveProductGroup(u.StockCode, eslestirmeMapF5)).ToHashSet());
                    f5Sip = f5Sip
                        .Where(s => s.OrderId != null
                            && f5OrderProducts.TryGetValue(s.OrderId, out var prods)
                            && prods.Contains(product))
                        .ToList();
                }

                // Quote → Opportunity eşleşme (4-step rep çözümü için gerekli)
                var f5QuoteIds = f5Sip.Select(s => s.QuoteId?.ToLower()).Where(q => q != null).Distinct().ToList();
                var f5TeklifMap = await db.TBL_VARUNA_TEKLIFs.AsNoTracking()
                    .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue)
                    .Select(t => new
                    {
                        TeklifId = t.Id.ToString().ToLower(),
                        OppId = t.OpportunityId!.Value.ToString().ToLower()
                    })
                    .ToListAsync();
                var f5QuoteToOpp = f5TeklifMap
                    .GroupBy(x => x.TeklifId)
                    .ToDictionary(g => g.Key, g => g.First().OppId);

                var f5OppIds = f5Sip
                    .Select(s => s.QuoteId != null && f5QuoteToOpp.TryGetValue(s.QuoteId.ToLower(), out var oid) ? oid : null)
                    .Where(o => o != null).Distinct().ToList();
                var f5OppRows = await db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking()
                    .Where(o => f5OppIds.Contains(o.Id))
                    .Select(o => new
                    {
                        o.Id,
                        o.Name,
                        o.OpportunityStageName,
                        o.DealType,
                        o.Probability,
                        o.CloseDate,
                        o.OwnerId,
                        o.CustomerRepresentativeId
                    })
                    .ToListAsync();
                var f5OppDict = f5OppRows
                    .GroupBy(o => o.Id.ToLower())
                    .ToDictionary(g => g.Key, g => g.First());

                // Teklif durumu lookup — fırsat → en son teklif (ModifiedOn desc → CreatedOn desc) + adet.
                // Funnel 5 (Faturalandı) dalında bile sipariş öncesi teklif kademesi yansıtılır.
                var f5OppIdsLowerSet = f5OppRows.Select(o => o.Id.ToLower()).ToHashSet();
                var f5TeklifInfoRows = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                    .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue)
                    .Select(t => new {
                        OppId = t.OpportunityId!.Value.ToString().ToLower(),
                        t.Status,
                        t.ModifiedOn,
                        t.CreatedOn
                    })
                    .ToListAsync();
                var f5TeklifInfoByOpp = f5TeklifInfoRows
                    .Where(t => f5OppIdsLowerSet.Contains(t.OppId))
                    .GroupBy(t => t.OppId)
                    .ToDictionary(g => g.Key, g => {
                        var latest = g.OrderByDescending(t => t.ModifiedOn ?? t.CreatedOn ?? DateTime.MinValue).First();
                        return (Adet: g.Count(), LatestStatus: latest.Status);
                    });

                var f5PersonMap = await GetPersonMapAsync(db);
                var f5AccountRepMap = await GetAccountToRepMapAsync(db);

                // 3-step çözümle, ownerName / firsatSahibi filtrelerini uygula
                var f5Enriched = f5Sip.Select(s =>
                {
                    var oppId = s.QuoteId != null && f5QuoteToOpp.TryGetValue(s.QuoteId.ToLower(), out var oid) ? oid : null;
                    var opp = oppId != null && f5OppDict.TryGetValue(oppId, out var o) ? o : null;
                    var efektifOwnerId = opp?.OwnerId ?? s.ProposalOwnerId;
                    var satisRep = ResolveSalesRepName(
                        s.AccountId,
                        opp?.CustomerRepresentativeId,
                        efektifOwnerId,
                        f5AccountRepMap, f5PersonMap, ownerMap);
                    var firsatSahibiName = efektifOwnerId != null
                        ? (f5PersonMap.TryGetValue(efektifOwnerId.ToLower(), out var pn) ? pn : ResolveOwnerName(efektifOwnerId, ownerMap))
                        : "Bilinmiyor";
                    return new
                    {
                        s.SerialNumber,
                        s.AccountTitle,
                        s.QuoteId,
                        s.InvoiceDate,
                        s.ModifiedOn,
                        Opp = opp,
                        SatisRep = satisRep,
                        FirsatSahibi = firsatSahibiName,
                        Tutar = spTutarMapF5.GetValueOrDefault(s.SerialNumber!, 0m)
                    };
                }).ToList();

                if (!string.IsNullOrEmpty(ownerName))
                    f5Enriched = f5Enriched.Where(x => x.SatisRep == ownerName).ToList();
                if (!string.IsNullOrEmpty(firsatSahibi))
                    f5Enriched = f5Enriched.Where(x => x.FirsatSahibi == firsatSahibi).ToList();

                // Aynı SerialNumber birden fazla sipariş satırında olabilir (çoklu ürün) — fatura bazında dedupe.
                var f5Distinct = f5Enriched
                    .Where(x => x.SerialNumber != null)
                    .GroupBy(x => x.SerialNumber!)
                    .Select(g => g.First())
                    .ToList();

                var totalF5 = f5Distinct.Count;
                var trComparerF5 = StringComparer.Create(new System.Globalization.CultureInfo("tr-TR"), ignoreCase: true);
                // Sıralama: sortKey (musteri/name/temsilci/kategori/tutar/kapanis) + sortDesc.
                // Boş alanlar her durumda sona; tie-break = müşteri tr-TR + InvoiceDate desc.
                var orderedF5 = sortKey switch
                {
                    "name" => sortDesc
                        ? f5Distinct.OrderBy(x => string.IsNullOrWhiteSpace(x.Opp?.Name) ? 1 : 0)
                                   .ThenByDescending(x => x.Opp?.Name ?? "", trComparerF5)
                        : f5Distinct.OrderBy(x => string.IsNullOrWhiteSpace(x.Opp?.Name) ? 1 : 0)
                                   .ThenBy(x => x.Opp?.Name ?? "", trComparerF5),
                    "temsilci" => sortDesc
                        ? f5Distinct.OrderBy(x => string.IsNullOrWhiteSpace(x.SatisRep) ? 1 : 0)
                                   .ThenByDescending(x => x.SatisRep ?? "", trComparerF5)
                        : f5Distinct.OrderBy(x => string.IsNullOrWhiteSpace(x.SatisRep) ? 1 : 0)
                                   .ThenBy(x => x.SatisRep ?? "", trComparerF5),
                    "kategori" => sortDesc
                        ? f5Distinct.OrderByDescending(x => string.Equals(x.Opp?.DealType, "Renovation", StringComparison.OrdinalIgnoreCase) ? "Yenileme" : "Yeni Satış", trComparerF5)
                        : f5Distinct.OrderBy(x => string.Equals(x.Opp?.DealType, "Renovation", StringComparison.OrdinalIgnoreCase) ? "Yenileme" : "Yeni Satış", trComparerF5),
                    "tutar" => sortDesc
                        ? f5Distinct.OrderByDescending(x => x.Tutar)
                        : f5Distinct.OrderBy(x => x.Tutar),
                    "kapanis" => sortDesc
                        ? f5Distinct.OrderBy(x => (x.InvoiceDate ?? x.ModifiedOn ?? x.Opp?.CloseDate).HasValue ? 0 : 1)
                                   .ThenByDescending(x => x.InvoiceDate ?? x.ModifiedOn ?? x.Opp?.CloseDate)
                        : f5Distinct.OrderBy(x => (x.InvoiceDate ?? x.ModifiedOn ?? x.Opp?.CloseDate).HasValue ? 0 : 1)
                                   .ThenBy(x => x.InvoiceDate ?? x.ModifiedOn ?? x.Opp?.CloseDate),
                    "teklif" => sortDesc
                        ? f5Distinct.OrderByDescending(x => {
                            var oid = x.Opp?.Id.ToLower();
                            return oid != null && f5TeklifInfoByOpp.TryGetValue(oid, out var tk) ? StatusToTurkishStage(tk.LatestStatus) : "Yok";
                          }, trComparerF5)
                        : f5Distinct.OrderBy(x => {
                            var oid = x.Opp?.Id.ToLower();
                            return oid != null && f5TeklifInfoByOpp.TryGetValue(oid, out var tk) ? StatusToTurkishStage(tk.LatestStatus) : "Yok";
                          }, trComparerF5),
                    _ => sortDesc
                        ? f5Distinct.OrderBy(x => string.IsNullOrWhiteSpace(x.AccountTitle) ? 1 : 0)
                                   .ThenByDescending(x => x.AccountTitle ?? "", trComparerF5)
                        : f5Distinct.OrderBy(x => string.IsNullOrWhiteSpace(x.AccountTitle) ? 1 : 0)
                                   .ThenBy(x => x.AccountTitle ?? "", trComparerF5)
                };
                var pagedF5 = orderedF5
                    .ThenByDescending(x => x.InvoiceDate ?? x.ModifiedOn)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToList();

                var resultF5 = pagedF5.Select(x => {
                    var oppIdLow = x.Opp?.Id.ToLower();
                    var hasTeklif = oppIdLow != null && f5TeklifInfoByOpp.TryGetValue(oppIdLow, out var tk);
                    var teklifAdet = hasTeklif ? f5TeklifInfoByOpp[oppIdLow!].Adet : 0;
                    var teklifDurumu = hasTeklif
                        ? StatusToTurkishStage(f5TeklifInfoByOpp[oppIdLow!].LatestStatus)
                        : "Yok";
                    return (object)new
                    {
                        Name = x.Opp?.Name ?? x.SerialNumber,
                        asama = x.Opp?.OpportunityStageName ?? "Faturalandı",
                        kategori = string.Equals(x.Opp?.DealType, "Renovation", StringComparison.OrdinalIgnoreCase) ? "Yenileme" : "Yeni Satış",
                        olasilik = x.Opp?.Probability ?? 100m,
                        tutar = (decimal?)x.Tutar,
                        kapanisTarihi = (x.InvoiceDate ?? x.ModifiedOn ?? x.Opp?.CloseDate)?.ToString("dd.MM.yyyy"),
                        satisTemsilcisi = x.SatisRep,
                        musteri = string.IsNullOrWhiteSpace(x.AccountTitle) ? "Tanımsız Müşteri" : x.AccountTitle!,
                        teklifDurumu,
                        teklifAdet
                    };
                }).ToList<object>();

                var detailResultF5 = new { total = totalF5, page, pageSize, items = resultF5 };
                _cache.Set(detailCacheKey, detailResultF5, CacheTTL);
                return Json(detailResultF5);
            }

            // TBL_VARUNA_OPPORTUNITIES — CloseDate bazlı filtreleme
            var query = ExcludeTestFirsat(db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking())
                .Where(o => o.CloseDate.HasValue
                    && o.CloseDate.Value >= start && o.CloseDate.Value <= end);

            // Funnel (exclusive pipeline) filtresi — breakdown ile BIREBIR aynı küme
            // funnel=34: kümülatif teklif (Beklemede ∪ Kabul edildi) = ExTeklifIds ∪ ExSiparisIds
            if (funnel.HasValue && ((funnel.Value >= 2 && funnel.Value <= 5) || funnel.Value == 34))
            {
                // Breakdown ile aynı semantik: Lost ve Closed aşamalar hariç
                query = query.Where(o => o.OpportunityStageName != "Lost"
                    && (o.OpportunityStageName == null || !o.OpportunityStageName.Contains("Closed")));
                // Tahakkukla dönem dışına kayan kapalı siparişli fırsatları hariç tut (breakdown ile aynı kapaliSet)
                var kapaliSet = await ComputeKapaliDonemDisiSetAsync(start, end);
                var donemFirsatIdsRawDet = await query.Select(o => o.Id).ToListAsync();
                var donemFirsatIdSetLower = donemFirsatIdsRawDet
                    .Where(id => !kapaliSet.Contains(id.ToLower()))
                    .Select(id => id.ToLower())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var exSets = await GetExclusiveSetsAsync(start, end, null, donemFirsatIdSetLower);
                HashSet<string> targetSet = funnel.Value switch
                {
                    5 => exSets.ExFaturaIds,
                    4 => exSets.ExSiparisIds,
                    3 => exSets.ExTeklifIds,
                    34 => new HashSet<string>(exSets.ExTeklifIds.Concat(exSets.ExSiparisIds), StringComparer.OrdinalIgnoreCase),
                    _ => exSets.ExFirsatIds,
                };
                var matchedIds = donemFirsatIdsRawDet
                    .Where(id => targetSet.Contains(id.ToLower()))
                    .ToHashSet();
                query = query.Where(o => matchedIds.Contains(o.Id));
            }

            if (!string.IsNullOrEmpty(owner))
                query = query.Where(o => o.OwnerId == owner);
            if (!string.IsNullOrEmpty(stage))
                query = query.Where(o => o.OpportunityStageName == stage);

            // NOT: Satış Temsilcisi (ownerName) filtresi resolved listesi materialize edildikten sonra
            //      uygulanır — render edilen `satisTemsilcisi` değeri ile filtre tutarlı olsun diye.

            // Müşteri filtresi — TBL_VARUNA_ACCOUNTS map'inden AccountId eşleşmesi
            if (!string.IsNullOrEmpty(customer))
            {
                var accTitleMap = await GetAccountTitleMapAsync(db);
                if (customer == "Tanımsız Müşteri")
                {
                    var knownAccIds = accTitleMap.Keys.ToHashSet();
                    query = query.Where(o =>
                        (o.AccountTitle == null || o.AccountTitle == "")
                        && (o.AccountId == null || !knownAccIds.Contains(o.AccountId.ToLower())));
                }
                else
                {
                    var matchAccIds = accTitleMap
                        .Where(kv => kv.Value == customer)
                        .Select(kv => kv.Key).ToHashSet();
                    query = query.Where(o =>
                        o.AccountTitle == customer
                        || (o.AccountId != null && matchAccIds.Contains(o.AccountId.ToLower())));
                }
            }

            // Ürün filtresi — aşama-bazlı resolver-aware
            if (!string.IsNullOrEmpty(product))
            {
                if (product == "Tanımsız" || product == "Diğer")
                {
                    query = query.Where(o => o.ProductGroupId == null);
                }
                else
                {
                    var prodIdSet = await ResolveOppIdsByProductGroupAsync(db, product);
                    query = query.Where(o => o.Id != null && prodIdSet.Contains(o.Id.ToLower()));
                }
            }

            // Fırsat Sahibi filtresi — direkt fırsat tablosundan (OwnerId → Person adı)
            if (!string.IsNullOrEmpty(firsatSahibi))
            {
                var fsPersonIds = await db.TBL_VARUNA_PERSONs.AsNoTracking()
                    .Where(p => p.PersonNameSurname == firsatSahibi && p.DeletedOn == null)
                    .Select(p => p.Id).ToListAsync();
                var fsPidSet = fsPersonIds.Select(id => id.ToLower()).ToHashSet();
                query = query.Where(o => o.OwnerId != null && fsPidSet.Contains(o.OwnerId.ToLower()));
            }

            // Müşteri ismine göre alfabetik sıralama gerektiği için tüm satırları çekiyoruz;
            // çözümleme post-query (account map + teklif sahibi) yapıldığı için SQL düzeyinde sort olamaz.
            // Filtrelenmiş dönem genelde 500-2000 satır, in-memory sort makul.
            // Total = ownerName filtresi resolved sonrası uygulanacağı için, count'ı resolved'dan alacağız.
            var rawItems = await query
                .Select(o => new
                {
                    o.Id,
                    o.Name,
                    o.OpportunityStageName,
                    DealTypeTR = o.DealType,
                    o.Probability,
                    o.CloseDate,
                    o.AmountAmount,
                    ownerId = o.OwnerId,
                    customerRepId = o.CustomerRepresentativeId,
                    o.Source,
                    // Müşteri çözümü için: fırsatın kendi AccountId/AccountTitle'ı kullanılır (teklife BAĞLI DEĞİL)
                    oppAccountId = o.AccountId,
                    oppAccountTitle = o.AccountTitle
                })
                .ToListAsync();

            // Müşteri isimleri: Fırsat.AccountId → TBL_VARUNA_ACCOUNTS (Title öncelikli, yoksa Name+SurName)
            // Fallback: fırsatın kendi AccountTitle alanı. Teklifle İŞİMİZ YOK.
            var accountTitleMap = await GetAccountTitleMapAsync(db);

            // Teklif durumu lookup: en son teklif (ModifiedOn desc → CreatedOn desc) + adet → "Teklif Durumu" kolonu.
            // ProposalOwnerId artık satış temsilcisi çözümünde KULLANILMIYOR (kanonik zincir REPS → CustRepId → OwnerId).
            var detayTeklifAll = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue)
                .Select(t => new {
                    OppId = t.OpportunityId!.Value.ToString().ToLower(),
                    t.Status,
                    t.ModifiedOn,
                    t.CreatedOn
                })
                .ToListAsync();
            var detayOppToTeklifInfo = detayTeklifAll
                .GroupBy(t => t.OppId)
                .ToDictionary(g => g.Key, g => {
                    var latest = g.OrderByDescending(t => t.ModifiedOn ?? t.CreatedOn ?? DateTime.MinValue).First();
                    return (Adet: g.Count(), LatestStatus: latest.Status);
                });

            // Satış Temsilcisi: kanonik 3-kademeli zincir (ResolveSalesRepName).
            //   1) ACCOUNT_REPRESENTATIVES (State=Active) — müşteri portföy ataması
            //   2) Fırsat.CustomerRepresentativeId
            //   3) Fırsat.OwnerId — son çare
            // (Eski TEKLIF.ProposalOwnerId yolu kaldırıldı — teklifi hazırlayan ≠ satış temsilcisi)
            var detailPersonMap = await GetPersonMapAsync(db);
            var detailAccountRepMap = await GetAccountToRepMapAsync(db);

            var resolved = rawItems.Select(i =>
            {
                string satisTemsilcisi = ResolveSalesRepName(
                    i.oppAccountId,
                    i.customerRepId,
                    i.ownerId,
                    detailAccountRepMap, detailPersonMap, ownerMap);
                string musteri = (i.oppAccountId != null && accountTitleMap.TryGetValue(i.oppAccountId.ToLower(), out var accName) && !string.IsNullOrWhiteSpace(accName))
                    ? accName
                    : (i.oppAccountTitle ?? "");

                // Teklif durumu: en son teklif Status'una göre Türkçeleştirilmiş etiket; teklif yoksa "Yok".
                int teklifAdet = 0;
                string teklifDurumu = "Yok";
                var oppIdLow = i.Id?.ToLower();
                if (oppIdLow != null && detayOppToTeklifInfo.TryGetValue(oppIdLow, out var tkInfo))
                {
                    teklifAdet = tkInfo.Adet;
                    teklifDurumu = StatusToTurkishStage(tkInfo.LatestStatus);
                }

                return new
                {
                    i.Name,
                    asama = i.OpportunityStageName,
                    kategori = string.Equals(i.DealTypeTR, "Renovation", StringComparison.OrdinalIgnoreCase) ? "Yenileme" : "Yeni Satış",
                    olasilik = i.Probability,
                    tutar = i.AmountAmount,
                    i.CloseDate,
                    kapanisTarihi = i.CloseDate?.ToString("dd.MM.yyyy"),
                    satisTemsilcisi,
                    musteri,
                    teklifDurumu,
                    teklifAdet
                };
            }).ToList();

            // Satış Temsilcisi (ownerName) filtresi — resolved.satisTemsilcisi ile birebir eşleşir,
            // böylece UI'daki dropdown seçimi ile tablonun gösterdiği kolon değeri tutarlı kalır.
            if (!string.IsNullOrEmpty(ownerName))
            {
                resolved = resolved.Where(r => r.satisTemsilcisi == ownerName).ToList();
            }

            var total = resolved.Count;

            // Sıralama: sortKey (musteri/name/temsilci/kategori/tutar/kapanis) + sortDesc.
            // Boş alanlar her durumda sona; tie-break = CloseDate desc.
            var trComparer = StringComparer.Create(new System.Globalization.CultureInfo("tr-TR"), ignoreCase: true);
            var ordered = sortKey switch
            {
                "name" => sortDesc
                    ? resolved.OrderBy(x => string.IsNullOrWhiteSpace(x.Name) ? 1 : 0)
                              .ThenByDescending(x => x.Name ?? "", trComparer)
                    : resolved.OrderBy(x => string.IsNullOrWhiteSpace(x.Name) ? 1 : 0)
                              .ThenBy(x => x.Name ?? "", trComparer),
                "temsilci" => sortDesc
                    ? resolved.OrderBy(x => string.IsNullOrWhiteSpace(x.satisTemsilcisi) ? 1 : 0)
                              .ThenByDescending(x => x.satisTemsilcisi ?? "", trComparer)
                    : resolved.OrderBy(x => string.IsNullOrWhiteSpace(x.satisTemsilcisi) ? 1 : 0)
                              .ThenBy(x => x.satisTemsilcisi ?? "", trComparer),
                "kategori" => sortDesc
                    ? resolved.OrderByDescending(x => x.kategori ?? "", trComparer)
                    : resolved.OrderBy(x => x.kategori ?? "", trComparer),
                "tutar" => sortDesc
                    ? resolved.OrderByDescending(x => x.tutar ?? 0m)
                    : resolved.OrderBy(x => x.tutar ?? 0m),
                "kapanis" => sortDesc
                    ? resolved.OrderBy(x => x.CloseDate.HasValue ? 0 : 1)
                              .ThenByDescending(x => x.CloseDate)
                    : resolved.OrderBy(x => x.CloseDate.HasValue ? 0 : 1)
                              .ThenBy(x => x.CloseDate),
                "teklif" => sortDesc
                    ? resolved.OrderByDescending(x => x.teklifDurumu ?? "", trComparer)
                    : resolved.OrderBy(x => x.teklifDurumu ?? "", trComparer),
                _ => sortDesc
                    ? resolved.OrderBy(x => string.IsNullOrWhiteSpace(x.musteri) ? 1 : 0)
                              .ThenByDescending(x => x.musteri ?? "", trComparer)
                    : resolved.OrderBy(x => string.IsNullOrWhiteSpace(x.musteri) ? 1 : 0)
                              .ThenBy(x => x.musteri ?? "", trComparer)
            };
            var result = ordered
                .ThenByDescending(x => x.CloseDate)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(x => new
                {
                    x.Name,
                    x.asama,
                    x.kategori,
                    x.olasilik,
                    x.tutar,
                    x.kapanisTarihi,
                    x.satisTemsilcisi,
                    x.musteri,
                    x.teklifDurumu,
                    x.teklifAdet
                })
                .ToList();

            var detailResult = new { total, page, pageSize, items = result };
            _cache.Set(detailCacheKey, detailResult, CacheTTL);
            return Json(detailResult);
        }

        // ── Exclusive Pipeline Sets ──
        private record ExclusivePipelineSets(
            HashSet<string> ExFirsatIds,
            HashSet<string> ExTeklifIds,
            HashSet<string> ExSiparisIds,
            HashSet<string> ExFaturaIds
        );

        /// <summary>
        /// Exclusive pipeline: her fırsat EN İLERİ aşamasına göre tek bir sete atanır.
        /// K5 (Fatura) > K4 (Sipariş) > K3 (Teklif) > K2 (Fırsat)
        /// </summary>
        // Tüm-zaman faturalı opportunity'ler — 10 dk cache. cumulative funnel için exclusive eleme kümesi.
        private const string ALL_TIME_FATURA_CACHE_KEY = "FA_AllTimeFaturaOppSet_v1";
        private async Task<HashSet<string>> GetAllTimeFaturaOppSetAsync()
        {
            if (_cache.TryGetValue(ALL_TIME_FATURA_CACHE_KEY, out HashSet<string>? cached) && cached != null)
                return cached;
            using var db = _contextFactory.CreateDbContext();
            db.Database.SetCommandTimeout(180);
            var ids = await (from t in ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                             where t.DeletedOn == null && t.OpportunityId.HasValue
                             join s in ExcludeTestSiparis(db.TBL_VARUNA_SIPARIs.AsNoTracking())
                                     .Where(s => s.OrderStatus == "Closed"
                                                 && (s.SerialNumber != null || s.SAPOutReferenceCode != null))
                                 on t.Id.ToString() equals s.QuoteId
                             select t.OpportunityId!.Value.ToString().ToLower())
                            .Distinct().ToListAsync();
            var set = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _cache.Set(ALL_TIME_FATURA_CACHE_KEY, set, TimeSpan.FromMinutes(10));
            return set;
        }

        private async Task<ExclusivePipelineSets> GetExclusiveSetsAsync(
            DateTime start, DateTime end, string? owner,
            HashSet<string> donemFirsatIdSetLower)
        {
            using var dbExSip = _contextFactory.CreateDbContext();
            using var dbExTek = _contextFactory.CreateDbContext();
            using var dbCockpitFat = _contextFactory.CreateDbContext();

            // Sipariş → fırsat bağlantısı (Open + Closed, tüm zamanlar)
            // Not: Open = henüz faturalanmamış, Closed = faturalanmış veya faturalanacak.
            // exFaturaIds zaten dönem içi faturalı fırsatları yakaladığı için, Closed ama faturası
            // bu dönemde olmayan fırsatlar (tahakkukla başka aya kaymış veya fatura hiç çıkmamış)
            // exSiparis bucket'ında doğru şekilde görünür.
            var siparisOppTask = ExcludeTest(dbExSip.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue)
                .Join(ExcludeTestSiparis(dbExSip.TBL_VARUNA_SIPARIs.AsNoTracking())
                    .Where(s => (s.OrderStatus == "Open" || s.OrderStatus == "Closed") && s.QuoteId != null),
                    t => t.Id.ToString(), s => s.QuoteId,
                    (t, s) => t.OpportunityId!.Value.ToString().ToLower())
                .Distinct().ToListAsync();

            // Aktif teklif → fırsat bağlantısı (tüm zamanlar)
            var aktifTeklifOppTask = ExcludeTest(dbExTek.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue
                    && (t.Status == null || (t.Status != "Reject" && t.Status != "Denied" && t.Status != "Closed")))
                .Select(t => t.OpportunityId!.Value.ToString().ToLower())
                .Distinct().ToListAsync();

            // Cockpit fatura + fırsat bağlantısı
            var cockpitFaturaTask = _cockpitData.GetFaturalarAsync(start, end, owner);
            // Dual-key: sentetik faturalı opportunity'leri de exFaturaIds'e bağla.
            var cockpitFaturaOppTask = ExcludeTest(dbCockpitFat.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue)
                .Join(ExcludeTestSiparis(dbCockpitFat.TBL_VARUNA_SIPARIs.AsNoTracking())
                    .Where(s => s.OrderStatus == "Closed"
                                && (s.SerialNumber != null || s.SAPOutReferenceCode != null)),
                    t => t.Id.ToString(), s => s.QuoteId,
                    (t, s) => new { OppId = t.OpportunityId!.Value.ToString().ToLower(), s.SerialNumber, s.SAPOutReferenceCode })
                .ToListAsync();

            // TÜM-ZAMAN fatura kesilmiş opportunity'ler — cache'li helper.
            var allTimeFaturaOppTask = GetAllTimeFaturaOppSetAsync();

            await Task.WhenAll(siparisOppTask, aktifTeklifOppTask, cockpitFaturaTask, cockpitFaturaOppTask, allTimeFaturaOppTask);

            var siparisOppSet = siparisOppTask.Result.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var aktifTeklifOppSet = aktifTeklifOppTask.Result.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var cockpitFaturalar = cockpitFaturaTask.Result;
            var cockpitFaturaNoSet = cockpitFaturalar.Select(f => f.FaturaNo).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var directSerialSet_xF2 = cockpitFaturaNoSet
                .Where(f => !string.IsNullOrEmpty(f) && !f.StartsWith("SAP:", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sapRefSet_xF2 = cockpitFaturaNoSet
                .Where(f => !string.IsNullOrEmpty(f) && f.StartsWith("SAP:", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Substring(4))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var cockpitFaturaOppIds = cockpitFaturaOppTask.Result
                .Where(x =>
                    (x.SerialNumber != null && directSerialSet_xF2.Contains(x.SerialNumber))
                    || (x.SAPOutReferenceCode != null && sapRefSet_xF2.Contains(x.SAPOutReferenceCode)))
                .Select(x => x.OppId).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // K5 — FaturaSet: Cockpit faturalarına bağlı fırsatlar
            var exFaturaIds = donemFirsatIdSetLower
                .Where(id => cockpitFaturaOppIds.Contains(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Tüm-zaman faturası olan opp'lar — başka bir dönemde faturalandıysa burada teklif/sipariş sayılmamalı.
            var allTimeFaturaOppSet = allTimeFaturaOppTask.Result;

            // K4 — SiparisSet: Siparişi olan, FaturaSet (dönem + tüm-zaman) hariç
            var exSiparisIds = donemFirsatIdSetLower
                .Where(id => siparisOppSet.Contains(id)
                             && !exFaturaIds.Contains(id)
                             && !allTimeFaturaOppSet.Contains(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // K3 — TeklifSet: Aktif teklifi olan, Fatura (dönem + tüm-zaman) + Sipariş hariç
            var exTeklifIds = donemFirsatIdSetLower
                .Where(id => aktifTeklifOppSet.Contains(id)
                             && !exFaturaIds.Contains(id)
                             && !exSiparisIds.Contains(id)
                             && !allTimeFaturaOppSet.Contains(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // K2 — FirsatSet: Hiçbir bağlantısı yok (teklif, sipariş, fatura yok)
            var exFirsatIds = donemFirsatIdSetLower
                .Where(id => !exFaturaIds.Contains(id) && !exSiparisIds.Contains(id) && !exTeklifIds.Contains(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return new ExclusivePipelineSets(exFirsatIds, exTeklifIds, exSiparisIds, exFaturaIds);
        }

        /// <summary>
        /// Tahakkuk-aware kapali set: kapalı siparişi olan fırsatların efektif faturalama tarihi
        /// dönem DIŞINA düşüyorsa bu fırsatlar sete girer. Breakdown ile detay endpoint'lerinin
        /// aynı fırsat kümesini görmesi için iki yerde de aynı helper kullanılır.
        /// Memory cache'lenir — dönem bazlı, 5dk TTL.
        /// </summary>
        private async Task<HashSet<string>> ComputeKapaliDonemDisiSetAsync(DateTime start, DateTime end)
        {
            var kCacheKey = $"KapaliDonemDisi_{start:yyyyMMdd}_{end:yyyyMMdd}";
            if (_cache.TryGetValue(kCacheKey, out HashSet<string>? cached) && cached != null)
                return cached;

            using var db = _contextFactory.CreateDbContext();
            var tahakkukMap = await _tahakkukService.GetTahakkukMapAsync();
            var kapaliZincir = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue)
                .Join(ExcludeTestSiparis(db.TBL_VARUNA_SIPARIs.AsNoTracking())
                    .Where(s => s.OrderStatus == "Closed" && s.QuoteId != null),
                    t => t.Id.ToString(), s => s.QuoteId,
                    (t, s) => new { OppId = t.OpportunityId!.Value.ToString().ToLower(), s.SerialNumber, s.SAPOutReferenceCode, s.InvoiceDate })
                .ToListAsync();
            var wonIds = await ExcludeTestFirsat(db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking())
                .Where(o => o.OpportunityStageName == "Won")
                .Select(o => o.Id!.ToLower()).ToListAsync();
            var wonSet = wonIds.ToHashSet();
            var teklifMusteri = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue && t.Account_Title != null)
                .Select(t => new { OppId = t.OpportunityId!.Value.ToString().ToLower(), t.Account_Title })
                .ToListAsync();
            var oppMusteri = teklifMusteri.Where(t => wonSet.Contains(t.OppId))
                .GroupBy(t => t.OppId).ToDictionary(g => g.Key, g => g.First().Account_Title!.Trim().ToLower());
            var closedSip = await ExcludeTestSiparis(db.TBL_VARUNA_SIPARIs.AsNoTracking())
                .Where(s => s.OrderStatus == "Closed" && s.SerialNumber != null && s.AccountTitle != null)
                .Select(s => new { s.SerialNumber, s.SAPOutReferenceCode, s.InvoiceDate, AccountTitle = s.AccountTitle!.Trim().ToLower() })
                .ToListAsync();
            var musteriEfektif = closedSip.GroupBy(s => s.AccountTitle)
                .ToDictionary(g => g.Key, g => {
                    foreach (var s in g.OrderByDescending(x => x.InvoiceDate))
                    {
                        var ef = EfektifInvoice(s.SerialNumber, s.SAPOutReferenceCode, s.InvoiceDate, tahakkukMap);
                        if (ef.HasValue && s.InvoiceDate.HasValue && ef.Value != s.InvoiceDate.Value) return ef;
                    }
                    return g.OrderByDescending(x => x.InvoiceDate).First().InvoiceDate;
                });
            var kapaliOppEfektif = kapaliZincir.GroupBy(x => x.OppId)
                .ToDictionary(g => g.Key, g => EfektifInvoice(g.First().SerialNumber, g.First().SAPOutReferenceCode, g.First().InvoiceDate, tahakkukMap));

            // Customer-level fallback: fırsatın KENDİ kapalı sipariş zinciri yok ve AKTİF teklifi de yoksa uygula.
            // Aktif teklif varsa → süreç devam ediyor, fırsat Teklif aşamasında kalmalı (müşterinin eski
            // faturası alakasız). Örn: HAYAT KİMYA yeni bakım sözleşmesi fırsatı.
            var aktifTeklifOppSet = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue
                    && (t.Status == null || (t.Status != "Reject" && t.Status != "Denied" && t.Status != "Closed")))
                .Select(t => t.OpportunityId!.Value.ToString().ToLower())
                .Distinct().ToListAsync();
            var aktifTeklifOppHash = aktifTeklifOppSet.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in oppMusteri)
                if (!kapaliOppEfektif.ContainsKey(kv.Key)
                    && !aktifTeklifOppHash.Contains(kv.Key)
                    && musteriEfektif.TryGetValue(kv.Value, out var ef))
                    kapaliOppEfektif[kv.Key] = ef;

            var result = kapaliOppEfektif
                .Where(kv => kv.Value.HasValue && (kv.Value.Value < start || kv.Value.Value > end))
                .Select(kv => kv.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _cache.Set(kCacheKey, result, CacheTTL);
            return result;
        }

        // ───────────────────────────────────────────────────────────────
        // GET /FirsatAnaliz/GetFunnelBreakdown?filter=month&funnel=4
        // Huni kartına tıklandığında: ürün + owner dağılımı
        // funnel: 1=tüm fırsatlar, 2=dönem fırsatlar, 3=teklifler, 4=siparişler, 5=faturalanan
        // ───────────────────────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> GetFunnelBreakdown(string? filter, string? startDate, string? endDate, int funnel = 2,
            string? customer = null, string? product = null, string? ownerName = null, string? firsatSahibi = null)
        {
            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);

            // ── Cache kontrolü ──
            // v3 prefix: top10 + expandable "Diğer" (detay'lı). Eski cache invalid.
            var cacheKey = $"FirsatFunnel_v3_{start:yyyyMMdd}_{end:yyyyMMdd}_{funnel}_{customer ?? ""}_{product ?? ""}_{ownerName ?? ""}_{firsatSahibi ?? ""}";
            if (!IsForceRefresh() && _cache.TryGetValue(cacheKey, out object? cachedFunnel) && cachedFunnel != null)
                return Json(cachedFunnel);

            using var db = _contextFactory.CreateDbContext();
            var ownerMap = await GetOwnerMapAsync();
            var eslestirmeMap = await GetUrunEslestirmeMapAsync();

            // Dönem fırsatlarını al (CloseDate bazlı)
            var firsatQuery = ExcludeTestFirsat(db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking())
                .Where(o => o.CloseDate.HasValue && o.CloseDate.Value >= start && o.CloseDate.Value <= end);

            // Müşteri filtresi — TBL_VARUNA_ACCOUNTS map'inden AccountId eşleşmesi
            if (!string.IsNullOrEmpty(customer))
            {
                var accTitleMap = await GetAccountTitleMapAsync(db);
                if (customer == "Tanımsız Müşteri")
                {
                    var knownAccIds = accTitleMap.Keys.ToHashSet();
                    firsatQuery = firsatQuery.Where(o =>
                        (o.AccountTitle == null || o.AccountTitle == "")
                        && (o.AccountId == null || !knownAccIds.Contains(o.AccountId.ToLower())));
                }
                else
                {
                    var matchAccIds = accTitleMap
                        .Where(kv => kv.Value == customer)
                        .Select(kv => kv.Key).ToHashSet();
                    firsatQuery = firsatQuery.Where(o =>
                        o.AccountTitle == customer
                        || (o.AccountId != null && matchAccIds.Contains(o.AccountId.ToLower())));
                }
            }

            // Ürün filtresi — aşama-bazlı resolver-aware (sipariş/teklif kalemi varsa o yol, yoksa ProductGroupId 1 seviye parent)
            if (!string.IsNullOrEmpty(product))
            {
                if (product == "Tanımsız" || product == "Diğer")
                {
                    firsatQuery = firsatQuery.Where(o => o.ProductGroupId == null);
                }
                else
                {
                    var prodOppIds = await ResolveOppIdsByProductGroupAsync(db, product);
                    firsatQuery = firsatQuery.Where(o => o.Id != null && prodOppIds.Contains(o.Id.ToLower()));
                }
            }

            // Fırsat Sahibi filtresi — direkt fırsat tablosundan (OwnerId → Person adı)
            if (!string.IsNullOrEmpty(firsatSahibi))
            {
                var fsPersonIds = await db.TBL_VARUNA_PERSONs.AsNoTracking()
                    .Where(p => p.PersonNameSurname == firsatSahibi && p.DeletedOn == null)
                    .Select(p => p.Id).ToListAsync();
                var fsPidSet = fsPersonIds.Select(id => id.ToLower()).ToHashSet();
                firsatQuery = firsatQuery.Where(o => o.OwnerId != null && fsPidSet.Contains(o.OwnerId.ToLower()));
            }

            // Satış Temsilcisi filtresi (ownerName) — asıl filtreleme breakdown seviyesinde (firsatOwnerData)
            // Burada sadece funnel>4 (sipariş/fatura) için person ID seti hazırlanır
            HashSet<string>? ownerPidSet = null;
            if (!string.IsNullOrEmpty(ownerName))
            {
                var pidsFunnel = await db.TBL_VARUNA_PERSONs.AsNoTracking()
                    .Where(p => p.PersonNameSurname == ownerName && p.DeletedOn == null)
                    .Select(p => p.Id).ToListAsync();
                ownerPidSet = pidsFunnel.Select(id => id.ToLower()).ToHashSet();
            }

            // Tahakkuk bazlı kapalı fırsat haritası (havuz-seviye, dönem-bağımsız, 5dk cache)
            // GetOpportunitySummary ile aynı haritayı paylaşır — duplicate EF query yok.
            var kapaliOppEfektifF = await GetKapaliOppEfektifMapCachedAsync(IsForceRefresh());
            var kapaliSetFunnel = kapaliOppEfektifF
                .Where(kv => kv.Value.HasValue && (kv.Value.Value < start || kv.Value.Value > end))
                .Select(kv => kv.Key).ToHashSet();

            // Tahakkukla bu döneme kayan fırsatlar
            var eklenecekSetFunnel = kapaliOppEfektifF
                .Where(kv => kv.Value.HasValue && kv.Value.Value >= start && kv.Value.Value <= end)
                .Select(kv => kv.Key).ToHashSet();
            var donemFirsatIdsRaw = await firsatQuery
                .Where(o => o.OpportunityStageName != "Lost"
                    && (o.OpportunityStageName == null || !o.OpportunityStageName.Contains("Closed")))
                .Select(o => o.Id).ToListAsync();
            var donemFirsatIds = donemFirsatIdsRaw.Where(id => !kapaliSetFunnel.Contains((id ?? "").ToLower())).ToList();
            // Ekle
            var donemCheckF = donemFirsatIds.Select(id => (id ?? "").ToLower()).ToHashSet();
            foreach (var ekId in eklenecekSetFunnel)
                if (!donemCheckF.Contains(ekId)) donemFirsatIds.Add(ekId);
            var donemGuidSet = donemFirsatIds.Where(id => Guid.TryParse(id, out _)).Select(id => Guid.Parse(id)).ToHashSet();

            // ── Exclusive pipeline setleri ──
            var donemFirsatIdSetLower = donemFirsatIds
                .Select(id => (id ?? "").ToLower())
                .Where(id => !string.IsNullOrEmpty(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var exclusiveSets = await GetExclusiveSetsAsync(start, end, null, donemFirsatIdSetLower);

            // Dönem teklifleri (fırsata bağlı + CreatedOn dönemde — Kart 3 referansıyla tutarlı)
            var donemTeklifler = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue && donemGuidSet.Contains(t.OpportunityId.Value)
                    && t.CreatedOn.HasValue && t.CreatedOn.Value >= start && t.CreatedOn.Value <= end)
                .Select(t => new { t.Id, t.ProposalOwnerId, t.TotalNetAmountLocalCurrency_Amount, t.Status, t.OpportunityId })
                .ToListAsync();
            var donemTeklifIdSet = donemTeklifler.Select(t => t.Id.ToString()).ToHashSet();

            // Dönem siparişleri (teklife bağlı) + ürün detayları
            // Tahakkuk override: SerialNumber bazlı, in-memory'de InvoiceDate efektif tarihe dönüştürülür
            var tahakkukMap = await _tahakkukService.GetTahakkukMapAsync();
            var donemSiparislerRaw = await ExcludeTestSiparis(db.TBL_VARUNA_SIPARIs.AsNoTracking())
                .Where(s => s.QuoteId != null && donemTeklifIdSet.Contains(s.QuoteId))
                .Select(s => new { s.OrderId, s.SerialNumber, s.SAPOutReferenceCode, s.AccountTitle, s.TotalNetAmount, s.OrderStatus, s.ProposalOwnerId, s.InvoiceDate })
                .ToListAsync();
            var donemSiparisler = donemSiparislerRaw.Select(s => new {
                s.OrderId,
                s.SerialNumber,
                s.AccountTitle,
                s.TotalNetAmount,
                s.OrderStatus,
                s.ProposalOwnerId,
                InvoiceDate = EfektifInvoice(s.SerialNumber, s.SAPOutReferenceCode, s.InvoiceDate, tahakkukMap)
            }).ToList();

            var orderIds = donemSiparisler.Select(s => s.OrderId).Where(o => o != null).Distinct().ToList();
            var siparisUrunleri = await db.TBL_VARUNA_SIPARIS_URUNLERIs.AsNoTracking()
                .Where(u => u.CrmOrderId != null && orderIds.Contains(u.CrmOrderId))
                .Select(u => new { u.CrmOrderId, u.StockCode, u.ProductName, Total = u.Total ?? 0m })
                .ToListAsync();

            // Shared base for funnel 4 (customer breakdown paylaşır)
            List<TBL_VARUNA_SIPARI>? funnel45Base = null;
            // Shared: funnel 5 customer breakdown (SP bazlı, önceden hesaplanır)
            object? funnel5CustomerBreakdown = null;
            // Shared: funnel ≤ 2'de base fırsat seti (ownerBreakdown + customerBreakdown paylaşır)
            HashSet<string>? efektifFirsatIdSet = null;
            Dictionary<string, decimal>? sharedFirsatAmountMap = null;

            // Owner dağılımı: funnel'a göre
            object ownerBreakdown;
            object firsatSahipleriBreakdown = new List<object>();
            object productBreakdown;
            object customerBreakdown = new List<object>();

            if (funnel <= 4 || funnel == 34)
            {
                // ── Referansla tutarlı base set oluştur ──
                // Funnel 1: Tüm fırsatlar (açık havuz)
                // Funnel 2-4: Exclusive pipeline setleri (fırsat bazlı breakdown)
                // Funnel 34: kümülatif teklif (3 ∪ 4)
                var excludeStages = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Lost", "Won" };

                IQueryable<TBL_VARUNA_OPPORTUNITIES> baseQuery;
                if (funnel == 1)
                {
                    // Tüm fırsatlar açık havuzu — dönem kısıtı yok, Won+Lost+Closed hariç
                    baseQuery = ExcludeTestFirsat(db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking())
                        .Where(o => !excludeStages.Contains(o.OpportunityStageName ?? "")
                            && (o.OpportunityStageName == null || !o.OpportunityStageName.Contains("Closed")));
                }
                else
                {
                    // Funnel 2/3/4: Her biri kendi exclusive setini kullanır
                    // Funnel 34: kümülatif teklif (Beklemede ∪ Kabul edildi)
                    var aktifIds = funnel == 2 ? exclusiveSets.ExFirsatIds
                                 : funnel == 3 ? exclusiveSets.ExTeklifIds
                                 : funnel == 34 ? new HashSet<string>(exclusiveSets.ExTeklifIds.Concat(exclusiveSets.ExSiparisIds), StringComparer.OrdinalIgnoreCase)
                                 : exclusiveSets.ExSiparisIds;
                    baseQuery = ExcludeTestFirsat(db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking())
                        .Where(o => aktifIds.Contains(o.Id));
                }

                // kapaliSet filtresi (tahakkuk-aware) — sadece funnel=1'de gerekli, 2-4 exclusive set zaten filtrelenmiş
                if (funnel == 1)
                    baseQuery = baseQuery.Where(o => !kapaliSetFunnel.Contains((o.Id ?? "").ToLower()));

                // ── Alt-katman filtreleri: baseQuery üzerinde uygulanır (breakdown havuzuyla birebir tutarlı)
                if (!string.IsNullOrEmpty(customer))
                {
                    var accTitleMapF = await GetAccountTitleMapAsync(db);
                    if (customer == "Tanımsız Müşteri")
                    {
                        var knownAccIdsF = accTitleMapF.Keys.ToList();
                        baseQuery = baseQuery.Where(o =>
                            (o.AccountTitle == null || o.AccountTitle == "")
                            && (o.AccountId == null || !knownAccIdsF.Contains(o.AccountId.ToLower())));
                    }
                    else
                    {
                        var matchAccIdsF = accTitleMapF.Where(kv => kv.Value == customer).Select(kv => kv.Key).ToList();
                        baseQuery = baseQuery.Where(o =>
                            o.AccountTitle == customer
                            || (o.AccountId != null && matchAccIdsF.Contains(o.AccountId.ToLower())));
                    }
                }
                if (!string.IsNullOrEmpty(product))
                {
                    if (product == "Tanımsız" || product == "Diğer")
                    {
                        baseQuery = baseQuery.Where(o => o.ProductGroupId == null);
                    }
                    else
                    {
                        var prodIdSet2 = await ResolveOppIdsByProductGroupAsync(db, product);
                        baseQuery = baseQuery.Where(o => o.Id != null && prodIdSet2.Contains(o.Id.ToLower()));
                    }
                }
                if (!string.IsNullOrEmpty(firsatSahibi))
                {
                    var fsPidsF = await db.TBL_VARUNA_PERSONs.AsNoTracking()
                        .Where(p => p.PersonNameSurname == firsatSahibi && p.DeletedOn == null)
                        .Select(p => p.Id).ToListAsync();
                    var fsPidListF = fsPidsF.Select(id => id.ToLower()).ToList();
                    baseQuery = baseQuery.Where(o => o.OwnerId != null && fsPidListF.Contains(o.OwnerId.ToLower()));
                }

                var firsatOwnerData = await baseQuery
                    .Select(o => new { o.Id, OwnerId = o.OwnerId ?? "unknown", o.AccountId, o.AccountTitle, o.AmountAmount, o.OpportunityStageName, o.ProductGroupId, o.CustomerRepresentativeId })
                    .ToListAsync();

                // ── Satış Temsilcisi filtresi (ownerName) — 3 kademeli fallback:
                //    1) TBL_VARUNA_ACCOUNT_REPRESENTATIVES (State=Active)
                //    2) Fırsat.CustomerRepresentativeId
                //    3) Fırsat.OwnerId
                if (!string.IsNullOrEmpty(ownerName))
                {
                    var personMapF = await GetPersonMapAsync(db);
                    var accRepMapF = await GetAccountToRepMapAsync(db);
                    firsatOwnerData = firsatOwnerData
                        .Where(d => ResolveSalesRepName(d.AccountId, d.CustomerRepresentativeId, d.OwnerId,
                                                        accRepMapF, personMapF, ownerMap) == ownerName)
                        .ToList();
                }

                // Efektif sahip: teklif sahibi varsa o, yoksa fırsat sahibi
                var brkTeklifOwners = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                    .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue && t.ProposalOwnerId.HasValue)
                    .Select(t => new { OppId = t.OpportunityId!.Value.ToString().ToLower(), t.ProposalOwnerId })
                    .ToListAsync();
                var brkOppToOwner = brkTeklifOwners
                    .GroupBy(t => t.OppId)
                    .ToDictionary(g => g.Key, g => g.First().ProposalOwnerId!.Value.ToString().ToLower());

                // Her fırsata EfektifOwner ata
                var firsatWithOwner = firsatOwnerData
                    .Select(d => new {
                        d.Id,
                        d.OwnerId,
                        d.AccountId,
                        d.AccountTitle,
                        d.CustomerRepresentativeId,
                        EfektifOwner = brkOppToOwner.TryGetValue((d.Id ?? "").ToLower(), out var po) ? po : d.OwnerId!,
                        d.AmountAmount,
                        d.ProductGroupId
                    }).ToList();

                // NOT: ownerName filtresi firsatOwnerData seviyesinde (efektif ad semantiği) yukarıda uygulandı.
                // Eski EfektifOwner bazlı daraltma breakdown ile çeliştiği için kaldırıldı.
                if (ownerPidSet != null)
                    efektifFirsatIdSet = firsatWithOwner.Select(d => d.Id).Where(id => id != null).ToHashSet()!;

                // ── Tek base set, üç farklı gruplama ──

                // Person map: Id → PersonNameSurname (TBL_VARUNA_PERSON)
                var personMapAll = await GetPersonMapAsync(db);

                // Satış temsilcisi map: kanonik kaynak ACCOUNT_REPRESENTATIVES (State=Active)
                var accountToRepMap = await GetAccountToRepMapAsync(db);

                // ── TEK HAVUZ, 4 FARKLI BOYUT — her kart havuzun tamamını gösterir, dip toplamlar eşit ──

                // ── A. SATIŞ TEMSİLCİSİ: 3 kademeli fallback (C bloğu sonrası filtre-aware versiyonu çağrılıyor) ──
                // Bu blok C bloğunun (oppGroupBreakdown) altına taşındı; aşağıda kuruluyor.

                // ── B. FIRSAT SAHİPLERİ: tüm havuz (C bloğu sonrası filtre-aware versiyonu çağrılıyor) ──

                // ── C. ÜRÜN BAZLI: aşama-bazlı tek kaynak (mutually exclusive) ──
                //   1) Sipariş varsa: SADECE sipariş kalemleri → TBLSOS_URUN_ESLESTIRME → ANA_URUN.Ad
                //   2) Teklif varsa : SADECE teklif kalemleri  → TBLSOS_URUN_ESLESTIRME → ANA_URUN.Ad
                //   3) Yetim       : ProductGroupId → PRODUCTGRUPS 1 seviye parent (CallDesk → ServiceCore)
                //   NULL/eşleşmez   : "Tanımsız"

                // 1) StockCode → ana ürün adı
                var stockEslestirmeMap = await GetUrunEslestirmeMapAsync();

                // 2) PRODUCTGRUPS: Id → 1 seviye parent.Name (parent yoksa kendi adı)
                var pgRows = await db.Database.SqlQueryRaw<ProductGroupParentDto>(
                    @"SELECT CAST(g.Id AS NVARCHAR(64)) AS Id, g.Name AS Name,
                             p.Name AS ParentName
                      FROM TBL_VARUNA_PRODUCTGRUPS g
                      LEFT JOIN TBL_VARUNA_PRODUCTGRUPS p
                        ON CAST(p.Id AS NVARCHAR(64)) = g.ParentGroupId
                      WHERE g.DeletedOn IS NULL").ToListAsync();
                var pgParentMap = pgRows
                    .Where(r => !string.IsNullOrEmpty(r.Id))
                    .GroupBy(r => r.Id!)
                    .ToDictionary(
                        g => g.Key,
                        g => g.First().ParentName ?? g.First().Name ?? "Tanımsız",
                        StringComparer.OrdinalIgnoreCase);

                // 3) Fırsat ↔ Teklif haritası (anahtar: lowercase string Guid)
                var firsatIdSet = firsatWithOwner
                    .Select(d => (d.Id ?? string.Empty).ToLowerInvariant())
                    .Where(s => s.Length > 0)
                    .ToHashSet();
                var teklifMap = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                    .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue)
                    .Select(t => new { TeklifGuid = t.Id, OppGuid = t.OpportunityId!.Value })
                    .ToListAsync();
                var teklifFiltered = teklifMap
                    .Select(x => new
                    {
                        TeklifKey = x.TeklifGuid.ToString().ToLowerInvariant(),
                        OppKey = x.OppGuid.ToString().ToLowerInvariant()
                    })
                    .Where(x => firsatIdSet.Contains(x.OppKey))
                    .ToList();
                var teklifByOpp = teklifFiltered
                    .GroupBy(x => x.OppKey)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.TeklifKey).ToList());
                var teklifIdToOpp = teklifFiltered
                    .GroupBy(x => x.TeklifKey)
                    .ToDictionary(g => g.Key, g => g.First().OppKey);
                var allTeklifKeys = teklifIdToOpp.Keys.ToHashSet();

                // 4) Sipariş → Teklif (QuoteId, string olarak gelir) zinciri
                var siparisRows = await ExcludeTestSiparis(db.TBL_VARUNA_SIPARIs.AsNoTracking())
                    .Where(s => s.QuoteId != null && s.OrderId != null
                        && s.TotalNetAmount.HasValue && s.TotalNetAmount > 0
                        && s.OrderStatus == "Closed")
                    .Select(s => new { s.OrderId, s.QuoteId, s.TotalNetAmount })
                    .ToListAsync();
                var siparislerByOpp = new Dictionary<string, List<(string OrderId, decimal Tutar)>>();
                foreach (var s in siparisRows)
                {
                    var qKey = s.QuoteId!.ToLowerInvariant();
                    if (!teklifIdToOpp.TryGetValue(qKey, out var oppKey)) continue;
                    if (!siparislerByOpp.TryGetValue(oppKey, out var list))
                        siparislerByOpp[oppKey] = list = new List<(string, decimal)>();
                    list.Add((s.OrderId!, s.TotalNetAmount ?? 0m));
                }
                var oppHasSiparis = siparislerByOpp.Keys.ToHashSet();

                // 5) Sipariş kalemleri (CrmOrderId → kalemler)
                var siparisOrderIds = siparisRows
                    .Where(s => s.OrderId != null)
                    .Select(s => s.OrderId!)
                    .ToHashSet();
                var sipKalemByOrder = new Dictionary<string, List<(string StockCode, decimal Total)>>();
                if (siparisOrderIds.Count > 0)
                {
                    var sipKalemler = await db.TBL_VARUNA_SIPARIS_URUNLERIs.AsNoTracking()
                        .Where(u => u.CrmOrderId != null && u.StockCode != null
                            && siparisOrderIds.Contains(u.CrmOrderId))
                        .Select(u => new { u.CrmOrderId, u.StockCode, Total = u.Total ?? 0m })
                        .ToListAsync();
                    sipKalemByOrder = sipKalemler
                        .GroupBy(u => u.CrmOrderId!)
                        .ToDictionary(
                            g => g.Key,
                            g => g.Select(u => (u.StockCode!, u.Total)).ToList());
                }

                // 6) Teklif kalemleri (QuoteId Guid → kalemler), anahtar lowercase string
                var teklifKalemByQuote = new Dictionary<string, List<(string StockCode, decimal Tutar)>>();
                if (allTeklifKeys.Count > 0)
                {
                    var teklifKalemler = await db.TBL_VARUNA_TEKLIF_URUNLERIs.AsNoTracking()
                        .Where(u => u.DeletedOn == null && u.QuoteId.HasValue && u.StockCode != null)
                        .Select(u => new
                        {
                            QuoteGuid = u.QuoteId!.Value,
                            u.StockCode,
                            Tutar = u.NetLineTotalAmountLocal_Amount ?? 0m
                        })
                        .ToListAsync();
                    teklifKalemByQuote = teklifKalemler
                        .Select(u => new
                        {
                            QuoteKey = u.QuoteGuid.ToString().ToLowerInvariant(),
                            u.StockCode,
                            u.Tutar
                        })
                        .Where(u => allTeklifKeys.Contains(u.QuoteKey))
                        .GroupBy(u => u.QuoteKey)
                        .ToDictionary(
                            g => g.Key,
                            g => g.Select(u => (u.StockCode!, u.Tutar)).ToList());
                }
                var oppHasTeklifKalem = teklifKalemByQuote.Keys
                    .Where(tKey => teklifIdToOpp.ContainsKey(tKey))
                    .Select(tKey => teklifIdToOpp[tKey])
                    .ToHashSet();

                // 7) Resolver: her fırsat için ürün payını hesapla
                // ÖNEMLİ semantik:
                //   - Kalem yolu bucket'ı f.AmountAmount'a normalize edilir (kalem oranı KORUNUR ama toplam = fırsat tutarı)
                //   - Yetim yolu: tüm AmountAmount tek gruba
                //   - Bu sayede ∑ürün = ∑müşteri = ∑owner (BUG-2 fix)
                //   - Filtre uygulandığında müşteri/owner/sahip de bu dict'ten o ürünün payını okur (BUG-3 fix)
                var oppGroupBreakdown = new Dictionary<string, Dictionary<string, decimal>>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in firsatWithOwner)
                {
                    var oppKey = (f.Id ?? string.Empty).ToLowerInvariant();
                    if (oppKey.Length == 0) continue;
                    var amount = f.AmountAmount ?? 0m;
                    var bucket = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

                    // 7a) Sipariş yolu
                    var siparisMatched = false;
                    if (oppHasSiparis.Contains(oppKey)
                        && siparislerByOpp.TryGetValue(oppKey, out var sipList))
                    {
                        foreach (var s in sipList)
                        {
                            if (!sipKalemByOrder.TryGetValue(s.OrderId, out var kalemler)) continue;
                            var toplamDoviz = kalemler.Sum(k => k.Total);
                            if (toplamDoviz == 0) continue;
                            foreach (var k in kalemler)
                            {
                                if (!stockEslestirmeMap.TryGetValue(k.StockCode, out var grup)) continue;
                                var tl = (k.Total / toplamDoviz) * s.Tutar;
                                bucket[grup] = bucket.GetValueOrDefault(grup) + tl;
                            }
                        }
                        if (bucket.Count > 0) siparisMatched = true;
                    }

                    // 7b) Teklif yolu (sipariş yoksa)
                    var teklifMatched = false;
                    if (!siparisMatched && oppHasTeklifKalem.Contains(oppKey)
                        && teklifByOpp.TryGetValue(oppKey, out var teklifIds))
                    {
                        foreach (var tId in teklifIds)
                        {
                            if (!teklifKalemByQuote.TryGetValue(tId, out var kalemler)) continue;
                            foreach (var k in kalemler)
                            {
                                if (!stockEslestirmeMap.TryGetValue(k.StockCode, out var grup)) continue;
                                bucket[grup] = bucket.GetValueOrDefault(grup) + k.Tutar;
                            }
                        }
                        if (bucket.Count > 0) teklifMatched = true;
                    }

                    if (siparisMatched || teklifMatched)
                    {
                        // Bucket'ı f.AmountAmount'a normalize et (ürün kartı toplamı = müşteri kartı toplamı)
                        var totalBucket = bucket.Values.Sum();
                        if (totalBucket > 0 && amount > 0)
                        {
                            var scale = amount / totalBucket;
                            foreach (var grup in bucket.Keys.ToList())
                                bucket[grup] = bucket[grup] * scale;
                        }
                        else if (totalBucket == 0)
                        {
                            // Kalem tutarı sıfır → tüm AmountAmount fall-through'a gitsin
                            bucket.Clear();
                        }
                    }

                    if (bucket.Count == 0)
                    {
                        // 7c) Yetim ya da kalem eşleşmedi → ProductGroupId 1 seviye parent
                        string grupAdi;
                        if (string.IsNullOrEmpty(f.ProductGroupId))
                            grupAdi = "Tanımsız";
                        else if (pgParentMap.TryGetValue(f.ProductGroupId, out var pName) && !string.IsNullOrEmpty(pName))
                            grupAdi = FirsatGrupAnaUrunMap.TryGetValue(pName, out var mapped) ? mapped : pName;
                        else
                            grupAdi = "Tanımsız";

                        bucket[grupAdi] = amount;
                    }

                    oppGroupBreakdown[oppKey] = bucket;
                }

                // ÜRÜN BAZLI: oppGroupBreakdown'tan grup-toplam üret
                var prodAccum = new Dictionary<string, (decimal tutar, int adet)>(StringComparer.OrdinalIgnoreCase);
                foreach (var (oppKey, bucket) in oppGroupBreakdown)
                {
                    foreach (var (grup, tl) in bucket)
                    {
                        // Ürün filtresi varsa SADECE seçili gruba ekle (BUG-3)
                        if (!string.IsNullOrEmpty(product) && !grup.Equals(product, StringComparison.OrdinalIgnoreCase)) continue;
                        var cur = prodAccum.GetValueOrDefault(grup);
                        prodAccum[grup] = (cur.tutar + tl, cur.adet + 1);
                    }
                }

                var prodGrouped = prodAccum
                    .Select(kv => new { urun = kv.Key, tutar = kv.Value.tutar, adet = kv.Value.adet })
                    .OrderByDescending(x => x.tutar)
                    .ToList();
                productBreakdown = BuildTopWithDigerProduct(prodGrouped, 10);

                // Müşteri/owner/sahip filtre varsa bu pay dict'inden saysın (yoksa AmountAmount)
                // Yardımcı: belirli bir fırsat için "kullanılacak tutar" — filtre varsa o ürünün payı, yoksa AmountAmount
                decimal GetEffectiveAmount(string? oppId, decimal? amountFallback)
                {
                    if (string.IsNullOrEmpty(product))
                        return amountFallback ?? 0m;
                    var key = (oppId ?? string.Empty).ToLowerInvariant();
                    if (oppGroupBreakdown.TryGetValue(key, out var b) && b.TryGetValue(product, out var v))
                        return v;
                    return 0m;
                }

                // ── D. MÜŞTERİ BAZLI: tüm havuz — AccountId → TBL_VARUNA_ACCOUNTS.Title/Name ──
                // DB'de gerçek müşteri tablosu: TBL_VARUNA_ACCOUNTS (Id, Name, SurName, Title)
                var accountIdToTitle = await GetAccountTitleMapAsync(db);

                // ── A. SATIŞ TEMSİLCİSİ ──
                var salesGrouped = firsatWithOwner
                    .Select(d => new {
                        RepName = ResolveSalesRepName(d.AccountId, d.CustomerRepresentativeId, d.OwnerId,
                                                      accountToRepMap, personMapAll, ownerMap),
                        Tutar = GetEffectiveAmount(d.Id, d.AmountAmount)
                    })
                    .Where(x => string.IsNullOrEmpty(product) || x.Tutar > 0)
                    .GroupBy(d => d.RepName)
                    .Select(g => new { adSoyad = g.Key, tutar = g.Sum(d => d.Tutar), adet = g.Count() })
                    .OrderByDescending(x => x.tutar)
                    .ToList();
                ownerBreakdown = BuildTopWithDigerOwner(salesGrouped, 10);

                // ── B. FIRSAT SAHİPLERİ ──
                var fsGrouped = firsatWithOwner
                    .Select(d => new {
                        d.OwnerId,
                        Tutar = GetEffectiveAmount(d.Id, d.AmountAmount)
                    })
                    .Where(x => string.IsNullOrEmpty(product) || x.Tutar > 0)
                    .GroupBy(d => d.OwnerId!)
                    .Select(g => {
                        var name = personMapAll.TryGetValue(g.Key.ToLower(), out var n) ? n : ResolveOwnerName(g.Key, ownerMap);
                        return new { adSoyad = name, tutar = g.Sum(d => d.Tutar), adet = g.Count() };
                    })
                    .OrderByDescending(x => x.tutar)
                    .ToList();
                firsatSahipleriBreakdown = BuildTopWithDigerOwner(fsGrouped, 10);

                // ── D. MÜŞTERİ BAZLI ──
                var custGrouped = firsatWithOwner
                    .Select(d => {
                        string musteri;
                        if (!string.IsNullOrWhiteSpace(d.AccountTitle))
                            musteri = d.AccountTitle!.Trim();
                        else if (d.AccountId != null && accountIdToTitle.TryGetValue(d.AccountId.ToLower(), out var t))
                            musteri = t;
                        else
                            musteri = "Tanımsız Müşteri";
                        return new { Musteri = musteri, Tutar = GetEffectiveAmount(d.Id, d.AmountAmount) };
                    })
                    .Where(x => string.IsNullOrEmpty(product) || x.Tutar > 0)
                    .GroupBy(x => x.Musteri)
                    .Select(g => new { musteri = g.Key, tutar = g.Sum(x => x.Tutar), adet = g.Count() })
                    .OrderByDescending(x => x.tutar)
                    .ToList();
                customerBreakdown = BuildTopWithDigerCustomer(custGrouped, 10);

                // Paylaş: diğer yerler de kullanabilir
                sharedFirsatAmountMap = firsatWithOwner.ToDictionary(f => (f.Id ?? "").ToLower(), f => f.AmountAmount ?? 0m);
                if (efektifFirsatIdSet == null)
                    efektifFirsatIdSet = firsatWithOwner.Select(d => d.Id).Where(id => id != null).ToHashSet()!;
            }
            else
            {
                // Funnel 5 (Fatura) — SP verisi base (Kart 5 referansıyla tutarlı)
                var spFaturalar = await _cockpitData.GetFaturalarAsync(start, end);
                var spFaturaNoSet = spFaturalar.Select(f => f.FaturaNo).ToHashSet();
                var spTutarMap = spFaturalar.ToDictionary(f => f.FaturaNo, f => f.NetTutar);

                // SP'nin döndürdüğü FaturaNo iki formattan biri olabilir:
                //   1) Gerçek SerialNumber (VIEW_CP_EXCEL_FATURA üzerinden gelen)
                //   2) "SAP:<SAPOutReferenceCode>" (Varuna Closed sipariş, sentetik fatura)
                // Sipariş tarafında sentetikleri yakalamak için iki ayrı set kuruyoruz.
                var directSerialSet = spFaturaNoSet
                    .Where(f => !string.IsNullOrEmpty(f) && !f.StartsWith("SAP:", StringComparison.OrdinalIgnoreCase))
                    .ToHashSet();
                var sapRefSet = spFaturaNoSet
                    .Where(f => !string.IsNullOrEmpty(f) && f.StartsWith("SAP:", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.Substring(4))
                    .ToHashSet();

                // SP fatura → sipariş eşleşmesi: SerialNumber doğrudan, ya da SAPOutReferenceCode → "SAP:<ref>"
                // AccountId + QuoteId: 4-step fallback için gerekli
                var spSiparislerRaw = await ExcludeTestSiparis(db.TBL_VARUNA_SIPARIs.AsNoTracking())
                    .Where(s =>
                        (s.SerialNumber != null && directSerialSet.Contains(s.SerialNumber))
                        || (s.SAPOutReferenceCode != null && sapRefSet.Contains(s.SAPOutReferenceCode)))
                    .Select(s => new { s.OrderId, s.SerialNumber, s.SAPOutReferenceCode, s.AccountTitle, s.AccountId, s.QuoteId, s.ProposalOwnerId })
                    .ToListAsync();

                // Her sipariş için fatura tutar map'ine ulaşılacak anahtarı (MapKey) hesapla.
                // Bundan sonraki tüm grup hesaplamaları SerialNumber yerine MapKey üzerinden yürür.
                var spSiparisler = spSiparislerRaw.Select(s => new
                {
                    s.OrderId,
                    SerialNumber = (s.SerialNumber != null && directSerialSet.Contains(s.SerialNumber))
                        ? s.SerialNumber
                        : (s.SAPOutReferenceCode != null && sapRefSet.Contains(s.SAPOutReferenceCode))
                            ? "SAP:" + s.SAPOutReferenceCode
                            : null,
                    s.AccountTitle,
                    s.AccountId,
                    s.QuoteId,
                    s.ProposalOwnerId
                })
                .Where(s => s.SerialNumber != null)
                .ToList();

                // Müşteri filtresi (ownerName filtresi 4-step resolve sonrası uygulanır — aşağıda)
                if (!string.IsNullOrEmpty(customer))
                    spSiparisler = spSiparisler.Where(s => s.AccountTitle == customer).ToList();

                // ── Sipariş → Teklif → Fırsat zinciri (OwnerId + CustomerRepresentativeId fallback için) ──
                var spQuoteIds = spSiparisler.Select(s => s.QuoteId).Where(q => q != null).Distinct().ToList();
                var teklifToOppMap = await db.TBL_VARUNA_TEKLIFs.AsNoTracking()
                    .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue)
                    .Select(t => new { TeklifId = t.Id.ToString(), OppId = t.OpportunityId!.Value.ToString() })
                    .ToListAsync();
                var teklifOppDict = teklifToOppMap
                    .GroupBy(t => t.TeklifId.ToLower())
                    .ToDictionary(g => g.Key, g => g.First().OppId.ToLower());
                // Fırsat → (OwnerId, CustomerRepresentativeId)
                var spOppIds = spSiparisler
                    .Select(s => s.QuoteId != null && teklifOppDict.TryGetValue(s.QuoteId.ToLower(), out var o) ? o : null)
                    .Where(o => o != null).Distinct().ToList();
                var firsatOwnerLookup = await db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking()
                    .Where(o => spOppIds.Contains(o.Id))
                    .Select(o => new { o.Id, o.OwnerId, o.CustomerRepresentativeId })
                    .ToListAsync();
                var firsatOwnerDict = firsatOwnerLookup
                    .GroupBy(f => f.Id.ToLower())
                    .ToDictionary(g => g.Key, g => g.First());

                // 4-step map'ler
                var personMapFunnel5 = await GetPersonMapAsync(db);
                var accountRepMapFunnel5 = await GetAccountToRepMapAsync(db);

                // Her sipariş için sahip/temsilci ikilisini çözümle
                // EfektifOwnerId (Fırsat Sahipleri kartı): fırsat.OwnerId → yoksa sipariş.ProposalOwnerId
                // SatisRepName (Satış Temsilcisi kartı): 3-step fallback (AccountRep → fırsat.CustRep → fırsat.Owner)
                var enrichedSiparisler = spSiparisler.Select(s => {
                    var oppId = s.QuoteId != null && teklifOppDict.TryGetValue(s.QuoteId.ToLower(), out var o) ? o : null;
                    var firsat = oppId != null && firsatOwnerDict.TryGetValue(oppId, out var f) ? f : null;
                    var efektifOwnerId = firsat?.OwnerId ?? s.ProposalOwnerId;
                    var satisRepName = ResolveSalesRepName(
                        s.AccountId,
                        firsat?.CustomerRepresentativeId,
                        efektifOwnerId,
                        accountRepMapFunnel5, personMapFunnel5, ownerMap);
                    var efektifOwnerName = efektifOwnerId != null
                        ? (personMapFunnel5.TryGetValue(efektifOwnerId.ToLower(), out var pn) ? pn : ResolveOwnerName(efektifOwnerId, ownerMap))
                        : "Bilinmiyor";
                    return new {
                        s.OrderId, s.SerialNumber, s.AccountTitle,
                        SatisRep = satisRepName,
                        FirsatSahibi = efektifOwnerName,
                        Tutar = spTutarMap.GetValueOrDefault(s.SerialNumber!, 0m)
                    };
                })
                .Where(x => x.SerialNumber != null)
                .ToList();

                // Satış Temsilcisi filtresi (ownerName) — 4-step resolve edilmiş isme göre
                if (!string.IsNullOrEmpty(ownerName))
                    enrichedSiparisler = enrichedSiparisler.Where(x => x.SatisRep == ownerName).ToList();
                // Fırsat Sahibi filtresi (firsatSahibi)
                if (!string.IsNullOrEmpty(firsatSahibi))
                    enrichedSiparisler = enrichedSiparisler.Where(x => x.FirsatSahibi == firsatSahibi).ToList();
                // Ürün filtresi — owner/sahip/müşteri kartları seçili ürünün KALEM PAYI'na daraltılsın (BUG-1, BUG-4)
                if (!string.IsNullOrEmpty(product))
                {
                    var spOrderIdsForProductFilter = spSiparisler
                        .Select(s => s.OrderId)
                        .Where(o => o != null)
                        .Cast<string>()
                        .ToHashSet();
                    var spRawUrunleriForFilter = await db.TBL_VARUNA_SIPARIS_URUNLERIs.AsNoTracking()
                        .Where(u => u.CrmOrderId != null && spOrderIdsForProductFilter.Contains(u.CrmOrderId))
                        .Select(u => new { u.CrmOrderId, u.StockCode, Total = u.Total ?? 0m })
                        .ToListAsync();
                    // Sipariş başına: toplam döviz / seçili ürün döviz
                    var orderDovizToplam = spRawUrunleriForFilter
                        .GroupBy(u => u.CrmOrderId!)
                        .ToDictionary(g => g.Key, g => g.Sum(u => u.Total));
                    var orderProductRatio = spRawUrunleriForFilter
                        .Where(u => ResolveProductGroup(u.StockCode, eslestirmeMap) == product)
                        .GroupBy(u => u.CrmOrderId!)
                        .ToDictionary(g => g.Key, g =>
                        {
                            var dt = orderDovizToplam.GetValueOrDefault(g.Key, 0m);
                            if (dt == 0) return 0m;
                            return g.Sum(u => u.Total) / dt;
                        });
                    var productOrderIds = orderProductRatio.Keys.ToHashSet();

                    // enrichedSiparisler'i daralt + Tutar'ı kalem payına revize et
                    enrichedSiparisler = enrichedSiparisler
                        .Where(x => x.OrderId != null && productOrderIds.Contains(x.OrderId))
                        .Select(x => new
                        {
                            x.OrderId,
                            x.SerialNumber,
                            x.AccountTitle,
                            x.SatisRep,
                            x.FirsatSahibi,
                            Tutar = (orderProductRatio.TryGetValue(x.OrderId!, out var r) ? r : 0m)
                                  * spTutarMap.GetValueOrDefault(x.SerialNumber!, 0m)
                        })
                        .ToList();
                }
                // Ürün/müşteri breakdown'u için spSiparisler'i de enriched set ile senkronize et
                if (!string.IsNullOrEmpty(ownerName) || !string.IsNullOrEmpty(firsatSahibi) || !string.IsNullOrEmpty(product))
                {
                    var keepSerials = enrichedSiparisler.Select(x => x.SerialNumber!).ToHashSet();
                    spSiparisler = spSiparisler.Where(s => s.SerialNumber != null && keepSerials.Contains(s.SerialNumber)).ToList();
                }

                // Satış Temsilcisi Bazlı (ownerBreakdown) — "Bilinmiyor" / boş → "Atanmamış".
                var spSalesGrouped = enrichedSiparisler
                    .GroupBy(x => string.IsNullOrWhiteSpace(x.SatisRep) || x.SatisRep.StartsWith("Bilinmiyor", StringComparison.OrdinalIgnoreCase) ? "Atanmamış" : x.SatisRep)
                    .Select(g => new { adSoyad = g.Key, tutar = g.Sum(x => x.Tutar), adet = g.Count() })
                    .OrderByDescending(x => x.tutar)
                    .ToList();
                ownerBreakdown = BuildTopWithDigerOwner(spSalesGrouped, 10);

                // Fırsat Sahipleri Bazlı (firsatSahipleriBreakdown) — "Bilinmiyor"/boş → "Sahipsiz".
                var spFsGrouped = enrichedSiparisler
                    .GroupBy(x => string.IsNullOrWhiteSpace(x.FirsatSahibi) || x.FirsatSahibi.StartsWith("Bilinmiyor", StringComparison.OrdinalIgnoreCase) ? "Sahipsiz" : x.FirsatSahibi)
                    .Select(g => new { adSoyad = g.Key, tutar = g.Sum(x => x.Tutar), adet = g.Count() })
                    .OrderByDescending(x => x.tutar)
                    .ToList();
                firsatSahipleriBreakdown = BuildTopWithDigerOwner(spFsGrouped, 10);

                // Ürün: oransal TL dağıtımı (SP tutarı base)
                var spOrderIds = spSiparisler.Select(s => s.OrderId).Where(o => o != null).Distinct().ToList();
                var spSipTutarMap = spSiparisler.Where(s => s.SerialNumber != null)
                    .ToDictionary(s => s.OrderId ?? "", s => spTutarMap.GetValueOrDefault(s.SerialNumber!, 0m));
                var spRawUrunleri = await db.TBL_VARUNA_SIPARIS_URUNLERIs.AsNoTracking()
                    .Where(u => u.CrmOrderId != null && spOrderIds.Contains(u.CrmOrderId))
                    .Select(u => new { u.CrmOrderId, u.StockCode, Total = u.Total ?? 0m })
                    .ToListAsync();
                var spOrderDovizToplam = spRawUrunleri.GroupBy(u => u.CrmOrderId!)
                    .ToDictionary(g => g.Key, g => g.Sum(u => u.Total));
                var spUrunTlList = spRawUrunleri.Select(u => new {
                    urun = ResolveProductGroup(u.StockCode, eslestirmeMap),
                    tlTutar = spOrderDovizToplam.TryGetValue(u.CrmOrderId!, out var dt) && dt > 0
                        ? (u.Total / dt) * spSipTutarMap.GetValueOrDefault(u.CrmOrderId!, 0m)
                        : 0m
                }).ToList();

                if (!string.IsNullOrEmpty(product))
                {
                    spSiparisler = spSiparisler.Where(s => {
                        var oids = spRawUrunleri.Where(u => ResolveProductGroup(u.StockCode, eslestirmeMap) == product)
                            .Select(u => u.CrmOrderId).ToHashSet();
                        return oids.Contains(s.OrderId);
                    }).ToList();
                    spUrunTlList = spUrunTlList.Where(u => u.urun == product).ToList();
                }

                var spProdGrouped = spUrunTlList
                    .GroupBy(x => x.urun)
                    .Select(g => new { urun = g.Key, tutar = g.Sum(x => x.tlTutar), adet = g.Count() })
                    .OrderByDescending(x => x.tutar).ToList();
                productBreakdown = BuildTopWithDigerProduct(spProdGrouped, 10);

                // Customer: SP fatura tutarı ile — üst kartla birebir eşleşmesi için.
                // AccountTitle null olan kayıtlar "Tanımsız Müşteri" altında tutulur, top 10 + Diğer (detaylı).
                // ÖNEMLİ: Aynı SerialNumber (Fatura_No) birden fazla sipariş satırında görünebilir
                //         (ör. tek faturaya bağlı çoklu ürün). Müşteri bazlı sayım için
                //         SerialNumber + AccountTitle birleşiminde DEDUPE şart — aksi halde
                //         tutar ve adet **çoklanır** (örn. 80K → 160K, 1 fatura → 2 adet).
                funnel45Base = null; // Funnel 5 kendi customerBreakdown'ını üretecek
                List<object> funnel5CustomerList;
                if (!string.IsNullOrEmpty(product))
                {
                    // Filtre durumunda müşteri tutarı = kalem payı (enrichedSiparisler.Tutar zaten kalem payına revize edildi)
                    var spCustGroupedFiltered = enrichedSiparisler
                        .GroupBy(x => string.IsNullOrWhiteSpace(x.AccountTitle) ? "Tanımsız Müşteri" : x.AccountTitle!.Trim())
                        .Select(g => new
                        {
                            musteri = g.Key,
                            tutar = g.Sum(x => x.Tutar),
                            adet = g.Select(x => x.SerialNumber).Distinct().Count()
                        })
                        .OrderByDescending(x => x.tutar)
                        .ToList();
                    funnel5CustomerList = BuildTopWithDigerCustomer(spCustGroupedFiltered, 10);
                }
                else
                {
                    var spCustGrouped = spSiparisler
                        .Where(s => s.SerialNumber != null)
                        .GroupBy(s => new {
                            Musteri = string.IsNullOrWhiteSpace(s.AccountTitle) ? "Tanımsız Müşteri" : s.AccountTitle!,
                            s.SerialNumber
                        })
                        .Select(g => new { Musteri = g.Key.Musteri, SerialNumber = g.Key.SerialNumber! })
                        .GroupBy(x => x.Musteri)
                        .Select(g => new {
                            musteri = g.Key,
                            tutar = g.Sum(x => spTutarMap.GetValueOrDefault(x.SerialNumber, 0m)),
                            adet = g.Count()
                        })
                        .OrderByDescending(x => x.tutar)
                        .ToList();
                    funnel5CustomerList = BuildTopWithDigerCustomer(spCustGrouped, 10);
                }
                funnel5CustomerBreakdown = funnel5CustomerList;
            }

            // Müşteri verisi — funnel==5 için SP bazlı override (34 kümülatif teklif değil)
            if (funnel == 5)
                customerBreakdown = funnel5CustomerBreakdown ?? new List<object>();

            var funnelResult = new { ownerBreakdown, firsatSahipleriBreakdown, productBreakdown, customerBreakdown, funnel };
            _cache.Set(cacheKey, funnelResult, CacheTTL);
            return Json(funnelResult);
        }

        // ── Top N + expandable "Diğer" yardımcıları ──
        // "Diğer" satırı top N rest'in toplamı, ek olarak `detay` field'ında geriye kalan tüm itemlar.
        // Frontend Diğer satırını tıklanabilir yapar (default kapalı).

        private static List<object> BuildTopWithDigerCustomer<T>(List<T> grouped, int topN) where T : class
        {
            var top = grouped.Take(topN).Cast<object>().ToList();
            var rest = grouped.Skip(topN).ToList();
            if (rest.Count == 0) return top;

            decimal restTutar = 0;
            int restAdet = 0;
            foreach (dynamic r in rest) { restTutar += (decimal)r.tutar; restAdet += (int)r.adet; }
            top.Add(new { musteri = "Diğer", tutar = restTutar, adet = restAdet, detay = rest });
            return top;
        }

        private static List<object> BuildTopWithDigerOwner<T>(List<T> grouped, int topN) where T : class
        {
            var top = grouped.Take(topN).Cast<object>().ToList();
            var rest = grouped.Skip(topN).ToList();
            if (rest.Count == 0) return top;

            decimal restTutar = 0;
            int restAdet = 0;
            foreach (dynamic r in rest) { restTutar += (decimal)r.tutar; restAdet += (int)r.adet; }
            top.Add(new { adSoyad = "Diğer", tutar = restTutar, adet = restAdet, detay = rest });
            return top;
        }

        private static List<object> BuildTopWithDigerProduct<T>(List<T> grouped, int topN) where T : class
        {
            var top = grouped.Take(topN).Cast<object>().ToList();
            var rest = grouped.Skip(topN).ToList();
            if (rest.Count == 0) return top;

            decimal restTutar = 0;
            int restAdet = 0;
            foreach (dynamic r in rest) { restTutar += (decimal)r.tutar; restAdet += (int)r.adet; }
            top.Add(new { urun = "Diğer", tutar = restTutar, adet = restAdet, detay = rest });
            return top;
        }
    }
}
