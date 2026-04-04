using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SOS.DbData;
using SOS.Models.ViewModels;
using SOS.Models.MsK;

namespace SOS.Controllers
{
    [Authorize]
    public class FirsatAnalizController : Controller
    {
        private readonly MskDbContext _context;
        private readonly IMemoryCache _cache;
        private static readonly TimeSpan CacheTTL = TimeSpan.FromMinutes(5);
        private static readonly SemaphoreSlim _cacheLock = new(1, 1);

        // Cache keys
        private const string CACHE_KEY_URUN_ESLESTIRME = "firsat_urun_eslestirme";
        private const string CACHE_KEY_ANA_URUNLER = "firsat_ana_urunler";

        // REAL Status values from database (English strings, not numeric)
        // Accepted=662, Draft=199, Presented=163, Closed=69, Reject=45, Denied=38, InReview=11, Approved=7, PartiallyOrdered=5
        private static readonly string[] WonStatuses = { "Accepted", "Approved", "PartiallyOrdered" };
        private static readonly string[] LostStatuses = { "Reject", "Denied", "Closed" };
        private static readonly string[] OpenStatuses = { "Draft", "Presented", "InReview" };
        // Pipeline = open (not won, not lost)
        private static readonly string[] PipelineStatuses = { "Draft", "Presented", "InReview" };

        // Siparis statuses (from DB: Open, Closed, Canceled)
        private static readonly string[] SiparisClosedStatuses = { "Closed" };
        private static readonly string[] SiparisCancelledStatuses = { "Canceled" };

