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
        private static IQueryable<TBL_VARUNA_TEKLIF> ExcludeTest(IQueryable<TBL_VARUNA_TEKLIF> q)
            => q.Where(t => t.Account_Title == null || (!t.Account_Title.Contains("TEST") && !t.Account_Title.Contains("DENEME") && !t.Account_Title.Contains("test") && !t.Account_Title.Contains("deneme")));
        private static IQueryable<TBL_VARUNA_SIPARI> ExcludeTestSiparis(IQueryable<TBL_VARUNA_SIPARI> q)
            => q.Where(s => s.AccountTitle == null || (!s.AccountTitle.Contains("TEST") && !s.AccountTitle.Contains("DENEME") && !s.AccountTitle.Contains("test") && !s.AccountTitle.Contains("deneme")));
        private static IQueryable<TBL_VARUNA_OPPORTUNITIES> ExcludeTestFirsat(IQueryable<TBL_VARUNA_OPPORTUNITIES> q)
            => q.Where(o => o.DeletedOn == null && (o.Name == null || (!o.Name.Contains("TEST") && !o.Name.Contains("DENEME") && !o.Name.Contains("test") && !o.Name.Contains("deneme"))));

        public FirsatAnalizController(
            IDbContextFactory<MskDbContext> contextFactory,
            IMemoryCache cache,
            SOS.Services.ITahakkukService tahakkukService,
            ICockpitDataService cockpitData)
        {
            _contextFactory = contextFactory;
            _cache = cache;
            _tahakkukService = tahakkukService;
            _cockpitData = cockpitData;
        }

        /// <summary>
        /// Sipariş için efektif fatura tarihi: tahakkuk varsa onu, yoksa orijinal InvoiceDate'i döner.
        /// </summary>
        private static DateTime? EfektifInvoice(string? serialNumber, DateTime? invoiceDate, Dictionary<string, DateTime> tahakkukMap)
        {
            if (serialNumber != null && tahakkukMap.TryGetValue(serialNumber, out var th))
                return th;
            return invoiceDate;
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
                    filter = "all";
                    start = new DateTime(2020, 1, 1);
                    end2 = today;
                    months = (today.Year - 2020) * 12 + today.Month;
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
                    && EfektifInvoice(s.SerialNumber, s.InvoiceDate, tahakkukMap) is DateTime ef
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
        /// Dönem hedef tutarını DB'den çeker (TBLSOS_HEDEF_AYLIK, Tip=GENEL)
        /// </summary>
        private async Task<decimal> GetDonemHedefAsync(DateTime start, DateTime end)
        {
            var months = Enumerable.Range(0, (end.Year - start.Year) * 12 + end.Month - start.Month + 1)
                .Select(i => new { Yil = start.AddMonths(i).Year, Ay = start.AddMonths(i).Month });

            using var db = _contextFactory.CreateDbContext();
            decimal toplam = 0;
            foreach (var m in months)
            {
                var hedef = await db.TBLSOS_HEDEF_AYLIKs.AsNoTracking()
                    .Where(h => h.Yil == m.Yil && h.Ay == m.Ay && h.Tip == "GENEL" && h.Aktif)
                    .Select(h => h.HedefTutar)
                    .FirstOrDefaultAsync();
                toplam += hedef;
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
            if (_cache.TryGetValue(cacheKey, out object? cached) && cached != null)
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
            var cacheKey = $"FirsatOppSummary_exclusive_{start:yyyyMMdd}_{end:yyyyMMdd}_{owner ?? "all"}";
            if (_cache.TryGetValue(cacheKey, out object? cachedResult) && cachedResult != null)
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

            // ── Tahakkuk-aware fırsat filtresi ──
            // Kapalı siparişli fırsatlar → efektif tarih dönem dışıysa havuzdan çıkar
            var tahakkukMap = await _tahakkukService.GetTahakkukMapAsync();

            // Yol 1: Teklif(QuoteId) → Sipariş zinciri
            var kapaliZincir = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue)
                .Join(ExcludeTestSiparis(db.TBL_VARUNA_SIPARIs.AsNoTracking())
                    .Where(s => s.OrderStatus == "Closed" && s.QuoteId != null),
                    t => t.Id.ToString(), s => s.QuoteId,
                    (t, s) => new { OppId = t.OpportunityId!.Value.ToString().ToLower(), s.SerialNumber, s.InvoiceDate })
                .ToListAsync();

            // Yol 2: Won fırsatlar → Teklif(Account_Title) → Sipariş(AccountTitle) eşleşmesi
            // Teklif tablosundan: OpportunityId → Account_Title
            var wonOppIds = await ExcludeTestFirsat(db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking())
                .Where(o => o.OpportunityStageName == "Won")
                .Select(o => o.Id).ToListAsync();
            var wonOppIdSet = wonOppIds.Where(id => id != null).Select(id => id!.ToLower()).ToHashSet();
            // Teklif → Won fırsatın müşteri adı
            var teklifMusteriMap = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue && t.Account_Title != null)
                .Select(t => new { OppId = t.OpportunityId!.Value.ToString().ToLower(), t.Account_Title })
                .ToListAsync();
            var oppMusteriMap = teklifMusteriMap
                .Where(t => wonOppIdSet.Contains(t.OppId))
                .GroupBy(t => t.OppId)
                .ToDictionary(g => g.Key, g => g.First().Account_Title!.Trim().ToLower());
            // Sipariş AccountTitle → efektif tarih
            var closedSipEfektif = await ExcludeTestSiparis(db.TBL_VARUNA_SIPARIs.AsNoTracking())
                .Where(s => s.OrderStatus == "Closed" && s.SerialNumber != null && s.AccountTitle != null)
                .Select(s => new { s.SerialNumber, s.InvoiceDate, AccountTitle = s.AccountTitle!.Trim().ToLower() })
                .ToListAsync();
            var musteriEfektifMap = closedSipEfektif
                .GroupBy(s => s.AccountTitle)
                .ToDictionary(g => g.Key, g => {
                    // Tahakkuk override olan en güncel siparişin efektif tarihi
                    foreach (var s in g.OrderByDescending(x => x.InvoiceDate))
                    {
                        var ef = EfektifInvoice(s.SerialNumber, s.InvoiceDate, tahakkukMap);
                        if (ef.HasValue && s.InvoiceDate.HasValue && ef.Value != s.InvoiceDate.Value)
                            return ef; // Tahakkuk override var — bu tarihi kullan
                    }
                    return g.OrderByDescending(x => x.InvoiceDate).First().InvoiceDate;
                });

            // OppId → Efektif tarih haritası
            var kapaliOppEfektif = kapaliZincir
                .GroupBy(x => x.OppId)
                .ToDictionary(g => g.Key, g => {
                    var first = g.First();
                    return EfektifInvoice(first.SerialNumber, first.InvoiceDate, tahakkukMap);
                });
            // Won fırsatlar: teklifteki müşteri adı → siparişteki müşteri → efektif tarih
            foreach (var kv in oppMusteriMap)
            {
                if (!kapaliOppEfektif.ContainsKey(kv.Key)
                    && musteriEfektifMap.TryGetValue(kv.Value, out var efDate))
                {
                    kapaliOppEfektif[kv.Key] = efDate;
                }
            }

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

            // Exclusive pipeline: Open sipariş → fırsat bağlantısı (tüm zamanlar)
            var openSiparisOppTask = ExcludeTest(db_exSip.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue)
                .Join(ExcludeTestSiparis(db_exSip.TBL_VARUNA_SIPARIs.AsNoTracking())
                    .Where(s => s.OrderStatus == "Open" && s.QuoteId != null),
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

            // Exclusive pipeline K5: Cockpit fatura → fırsat bağlantısı
            var cockpitFaturaTask = _cockpitData.GetFaturalarAsync(start, end, owner);
            var cockpitFaturaOppTask = ExcludeTest(db_cockpitFat.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue)
                .Join(ExcludeTestSiparis(db_cockpitFat.TBL_VARUNA_SIPARIs.AsNoTracking())
                    .Where(s => s.OrderStatus == "Closed" && s.SerialNumber != null),
                    t => t.Id.ToString(), s => s.QuoteId,
                    (t, s) => new { OppId = t.OpportunityId!.Value.ToString().ToLower(), s.SerialNumber })
                .ToListAsync();

            await Task.WhenAll(tumFirsatTask, firsatsizTeklifTask, donemFirsatIdsTask, openSiparisOppTask, aktifTeklifOppTask, cockpitFaturaTask, cockpitFaturaOppTask);

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
            var agirlikliPotansiyel = aktifTeklifler.Sum(t => {
                var prob = t.OpportunityId.HasValue && firsatProbMap.TryGetValue(t.OpportunityId.Value.ToString(), out var p) ? p : 0m;
                return (t.TotalNetAmountLocalCurrency_Amount ?? 0m) * prob / 100m;
            });
            var ortOlasilik = aktifTeklifler.Count > 0
                ? aktifTeklifler.Average(t => {
                    return t.OpportunityId.HasValue && firsatProbMap.TryGetValue(t.OpportunityId.Value.ToString(), out var p) ? p : 0m;
                }) : 0m;

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
                .Select(s => new { s.SerialNumber, s.TotalNetAmount, s.OrderStatus, s.InvoiceDate, s.CreateOrderDate })
                .ToListAsync();
            var donemSiparisler = donemSiparislerRaw.Select(s => new {
                s.SerialNumber,
                s.TotalNetAmount,
                s.OrderStatus,
                EfektifTarih = EfektifInvoice(s.SerialNumber, s.InvoiceDate, tahakkukMap),
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
            var cockpitFaturaOppIds = cockpitFaturaOppMap
                .Where(x => x.SerialNumber != null && cockpitFaturaNoSet.Contains(x.SerialNumber))
                .Select(x => x.OppId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var exFaturaIds = dataAktif
                .Where(d => cockpitFaturaOppIds.Contains((d.Id ?? "").ToLower()))
                .Select(d => (d.Id ?? "").ToLower())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // K4 — SiparisSet: Open siparişi olan, FaturaSet hariç
            var exSiparisIds = dataAktif
                .Where(d => openSiparisOppSet.Contains((d.Id ?? "").ToLower())
                    && !exFaturaIds.Contains((d.Id ?? "").ToLower()))
                .Select(d => (d.Id ?? "").ToLower())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // K3 — TeklifSet: Aktif teklifi olan, Fatura+Sipariş hariç
            var exTeklifIds = dataAktif
                .Where(d => aktifTeklifOppSet.Contains((d.Id ?? "").ToLower())
                    && !exFaturaIds.Contains((d.Id ?? "").ToLower())
                    && !exSiparisIds.Contains((d.Id ?? "").ToLower()))
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

            // ── PARALEL GRUP 2: Yıllık trend sorguları ──
            var yil = DateTime.Now.Year;
            using var db4 = _contextFactory.CreateDbContext();
            using var db5 = _contextFactory.CreateDbContext();

            var tumYilFirsatTask = ExcludeTestFirsat(db4.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking())
                .Where(o => o.CloseDate.HasValue && o.CloseDate.Value.Year == yil
                    && o.OpportunityStageName != "Lost")
                .Select(o => new { o.Id, o.CloseDate, o.OpportunityStageName, o.AmountAmount })
                .ToListAsync();

            var tumYilTeklifTask = ExcludeTest(db5.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.CreatedOn.HasValue && t.CreatedOn.Value.Year == yil)
                .Select(t => new { t.CreatedOn, t.TotalNetAmountLocalCurrency_Amount })
                .ToListAsync();
            using var db6 = _contextFactory.CreateDbContext();
            var tumYilSiparisTask = ExcludeTestSiparis(db6.TBL_VARUNA_SIPARIs.AsNoTracking())
                .Where(s => s.CreateOrderDate.HasValue && s.CreateOrderDate.Value.Year == yil && s.OrderStatus != "Canceled")
                .Select(s => new { s.CreateOrderDate, s.OrderStatus, s.TotalNetAmount })
                .ToListAsync();

            await Task.WhenAll(tumYilFirsatTask, tumYilTeklifTask, tumYilSiparisTask);

            var tumYilFirsatlar = tumYilFirsatTask.Result;
            // Kapalı siparişi olanları da düş
            var tumYilAktif = tumYilFirsatlar.Where(o => !kapaliSet.Contains((o.Id ?? "").ToLower())).ToList();
            var aylikFirsatlar = tumYilAktif
                .GroupBy(d => $"{d.CloseDate!.Value.Year}-{d.CloseDate.Value.Month:D2}")
                .ToDictionary(g => g.Key, g => new { toplam = g.Count(), won = g.Count(d => d.OpportunityStageName == "Won"), lost = 0, tutar = g.Sum(d => d.AmountAmount ?? 0m) });

            var tumYilTeklifler = tumYilTeklifTask.Result;
            var aylikTeklifData = tumYilTeklifler
                .GroupBy(t => $"{t.CreatedOn!.Value.Year}-{t.CreatedOn.Value.Month:D2}")
                .ToDictionary(g => g.Key, g => new { adet = g.Count(), tutar = g.Sum(t => t.TotalNetAmountLocalCurrency_Amount ?? 0m) });

            var tumYilSiparisler = tumYilSiparisTask.Result;
            var aylikSiparisData = tumYilSiparisler
                .GroupBy(s => $"{s.CreateOrderDate!.Value.Year}-{s.CreateOrderDate.Value.Month:D2}")
                .ToDictionary(g => g.Key, g => new {
                    acik = g.Count(s => s.OrderStatus == "Open"),
                    kapali = g.Count(s => s.OrderStatus == "Closed"),
                    acikTutar = g.Where(s => s.OrderStatus == "Open").Sum(s => s.TotalNetAmount ?? 0m),
                    kapaliTutar = g.Where(s => s.OrderStatus == "Closed").Sum(s => s.TotalNetAmount ?? 0m)
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
                // Potansiyel (teklif × fırsat olasılığı — mevcut sorgulardan)
                agirlikliPotansiyel,
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
                    (t, s) => new { OppId = t.OpportunityId!.Value.ToString().ToLower(), s.SerialNumber, s.InvoiceDate })
                .ToListAsync();
            var oppEfektifMap = kapaliZincir.GroupBy(x => x.OppId)
                .ToDictionary(g => g.Key, g => EfektifInvoice(g.First().SerialNumber, g.First().InvoiceDate, tahakkukMap));

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
            if (_cache.TryGetValue(cacheKey, out object? cached) && cached != null)
                return Json(cached);

            using var db1 = _contextFactory.CreateDbContext();
            using var db2 = _contextFactory.CreateDbContext();
            using var db3 = _contextFactory.CreateDbContext();
            using var db4 = _contextFactory.CreateDbContext();

            // Paralel 4 SP + fatura özeti
            object ownerParam = (object?)owner ?? DBNull.Value;
            var k1Task = db1.Database.SqlQueryRaw<PipelineFirsatRow>(
                "EXEC SP_PIPELINE_FIRSAT @p0, @p1, @p2",
                DBNull.Value, DBNull.Value, ownerParam).FirstOrDefaultAsync();
            var k2Task = db2.Database.SqlQueryRaw<PipelineFirsatRow>(
                "EXEC SP_PIPELINE_FIRSAT @p0, @p1, @p2",
                start, end, ownerParam).FirstOrDefaultAsync();
            var k3Task = db3.Database.SqlQueryRaw<PipelineTeklifRow>(
                "EXEC SP_PIPELINE_TEKLIF @p0, @p1, @p2",
                start, end, ownerParam).FirstOrDefaultAsync();
            var k4Task = db4.Database.SqlQueryRaw<PipelineSiparisRow>(
                "EXEC SP_PIPELINE_SIPARIS @p0, @p1, @p2",
                start, end, ownerParam).FirstOrDefaultAsync();
            var k5Task = _cockpitData.GetFaturaOzetAsync(start, end, owner);

            await Task.WhenAll(k1Task, k2Task, k3Task, k4Task, k5Task);

            var k1 = k1Task.Result ?? new PipelineFirsatRow();
            var k2 = k2Task.Result ?? new PipelineFirsatRow();
            var k3 = k3Task.Result ?? new PipelineTeklifRow();
            var k4 = k4Task.Result ?? new PipelineSiparisRow();
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
            if (_cache.TryGetValue(cacheKey, out object? cached) && cached != null)
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
            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);
            var cacheKey = $"FirsatCycle_{start:yyyyMMdd}_{end:yyyyMMdd}_{person ?? "all"}";

            if (_cache.TryGetValue(cacheKey, out object? cached) && cached != null)
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

            // 5a) Fırsat kapanış süresi: CreatedOn → CloseDate (Won fırsatlar, dönemde)
            var wonFirsatKapanisSuresi = await ExcludeTestFirsat(db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking())
                .Where(o => o.OpportunityStageName == "Won"
                    && o.CreatedOn.HasValue && o.CloseDate.HasValue
                    && o.CloseDate.Value >= start && o.CloseDate.Value <= end)
                .Select(o => new { o.CreatedOn, o.CloseDate })
                .ToListAsync();
            var kapanisGunleri = wonFirsatKapanisSuresi
                .Select(o => (o.CloseDate!.Value - o.CreatedOn!.Value).TotalDays)
                .Where(d => d >= 0)
                .OrderBy(d => d).ToList();
            var ortFirsatKapanis = kapanisGunleri.Count > 0 ? Math.Round(kapanisGunleri.Average(), 1) : 0.0;
            var medyanFirsatKapanis = kapanisGunleri.Count > 0
                ? (kapanisGunleri.Count % 2 == 0
                    ? Math.Round((kapanisGunleri[kapanisGunleri.Count / 2 - 1] + kapanisGunleri[kapanisGunleri.Count / 2]) / 2.0, 1)
                    : Math.Round(kapanisGunleri[kapanisGunleri.Count / 2], 1))
                : 0.0;
            var firsatKapanisAdet = kapanisGunleri.Count;

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
        /// TBL_VARUNA_ACCOUNT_REPRESENTATIVES.AccountOwnerId → TBL_VARUNA_PERSON.PersonNameSurname map'i
        /// </summary>
        private async Task<Dictionary<string, string>> GetAccountRepPersonMapAsync(MskDbContext db)
        {
            var mapCacheKey = "account_rep_person_map";
            if (_cache.TryGetValue(mapCacheKey, out Dictionary<string, string>? cached) && cached != null)
                return cached;

            // AccountOwnerId'leri al
            var reps = await db.TBL_VARUNA_ACCOUNT_REPRESENTATIVESs.AsNoTracking()
                .Where(r => r.AccountOwnerId.HasValue && r.State == "Active")
                .Select(r => r.AccountOwnerId!.Value)
                .Distinct()
                .ToListAsync();

            var repIdStrings = reps.Select(r => r.ToString().ToLower()).ToHashSet();

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
        /// AccountId → AccountOwnerId (lowercase string) map
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

        // ───────────────────────────────────────────────────────────────
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
            string? owner, string? stage, string? customer, string? product, string? ownerName, string? firsatSahibi = null, int page = 1, int pageSize = 20, int? funnel = null)
        {
            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);

            // ── Cache kontrolü ──
            var detailCacheKey = $"FirsatDetail_{start:yyyyMMdd}_{end:yyyyMMdd}_{stage ?? ""}_{owner ?? ""}_{customer ?? ""}_{product ?? ""}_{ownerName ?? ""}_{firsatSahibi ?? ""}_{funnel?.ToString() ?? ""}_{page}";
            if (_cache.TryGetValue(detailCacheKey, out object? cachedDetail) && cachedDetail != null)
                return Json(cachedDetail);

            using var db = _contextFactory.CreateDbContext();
            var ownerMap = await GetOwnerMapAsync();

            // TBL_VARUNA_OPPORTUNITIES — CloseDate bazlı filtreleme
            var query = ExcludeTestFirsat(db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking())
                .Where(o => o.CloseDate.HasValue
                    && o.CloseDate.Value >= start && o.CloseDate.Value <= end);

            // Funnel (exclusive pipeline) filtresi — breakdown ile BIREBIR aynı küme
            if (funnel.HasValue && funnel.Value >= 2 && funnel.Value <= 5)
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

            // Satış Temsilcisi filtresi — breakdown'la BIREBIR aynı efektif ad semantiği
            if (!string.IsNullOrEmpty(ownerName))
            {
                var personMapDet = await GetPersonMapAsync(db);
                var accRepMap = await GetAccountToRepMapAsync(db);
                var cand = await query
                    .Select(o => new { o.Id, o.AccountId, o.OwnerId })
                    .ToListAsync();
                var matchedIds = cand.Where(o => {
                    string? efektifAd = null;
                    if (o.AccountId != null
                        && accRepMap.TryGetValue(o.AccountId.ToLower(), out var repId)
                        && personMapDet.TryGetValue(repId, out var repName))
                        efektifAd = repName;
                    else if (o.OwnerId != null)
                        efektifAd = personMapDet.TryGetValue(o.OwnerId.ToLower(), out var on)
                            ? on
                            : ResolveOwnerName(o.OwnerId, ownerMap);
                    return efektifAd == ownerName;
                }).Select(o => o.Id).ToList();
                query = query.Where(o => matchedIds.Contains(o.Id));
            }

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

            // Ürün filtresi — direkt fırsat tablosundan (ProductGroupId → Name)
            if (!string.IsNullOrEmpty(product))
            {
                if (product == "Tanımsız" || product == "Diğer")
                {
                    query = query.Where(o => o.ProductGroupId == null);
                }
                else
                {
                    var productOppIds = await db.Database.SqlQueryRaw<string>(
                        @"SELECT o.Id FROM TBL_VARUNA_OPPORTUNITIES o
                          JOIN TBL_VARUNA_PRODUCTGRUPS pg ON o.ProductGroupId = CAST(pg.Id AS NVARCHAR(64))
                          WHERE pg.Name = {0} AND o.DeletedOn IS NULL", product).ToListAsync();
                    var prodIdSet = productOppIds.ToHashSet();
                    query = query.Where(o => prodIdSet.Contains(o.Id));
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

            var total = await query.CountAsync();
            var items = await query
                .OrderByDescending(o => o.CloseDate)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(o => new
                {
                    o.Name,
                    o.OpportunityStageName,
                    DealTypeTR = o.DealType,
                    o.Probability,
                    o.CloseDate,
                    o.AmountAmount,
                    ownerId = o.OwnerId,
                    o.Source
                })
                .ToListAsync();

            // Müşteri isimleri: Fırsat Id → Teklif.OpportunityId → Account_Title
            // SQL JOIN ile çöz — Guid case farkı sorun yaratmasın
            var oppNames = items.Select(i => i.Name).Where(n => n != null).Distinct().ToList();
            var nameMusteriMap = new Dictionary<string, string>();
            if (oppNames.Count > 0)
            {
                // Fırsat Id'lerini al
                var oppIdPairs = await ExcludeTestFirsat(db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking())
                    .Where(o => oppNames.Contains(o.Name))
                    .Select(o => new { o.Name, o.Id })
                    .ToListAsync();
                var oppIdList = oppIdPairs.Select(x => x.Id).Where(id => id != null).Distinct().ToList();
                // Teklif tablosundan müşteri ismi (LOWER ile case-insensitive eşleşme)
                var teklifMusteri = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                    .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue && t.Account_Title != null)
                    .Select(t => new { OppId = t.OpportunityId!.Value.ToString().ToLower(), t.Account_Title })
                    .ToListAsync();
                var musteriLookup = teklifMusteri
                    .GroupBy(t => t.OppId)
                    .ToDictionary(g => g.Key, g => g.First().Account_Title ?? "");
                foreach (var p in oppIdPairs)
                {
                    if (p.Name != null && p.Id != null && musteriLookup.TryGetValue(p.Id.ToLower(), out var musteri))
                        nameMusteriMap.TryAdd(p.Name, musteri);
                }
            }

            // Teklif sahibi lookup: fırsat Id → ProposalOwnerId (teklif varsa öncelikli)
            var itemOppIds = items.Select(i => i.ownerId).Where(id => id != null).Distinct().ToList();
            var allItemIds = items.Select(i => i.Name).Where(n => n != null).Distinct().ToList();
            var detayFirsatIds = await ExcludeTestFirsat(db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking())
                .Where(o => allItemIds.Contains(o.Name))
                .Select(o => new { o.Name, o.Id }).ToListAsync();
            var detayTeklifOwners = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue && t.ProposalOwnerId.HasValue)
                .Select(t => new { OppId = t.OpportunityId!.Value.ToString().ToLower(), t.ProposalOwnerId })
                .ToListAsync();
            var detayOppToOwner = detayTeklifOwners
                .GroupBy(t => t.OppId)
                .ToDictionary(g => g.Key, g => g.First().ProposalOwnerId!.Value.ToString().ToLower());
            var nameToFirsatId = detayFirsatIds.Where(x => x.Name != null).GroupBy(x => x.Name!).ToDictionary(g => g.Key, g => (g.First().Id ?? "").ToLower());

            var result = items.Select(i => new
            {
                i.Name,
                asama = i.OpportunityStageName,
                anlasmaTipi = i.DealTypeTR,
                olasilik = i.Probability,
                tutar = i.AmountAmount,
                kapanisTarihi = i.CloseDate?.ToString("dd.MM.yyyy"),
                satisTemsilcisi = i.Name != null && nameToFirsatId.TryGetValue(i.Name, out var fid) && detayOppToOwner.TryGetValue(fid, out var po)
                    ? ResolveOwnerName(po, ownerMap)
                    : ResolveOwnerName(i.ownerId, ownerMap),
                kaynak = i.Source,
                musteri = i.Name != null && nameMusteriMap.TryGetValue(i.Name, out var m) ? m : ""
            }).ToList();

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
        private async Task<ExclusivePipelineSets> GetExclusiveSetsAsync(
            DateTime start, DateTime end, string? owner,
            HashSet<string> donemFirsatIdSetLower)
        {
            using var dbExSip = _contextFactory.CreateDbContext();
            using var dbExTek = _contextFactory.CreateDbContext();
            using var dbCockpitFat = _contextFactory.CreateDbContext();

            // Open sipariş → fırsat bağlantısı (tüm zamanlar)
            var openSiparisOppTask = ExcludeTest(dbExSip.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue)
                .Join(ExcludeTestSiparis(dbExSip.TBL_VARUNA_SIPARIs.AsNoTracking())
                    .Where(s => s.OrderStatus == "Open" && s.QuoteId != null),
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
            var cockpitFaturaOppTask = ExcludeTest(dbCockpitFat.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue)
                .Join(ExcludeTestSiparis(dbCockpitFat.TBL_VARUNA_SIPARIs.AsNoTracking())
                    .Where(s => s.OrderStatus == "Closed" && s.SerialNumber != null),
                    t => t.Id.ToString(), s => s.QuoteId,
                    (t, s) => new { OppId = t.OpportunityId!.Value.ToString().ToLower(), s.SerialNumber })
                .ToListAsync();

            await Task.WhenAll(openSiparisOppTask, aktifTeklifOppTask, cockpitFaturaTask, cockpitFaturaOppTask);

            var openSiparisOppSet = openSiparisOppTask.Result.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var aktifTeklifOppSet = aktifTeklifOppTask.Result.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var cockpitFaturalar = cockpitFaturaTask.Result;
            var cockpitFaturaNoSet = cockpitFaturalar.Select(f => f.FaturaNo).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var cockpitFaturaOppIds = cockpitFaturaOppTask.Result
                .Where(x => x.SerialNumber != null && cockpitFaturaNoSet.Contains(x.SerialNumber))
                .Select(x => x.OppId).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // K5 — FaturaSet: Cockpit faturalarına bağlı fırsatlar
            var exFaturaIds = donemFirsatIdSetLower
                .Where(id => cockpitFaturaOppIds.Contains(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // K4 — SiparisSet: Open siparişi olan, FaturaSet hariç
            var exSiparisIds = donemFirsatIdSetLower
                .Where(id => openSiparisOppSet.Contains(id) && !exFaturaIds.Contains(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // K3 — TeklifSet: Aktif teklifi olan, Fatura+Sipariş hariç
            var exTeklifIds = donemFirsatIdSetLower
                .Where(id => aktifTeklifOppSet.Contains(id) && !exFaturaIds.Contains(id) && !exSiparisIds.Contains(id))
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
                    (t, s) => new { OppId = t.OpportunityId!.Value.ToString().ToLower(), s.SerialNumber, s.InvoiceDate })
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
                .Select(s => new { s.SerialNumber, s.InvoiceDate, AccountTitle = s.AccountTitle!.Trim().ToLower() })
                .ToListAsync();
            var musteriEfektif = closedSip.GroupBy(s => s.AccountTitle)
                .ToDictionary(g => g.Key, g => {
                    foreach (var s in g.OrderByDescending(x => x.InvoiceDate))
                    {
                        var ef = EfektifInvoice(s.SerialNumber, s.InvoiceDate, tahakkukMap);
                        if (ef.HasValue && s.InvoiceDate.HasValue && ef.Value != s.InvoiceDate.Value) return ef;
                    }
                    return g.OrderByDescending(x => x.InvoiceDate).First().InvoiceDate;
                });
            var kapaliOppEfektif = kapaliZincir.GroupBy(x => x.OppId)
                .ToDictionary(g => g.Key, g => EfektifInvoice(g.First().SerialNumber, g.First().InvoiceDate, tahakkukMap));
            foreach (var kv in oppMusteri)
                if (!kapaliOppEfektif.ContainsKey(kv.Key) && musteriEfektif.TryGetValue(kv.Value, out var ef))
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
            var cacheKey = $"FirsatFunnel_{start:yyyyMMdd}_{end:yyyyMMdd}_{funnel}_{customer ?? ""}_{product ?? ""}_{ownerName ?? ""}_{firsatSahibi ?? ""}";
            if (_cache.TryGetValue(cacheKey, out object? cachedFunnel) && cachedFunnel != null)
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

            // Ürün filtresi — direkt fırsat tablosundan (ProductGroupId → Name)
            if (!string.IsNullOrEmpty(product))
            {
                if (product == "Tanımsız" || product == "Diğer")
                {
                    firsatQuery = firsatQuery.Where(o => o.ProductGroupId == null);
                }
                else
                {
                    var prodOppIds = await db.Database.SqlQueryRaw<string>(
                        @"SELECT o.Id FROM TBL_VARUNA_OPPORTUNITIES o
                          JOIN TBL_VARUNA_PRODUCTGRUPS pg ON o.ProductGroupId = CAST(pg.Id AS NVARCHAR(64))
                          WHERE pg.Name = {0} AND o.DeletedOn IS NULL", product).ToListAsync();
                    var prodIdSet = prodOppIds.ToHashSet();
                    firsatQuery = firsatQuery.Where(o => prodIdSet.Contains(o.Id));
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

            // Tahakkuk bazlı kapaliSet: dönem dışına kayan kapalı siparişli fırsatları çıkar
            var tahakkukMapFunnel = await _tahakkukService.GetTahakkukMapAsync();
            var kapaliZincirFunnel = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue)
                .Join(ExcludeTestSiparis(db.TBL_VARUNA_SIPARIs.AsNoTracking())
                    .Where(s => s.OrderStatus == "Closed" && s.QuoteId != null),
                    t => t.Id.ToString(), s => s.QuoteId,
                    (t, s) => new { OppId = t.OpportunityId!.Value.ToString().ToLower(), s.SerialNumber, s.InvoiceDate })
                .ToListAsync();
            // Won fırsatlar → teklif müşteri → sipariş müşteri → efektif tarih
            var wonIdsF = await ExcludeTestFirsat(db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking())
                .Where(o => o.OpportunityStageName == "Won")
                .Select(o => o.Id!.ToLower()).ToListAsync();
            var wonSetF = wonIdsF.ToHashSet();
            var teklifMusteriF = await ExcludeTest(db.TBL_VARUNA_TEKLIFs.AsNoTracking())
                .Where(t => t.DeletedOn == null && t.OpportunityId.HasValue && t.Account_Title != null)
                .Select(t => new { OppId = t.OpportunityId!.Value.ToString().ToLower(), t.Account_Title })
                .ToListAsync();
            var oppMusteriF = teklifMusteriF.Where(t => wonSetF.Contains(t.OppId))
                .GroupBy(t => t.OppId).ToDictionary(g => g.Key, g => g.First().Account_Title!.Trim().ToLower());
            var closedSipF = await ExcludeTestSiparis(db.TBL_VARUNA_SIPARIs.AsNoTracking())
                .Where(s => s.OrderStatus == "Closed" && s.SerialNumber != null && s.AccountTitle != null)
                .Select(s => new { s.SerialNumber, s.InvoiceDate, AccountTitle = s.AccountTitle!.Trim().ToLower() })
                .ToListAsync();
            var musteriEfektifF = closedSipF.GroupBy(s => s.AccountTitle)
                .ToDictionary(g => g.Key, g => {
                    foreach (var s in g.OrderByDescending(x => x.InvoiceDate))
                    {
                        var ef = EfektifInvoice(s.SerialNumber, s.InvoiceDate, tahakkukMapFunnel);
                        if (ef.HasValue && s.InvoiceDate.HasValue && ef.Value != s.InvoiceDate.Value) return ef;
                    }
                    return g.OrderByDescending(x => x.InvoiceDate).First().InvoiceDate;
                });
            var kapaliOppEfektifF = kapaliZincirFunnel.GroupBy(x => x.OppId)
                .ToDictionary(g => g.Key, g => EfektifInvoice(g.First().SerialNumber, g.First().InvoiceDate, tahakkukMapFunnel));
            foreach (var kv in oppMusteriF)
                if (!kapaliOppEfektifF.ContainsKey(kv.Key) && musteriEfektifF.TryGetValue(kv.Value, out var ef))
                    kapaliOppEfektifF[kv.Key] = ef;
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
                .Select(s => new { s.OrderId, s.SerialNumber, s.AccountTitle, s.TotalNetAmount, s.OrderStatus, s.ProposalOwnerId, s.InvoiceDate })
                .ToListAsync();
            var donemSiparisler = donemSiparislerRaw.Select(s => new {
                s.OrderId,
                s.SerialNumber,
                s.AccountTitle,
                s.TotalNetAmount,
                s.OrderStatus,
                s.ProposalOwnerId,
                InvoiceDate = EfektifInvoice(s.SerialNumber, s.InvoiceDate, tahakkukMap)
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

            if (funnel <= 4)
            {
                // ── Referansla tutarlı base set oluştur ──
                // Funnel 1: Tüm fırsatlar (açık havuz)
                // Funnel 2-4: Exclusive pipeline setleri (fırsat bazlı breakdown)
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
                    var aktifIds = funnel == 2 ? exclusiveSets.ExFirsatIds
                                 : funnel == 3 ? exclusiveSets.ExTeklifIds
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
                        var prodOppIds2 = await db.Database.SqlQueryRaw<string>(
                            @"SELECT o.Id FROM TBL_VARUNA_OPPORTUNITIES o
                              JOIN TBL_VARUNA_PRODUCTGRUPS pg ON o.ProductGroupId = CAST(pg.Id AS NVARCHAR(64))
                              WHERE pg.Name = {0} AND o.DeletedOn IS NULL", product).ToListAsync();
                        var prodIdSet2 = prodOppIds2.ToHashSet();
                        baseQuery = baseQuery.Where(o => prodIdSet2.Contains(o.Id));
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
                    .Select(o => new { o.Id, OwnerId = o.OwnerId ?? "unknown", o.AccountId, o.AccountTitle, o.AmountAmount, o.OpportunityStageName, o.ProductGroupId })
                    .ToListAsync();

                // ── Satış Temsilcisi filtresi (ownerName) — firsatOwnerData in-memory üzerinden efektif ad match
                //    Breakdown'un Satış Temsilcisi hesabıyla BIREBIR aynı: AccountRep (personMap'te var) veya OwnerId fallback
                if (!string.IsNullOrEmpty(ownerName))
                {
                    var personMapF = await GetPersonMapAsync(db);
                    var accRepMapF = await GetAccountToRepMapAsync(db);
                    firsatOwnerData = firsatOwnerData.Where(d => {
                        string? efektifAd = null;
                        if (d.AccountId != null
                            && accRepMapF.TryGetValue(d.AccountId.ToLower(), out var repId)
                            && personMapF.TryGetValue(repId, out var rn))
                            efektifAd = rn;
                        else
                            efektifAd = personMapF.TryGetValue(d.OwnerId.ToLower(), out var on)
                                ? on
                                : ResolveOwnerName(d.OwnerId, ownerMap);
                        return efektifAd == ownerName;
                    }).ToList();
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

                // AccountId → AccountOwnerId map (TBL_VARUNA_ACCOUNT_REPRESENTATIVES)
                var accountToRepMap = await GetAccountToRepMapAsync(db);

                // ── TEK HAVUZ, 4 FARKLI BOYUT — her kart havuzun tamamını gösterir, dip toplamlar eşit ──

                // ── A. SATIŞ TEMSİLCİSİ: tüm havuz — AccountRep varsa rep adı, yoksa OwnerId fallback ──
                var salesGrouped = firsatWithOwner
                    .Select(d => {
                        var repId = (d.AccountId != null && accountToRepMap.TryGetValue(d.AccountId.ToLower(), out var rid)) ? rid : null;
                        string repName;
                        if (repId != null && personMapAll.TryGetValue(repId, out var rn))
                            repName = rn;
                        else
                            repName = personMapAll.TryGetValue(d.OwnerId.ToLower(), out var on) ? on : ResolveOwnerName(d.OwnerId, ownerMap);
                        return new { RepName = repName, d.AmountAmount };
                    })
                    .GroupBy(d => d.RepName)
                    .Select(g => new { adSoyad = g.Key, tutar = g.Sum(d => d.AmountAmount ?? 0m), adet = g.Count() })
                    .OrderByDescending(x => x.tutar)
                    .ToList();
                var salesTop = salesGrouped.Take(10).ToList();
                var salesRestTutar = salesGrouped.Skip(10).Sum(x => x.tutar);
                var salesRestAdet  = salesGrouped.Skip(10).Sum(x => x.adet);
                if (salesRestTutar > 0 || salesRestAdet > 0)
                    salesTop.Add(new { adSoyad = "Diğer", tutar = salesRestTutar, adet = salesRestAdet });
                ownerBreakdown = salesTop;

                // ── B. FIRSAT SAHİPLERİ: tüm havuz — OwnerId bazlı (direkt fırsat tablosundan) ──
                var fsGrouped = firsatWithOwner
                    .GroupBy(d => d.OwnerId!)
                    .Select(g => {
                        var name = personMapAll.TryGetValue(g.Key.ToLower(), out var n) ? n : ResolveOwnerName(g.Key, ownerMap);
                        return new { adSoyad = name, tutar = g.Sum(d => d.AmountAmount ?? 0m), adet = g.Count() };
                    })
                    .OrderByDescending(x => x.tutar)
                    .ToList();
                var fsTop = fsGrouped.Take(10).ToList();
                var fsRestTutar = fsGrouped.Skip(10).Sum(x => x.tutar);
                var fsRestAdet  = fsGrouped.Skip(10).Sum(x => x.adet);
                if (fsRestTutar > 0 || fsRestAdet > 0)
                    fsTop.Add(new { adSoyad = "Diğer", tutar = fsRestTutar, adet = fsRestAdet });
                firsatSahipleriBreakdown = fsTop;

                // ── C. ÜRÜN BAZLI: tüm havuz — ProductGroupId → PG.Name, yoksa "Tanımsız" ──
                var prodGroups = await db.Database.SqlQueryRaw<ProductGroupNameDto>(
                    @"SELECT CAST(Id AS NVARCHAR(64)) AS Id, Name FROM TBL_VARUNA_PRODUCTGRUPS WHERE DeletedOn IS NULL").ToListAsync();
                var prodGroupMap = prodGroups
                    .Where(p => !string.IsNullOrEmpty(p.Id))
                    .GroupBy(p => p.Id!)
                    .ToDictionary(g => g.Key, g => g.First().Name ?? "Tanımsız");
                var prodGrouped = firsatWithOwner
                    .Select(d => new {
                        Urun = (d.ProductGroupId != null && prodGroupMap.TryGetValue(d.ProductGroupId, out var pn))
                            ? pn : "Tanımsız",
                        d.AmountAmount
                    })
                    .GroupBy(x => x.Urun)
                    .Select(g => new { urun = g.Key, tutar = g.Sum(x => x.AmountAmount ?? 0m), adet = g.Count() })
                    .OrderByDescending(x => x.tutar)
                    .ToList();
                var prodTop = prodGrouped.Take(10).ToList();
                var prodRestTutar = prodGrouped.Skip(10).Sum(x => x.tutar);
                var prodRestAdet  = prodGrouped.Skip(10).Sum(x => x.adet);
                if (prodRestTutar > 0 || prodRestAdet > 0)
                    prodTop.Add(new { urun = "Diğer", tutar = prodRestTutar, adet = prodRestAdet });
                productBreakdown = prodTop;

                // ── D. MÜŞTERİ BAZLI: tüm havuz — AccountId → TBL_VARUNA_ACCOUNTS.Title/Name ──
                // DB'de gerçek müşteri tablosu: TBL_VARUNA_ACCOUNTS (Id, Name, SurName, Title)
                var accountIdToTitle = await GetAccountTitleMapAsync(db);

                var custGrouped = firsatWithOwner
                    .Select(d => {
                        string musteri;
                        // Öncelik 1: fırsat tablosundaki AccountTitle (dolu ise — bazı CRM kayıtlarında dolu olabilir)
                        if (!string.IsNullOrWhiteSpace(d.AccountTitle))
                            musteri = d.AccountTitle!.Trim();
                        // Öncelik 2: AccountId → TBL_VARUNA_ACCOUNTS map'inden çöz
                        else if (d.AccountId != null && accountIdToTitle.TryGetValue(d.AccountId.ToLower(), out var t))
                            musteri = t;
                        else
                            musteri = "Tanımsız Müşteri";
                        return new { Musteri = musteri, d.AmountAmount };
                    })
                    .GroupBy(x => x.Musteri)
                    .Select(g => new { musteri = g.Key, tutar = g.Sum(x => x.AmountAmount ?? 0m), adet = g.Count() })
                    .OrderByDescending(x => x.tutar)
                    .ToList();
                var custTop = custGrouped.Take(10).ToList();
                var custRestTutar = custGrouped.Skip(10).Sum(x => x.tutar);
                var custRestAdet  = custGrouped.Skip(10).Sum(x => x.adet);
                if (custRestTutar > 0 || custRestAdet > 0)
                    custTop.Add(new { musteri = "Diğer", tutar = custRestTutar, adet = custRestAdet });
                customerBreakdown = custTop;

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

                // SP fatura → sipariş eşleşmesi (SerialNumber = FaturaNo)
                var spSiparisler = await ExcludeTestSiparis(db.TBL_VARUNA_SIPARIs.AsNoTracking())
                    .Where(s => s.SerialNumber != null && spFaturaNoSet.Contains(s.SerialNumber))
                    .Select(s => new { s.OrderId, s.SerialNumber, s.AccountTitle, s.ProposalOwnerId })
                    .ToListAsync();

                // Alt-katman filtreleri
                if (ownerPidSet != null)
                    spSiparisler = spSiparisler
                        .Where(s => s.ProposalOwnerId != null && ownerPidSet.Contains(s.ProposalOwnerId.ToLower()))
                        .ToList();
                if (!string.IsNullOrEmpty(customer))
                    spSiparisler = spSiparisler.Where(s => s.AccountTitle == customer).ToList();

                // Owner: SP fatura tutarı ile
                var accRepMap2 = await GetAccountRepPersonMapAsync(db);
                var allSpOwnerGroups = spSiparisler
                    .Where(s => !string.IsNullOrEmpty(s.ProposalOwnerId) && s.SerialNumber != null)
                    .GroupBy(s => s.ProposalOwnerId!)
                    .Select(g => new {
                        adSoyad = accRepMap2.TryGetValue(g.Key.ToLower(), out var n) ? n : ResolveOwnerName(g.Key, ownerMap),
                        tutar = g.Sum(s => spTutarMap.GetValueOrDefault(s.SerialNumber!, 0m)),
                        adet = g.Count(),
                        isAccountRep = accRepMap2.ContainsKey(g.Key.ToLower())
                    })
                    .Where(x => !x.adSoyad.Contains("GMAIL", StringComparison.OrdinalIgnoreCase)
                        && !x.adSoyad.Contains("TEST", StringComparison.OrdinalIgnoreCase)
                        && !x.adSoyad.Contains("DENEME", StringComparison.OrdinalIgnoreCase)
                        && !x.adSoyad.StartsWith("Bilinmiyor", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var spSalesRepsSorted = allSpOwnerGroups.Where(x => x.isAccountRep)
                    .OrderByDescending(x => x.tutar).ToList();
                var spSalesTop = spSalesRepsSorted.Take(10).ToList();
                var spSalesRest = spSalesRepsSorted.Skip(10).ToList();
                if (spSalesRest.Any())
                    spSalesTop.Add(new { adSoyad = "Diğer", tutar = spSalesRest.Sum(x => x.tutar), adet = spSalesRest.Sum(x => x.adet), isAccountRep = true });
                ownerBreakdown = spSalesTop;

                var spFsSorted = allSpOwnerGroups.Where(x => !x.isAccountRep)
                    .OrderByDescending(x => x.tutar).ToList();
                var spFsTop = spFsSorted.Take(10).ToList();
                var spFsRest = spFsSorted.Skip(10).ToList();
                if (spFsRest.Any())
                    spFsTop.Add(new { adSoyad = "Diğer", tutar = spFsRest.Sum(x => x.tutar), adet = spFsRest.Sum(x => x.adet), isAccountRep = false });
                firsatSahipleriBreakdown = spFsTop;

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

                productBreakdown = spUrunTlList
                    .GroupBy(x => x.urun)
                    .Select(g => new { urun = g.Key, tutar = g.Sum(x => x.tlTutar), adet = g.Count() })
                    .OrderByDescending(x => x.tutar).ToList();

                // Customer: SP fatura tutarı ile
                funnel45Base = null; // Funnel 5 kendi customerBreakdown'ını üretecek
                var spCustBreakdown = spSiparisler
                    .Where(s => s.AccountTitle != null && s.SerialNumber != null)
                    .GroupBy(s => s.AccountTitle!)
                    .Select(g => new {
                        musteri = g.Key,
                        tutar = g.Sum(s => spTutarMap.GetValueOrDefault(s.SerialNumber!, 0m)),
                        adet = g.Count()
                    })
                    .OrderByDescending(x => x.tutar).Take(10).ToList();
                funnel5CustomerBreakdown = spCustBreakdown;
            }

            // Müşteri verisi — funnel>4 için SP bazlı override
            if (funnel > 4)
                customerBreakdown = funnel5CustomerBreakdown ?? new List<object>();

            var funnelResult = new { ownerBreakdown, firsatSahipleriBreakdown, productBreakdown, customerBreakdown, funnel };
            _cache.Set(cacheKey, funnelResult, CacheTTL);
            return Json(funnelResult);
        }
    }
}