        public FirsatAnalizController(MskDbContext context, IMemoryCache cache)
        {
            _context = context;
            _cache = cache;
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
                    start = new DateTime(year, 1, 1);
                    end2 = today;
                    months = now.Month;
                    break;
                case "q1":
                    start = new DateTime(year, 1, 1);
                    end2 = new DateTime(year, 3, 31, 23, 59, 59);
                    if (end2 > today) end2 = today;
                    months = 3;
                    break;
                case "q2":
                    start = new DateTime(year, 4, 1);
                    end2 = new DateTime(year, 6, 30, 23, 59, 59);
                    if (end2 > today) end2 = today;
                    months = 3;
                    break;
                case "q3":
                    start = new DateTime(year, 7, 1);
                    end2 = new DateTime(year, 9, 30, 23, 59, 59);
                    if (end2 > today) end2 = today;
                    months = 3;
                    break;
                case "q4":
                    start = new DateTime(year, 10, 1);
                    end2 = new DateTime(year, 12, 31, 23, 59, 59);
                    if (end2 > today) end2 = today;
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
                    filter = "all";
                    start = new DateTime(2020, 1, 1);
                    end2 = today;
                    months = (today.Year - 2020) * 12 + today.Month;
                    break;
                default:
                    filter = "all";
                    start = new DateTime(2020, 1, 1);
                    end2 = today;
                    months = (today.Year - 2020) * 12 + today.Month;
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

                var db = _context;
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
        /// Loads all active TBLSOS_ANA_URUN records. Cached for 5 minutes.
        /// </summary>
        private async Task<List<TBLSOS_ANA_URUN>> GetAnaUrunlerAsync()
        {
            if (_cache.TryGetValue(CACHE_KEY_ANA_URUNLER, out List<TBLSOS_ANA_URUN>? cached) && cached != null)
                return cached;

            var db = _context;
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
            var q = db.TBL_VARUNA_TEKLIFs.AsNoTracking()
                .Where(t => t.DeletedOn == null);

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

        private IQueryable<TBL_VARUNA_SIPARI> GetFilteredSiparisler(MskDbContext db, DateTime start, DateTime end)
        {
            return db.TBL_VARUNA_SIPARIs.AsNoTracking()
                .Where(s => s.CreateOrderDate.HasValue
                    && s.CreateOrderDate.Value >= start
                    && s.CreateOrderDate.Value <= end);
        }

        #endregion

        // ===================================================================
        // GET /FirsatAnaliz/Index
        // ===================================================================
        public IActionResult Index(string? filter, string? startDate, string? endDate)
        {
            var (start, end, activeFilter, _) = ParseFilter(filter, startDate, endDate);

            var vm = new FirsatAnalizViewModel
            {
                AktifFiltre = activeFilter,
                FiltreBaslangic = start,
                FiltreBitis = end
            };

            return View(vm);
        }

        // ===================================================================
        // DEBUG: Tüm alanların doluluk oranı ve örnek değerler
        // ===================================================================
        [HttpGet, AllowAnonymous]
        public async Task<IActionResult> TestKpi(string? filter)
        {
            var (start, end, f, _) = ParseFilter(filter ?? "all", null, null);
            var db = _context;
            var teklifler = GetFilteredTeklifler(db, start, end, null, null);
            var totalCount = await teklifler.CountAsync();
            var openList = new[] { "Draft", "Presented", "InReview" };
            var openCount = await teklifler.Where(t => t.Status != null && openList.Contains(t.Status)).CountAsync();
            var openSum = await teklifler.Where(t => t.Status != null && openList.Contains(t.Status)).SumAsync(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m);
            var wonCount = await teklifler.Where(t => t.Status != null && WonStatuses.Contains(t.Status)).CountAsync();
            var wonSum = await teklifler.Where(t => t.Status != null && WonStatuses.Contains(t.Status)).SumAsync(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m);
            return Json(new { filter = f, start, end, totalCount, openCount, openSum, wonCount, wonSum });
        }

        [HttpGet, AllowAnonymous]
        public async Task<IActionResult> FieldAudit()
        {
            var db = _context;
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

            var db = _context;
            var teklifler = GetFilteredTeklifler(db, start, end, person, product);

            // Pipeline: Status IN open (1,2,3,6) = active pipeline
            var activeTeklifler = teklifler.Where(t => t.Status != null && OpenStatuses.Contains(t.Status));
            var pipelineToplam = await activeTeklifler.SumAsync(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m);
            var aktifFirsatAdet = await activeTeklifler.CountAsync();

            // Trend: compare current period vs previous period of same duration
            var duration = end - start;
            var prevStart = start.AddDays(-duration.TotalDays);
            var prevEnd = start.AddSeconds(-1);
            var prevTeklifler = db.TBL_VARUNA_TEKLIFs.AsNoTracking()
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

            // Siparisler
            var siparisler = GetFilteredSiparisler(db, start, end);
            var acikSiparisler = siparisler.Where(s => s.OrderStatus != null
                && !SiparisClosedStatuses.Contains(s.OrderStatus));
            var acikSiparisAdet = await acikSiparisler.CountAsync();
            var acikSiparisToplam = await acikSiparisler.SumAsync(s => s.TotalNetAmount ?? 0m);

            var kapaliSiparisler = siparisler.Where(s => s.OrderStatus != null
                && (s.OrderStatus == "Closed" || s.OrderStatus == "Completed"));
            var kapaliSiparisAdet = await kapaliSiparisler.CountAsync();
            var kapaliSiparisToplam = await kapaliSiparisler.SumAsync(s => s.TotalNetAmount ?? 0m);

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

            return Json(new FirsatKpiDto(
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
            ));
        }

        // ===================================================================
        // GET /FirsatAnaliz/GetFunnelData
        // ===================================================================
        [HttpGet]
        public async Task<IActionResult> GetFunnelData(string? filter, string? startDate, string? endDate, string? person, string? product)
        {
            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);

            var db = _context;
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

            var linkedSiparisler = db.TBL_VARUNA_SIPARIs.AsNoTracking()
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

            return Json(stages);
        }

        // ===================================================================
        // GET /FirsatAnaliz/GetStatusBreakdown?type=firsatlar|teklifler|siparisler
        // ===================================================================
        [HttpGet]
        public async Task<IActionResult> GetStatusBreakdown(string type, string? filter, string? startDate, string? endDate, string? person, string? product)
        {
            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);

            var db = _context;

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
                    return Json(group);
                }
                case "siparisler":
                {
                    var siparisler = await GetFilteredSiparisler(db, start, end)
                        .GroupBy(s => s.OrderStatus ?? "Bilinmiyor")
                        .Select(g => new { Status = g.Key, Count = g.Count(), Total = g.Sum(s => s.TotalNetAmount ?? 0m) })
                        .ToListAsync();

                    var items = siparisler.Select(g => new StatusBreakdownDto(
                        StatusName: SiparisStatusToTurkish(g.Status),
                        Count: g.Count,
                        TotalValue: g.Total,
                        Color: SiparisStatusToColor(g.Status),
                        Icon: "fas fa-shopping-cart"
                    )).OrderByDescending(i => i.TotalValue).ToList();

                    var group = new StatusBreakdownGroupDto(
                        GroupTitle: "Siparis Durumlari",
                        GrandTotal: items.Sum(i => i.TotalValue),
                        GrandCount: items.Sum(i => i.Count),
                        Items: items
                    );
                    return Json(group);
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

                    var db = _context;

                    for (int i = 0; i < 6; i++)
                    {
                        var monthStart = trendStart.AddMonths(i);
                        var monthEnd = new DateTime(monthStart.Year, monthStart.Month,
                            DateTime.DaysInMonth(monthStart.Year, monthStart.Month), 23, 59, 59);

                        labels.Add(monthStart.ToString("MMM yyyy", new System.Globalization.CultureInfo("tr-TR")));

                        var monthTeklifler = db.TBL_VARUNA_TEKLIFs.AsNoTracking()
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

                        siparisData.Add(await db.TBL_VARUNA_SIPARIs.AsNoTracking()
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

                    var db = _context;

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
                    var db = _context;

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

            var db = _context;

            var teklifler = await db.TBL_VARUNA_TEKLIFs.AsNoTracking()
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

            var db = _context;

            // Base query for open teklifler (no date filter -- risks are global)
            var openTeklifler = db.TBL_VARUNA_TEKLIFs.AsNoTracking()
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
            var agingOrders = await db.TBL_VARUNA_SIPARIs.AsNoTracking()
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

            var db = _context;

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
                    var q = GetFilteredSiparisler(db, start, end);
                    if (!string.IsNullOrEmpty(status))
                        q = q.Where(s => s.OrderStatus == status);

                    var totalCount = await q.CountAsync();
                    var rows = await q
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
                        .ToListAsync();

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
            var db = _context;

            var kisiler = await db.TBL_VARUNA_TEKLIFs.AsNoTracking()
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

            // Use TBLSOS_ANA_URUN + TBLSOS_URUN_ESLESTIRME
            var eslestirmeMap = await GetUrunEslestirmeMapAsync();

            var db = _context;

            // Teklif IDs + statuses in range
            var teklifIdsInRange = await db.TBL_VARUNA_TEKLIFs.AsNoTracking()
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

            // Siparis urunleri in range
            var siparislerInRange = await db.TBL_VARUNA_SIPARIs.AsNoTracking()
                .Where(s => s.CreateOrderDate.HasValue
                    && s.CreateOrderDate.Value >= start
                    && s.CreateOrderDate.Value <= end)
                .Select(s => new { s.OrderId })
                .ToListAsync();

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

            var db = _context;

            // All teklifler for this person in date range
            var personTeklifler = await db.TBL_VARUNA_TEKLIFs.AsNoTracking()
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
    }
}
