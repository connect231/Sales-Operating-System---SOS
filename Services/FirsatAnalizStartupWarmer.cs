using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SOS.DbData;

namespace SOS.Services;

/// <summary>
/// Uygulama başlangıcında BIR KEZ çalışır:
/// 1) SQL Server page cache'ini ısıtır — ağır FirsatAnaliz tablolarını touch eder
/// 2) FirsatAnaliz endpoint'lerini preload eder (month × 5 endpoint)
///
/// Amaç: İlk user dashboard açılışında 147s cold call maliyetini ortadan kaldırmak.
/// DB'ye YAZMA yapmaz — yalnızca SELECT. Verileri bozmaz.
/// Hata durumunda sessizce geçer (warmup opsiyoneldir, application başlaması engellenmez).
/// </summary>
public class FirsatAnalizStartupWarmer : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FirsatAnalizStartupWarmer> _logger;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(4);

    public FirsatAnalizStartupWarmer(
        IServiceScopeFactory scopeFactory,
        ILogger<FirsatAnalizStartupWarmer> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        // Periyodik döngü — her 4 dakikada bir cache'i taze tut (idle sonrası cold call olmaz)
        while (!stoppingToken.IsCancellationRequested)
        {
            var globalStart = DateTime.UtcNow;

            // ── 1) SQL page cache warmup (hafif, sadece read) ──
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MskDbContext>>();

            var sqlStart = DateTime.UtcNow;
            // Her warmup task'ı için AYRI DbContext — paralel kullanımda concurrency hatası olmasın
            // (DbContext thread-safe değil; IDbContextFactory ile her task kendi context'ini yaratır)
            async Task WarmAsync(Func<MskDbContext, Task> action)
            {
                using var ctx = factory.CreateDbContext();
                await action(ctx);
            }
            var warmupTasks = new[]
            {
                WarmAsync(d => d.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking().OrderByDescending(o => o.CloseDate).Take(100).ToListAsync(stoppingToken)),
                WarmAsync(d => d.TBL_VARUNA_TEKLIFs.AsNoTracking().OrderByDescending(t => t.CreatedOn).Take(100).ToListAsync(stoppingToken)),
                WarmAsync(d => d.TBL_VARUNA_SIPARIs.AsNoTracking().OrderByDescending(s => s.CreateOrderDate).Take(100).ToListAsync(stoppingToken)),
                WarmAsync(d => d.TBL_VARUNA_SIPARIS_URUNLERIs.AsNoTracking().Take(200).ToListAsync(stoppingToken)),
                WarmAsync(d => d.TBL_VARUNA_PERSONs.AsNoTracking().Take(500).ToListAsync(stoppingToken)),
                WarmAsync(d => d.TBL_VARUNA_ACCOUNT_REPRESENTATIVESs.AsNoTracking().Take(500).ToListAsync(stoppingToken))
            };
            await Task.WhenAll(warmupTasks);
            var sqlMs = (int)(DateTime.UtcNow - sqlStart).TotalMilliseconds;
            _logger.LogInformation("SQL page cache warmup tamamlandı: {Ms}ms", sqlMs);
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SQL page cache warmup başarısız (fatal değil)");
        }

        if (stoppingToken.IsCancellationRequested) break;

        // ── 2) Hedef Karne + AnalizDetay preload (matris cache'i sıcak kalsın) ──
        // FirsatAnaliz preload'undan ÖNCE — Hedef cache'i hızlı populate olur (~3-5s),
        // FirsatAnaliz preload (261s) arka planda devam ederken kullanıcı hedef paneli açabilir.
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var hedef = scope.ServiceProvider.GetRequiredService<IHedefService>();
            var hStart = DateTime.UtcNow;
            var yil = DateTime.Now.Year;

            // SirketOzeti çağrısı ürün matrisini ısıtır
            await hedef.GetSirketOzetiAsync(yil);
            // TemsilciListesi çağrısı temsilci matrisini ısıtır
            await hedef.GetTemsilciListesiAsync(yil);

            // 3 dönem × 3 sekme AnalizDetay — tab-spesifik cache'leri ısıtır
            var bugun = DateTime.Now.Date;
            var ayBaslangic = new DateTime(yil, bugun.Month, 1);
            var ayBitis = ayBaslangic.AddMonths(1).AddDays(-1);
            var q2Baslangic = new DateTime(yil, 4, 1);
            var q2Bitis = new DateTime(yil, 6, 30);
            var ytdBaslangic = new DateTime(yil, 1, 1);
            var ytdBitis = new DateTime(yil, bugun.Month, DateTime.DaysInMonth(yil, bugun.Month));

            int hCount = 2;
            foreach (var (s, e) in new[] { (ayBaslangic, ayBitis), (q2Baslangic, q2Bitis), (ytdBaslangic, ytdBitis) })
            {
                if (stoppingToken.IsCancellationRequested) break;
                foreach (var tab in new[] { "yenileme", "urun", "satisci" })
                {
                    await hedef.GetAnalizDetayAsync(tab, yil, s, e);
                    hCount++;
                }
            }

            var hMs = (int)(DateTime.UtcNow - hStart).TotalMilliseconds;
            _logger.LogInformation("Hedef preload tamamlandı: {Ms}ms ({Count} çağrı — Karne + AnalizDetay 3×3)", hMs, hCount);
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hedef preload başarısız (fatal değil)");
        }

        if (stoppingToken.IsCancellationRequested) break;

        // ── 3) FirsatAnaliz minimal endpoint preload (Bu Ay × 5 kritik endpoint) ──
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var faController = ActivatorUtilities.CreateInstance<SOS.Controllers.FirsatAnalizController>(scope.ServiceProvider);
            var faStart = DateTime.UtcNow;

            // Sıralı çalışır — paralel DB connection pool starvation yaratıyordu
            // Controller filter pipeline'ı bypass (auth vs) — direkt action call
            // Aktif dönemler: month, lastmonth, ytd, aktif çeyrek + all
            // Her dönem KpiCore + Funnel 2/3/4/5 = 5 çağrı; ağır Summary sadece sıcak dönemler için.
            int faCount = 0;
            var aktifCeyrek = "q" + ((DateTime.Now.Month - 1) / 3 + 1).ToString();
            var hotPeriods = new[] { "month", "lastmonth", "ytd", aktifCeyrek };  // Summary dahil
            var coldPeriods = new[] { "all" };                                     // Summary'siz (çok ağır)
            foreach (var flt in hotPeriods)
            {
                if (stoppingToken.IsCancellationRequested) break;
                await faController.GetKpiCore(flt, null, null, null);                                      faCount++;
                await faController.GetOpportunitySummary(flt, null, null, null);                            faCount++;
                foreach (var fn in new[] { 2, 3, 4, 5 })
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    await faController.GetFunnelBreakdown(flt, null, null, fn, null, null, null, null);
                    faCount++;
                }
            }
            // "Tümü" için sadece KpiCore + en kritik funnel — Summary "all" döneminde aşırı yavaş
            foreach (var flt in coldPeriods)
            {
                if (stoppingToken.IsCancellationRequested) break;
                await faController.GetKpiCore(flt, null, null, null);                                      faCount++;
                await faController.GetFunnelBreakdown(flt, null, null, 3, null, null, null, null);          faCount++;
            }
            // Month için detail (en sık tıklanan funnel=3)
            if (!stoppingToken.IsCancellationRequested)
            {
                await faController.GetOpportunityDetail("month", null, null, null, null, null, null, null, null, 1, 15, 3);
                faCount++;
            }

            // Satış hızı kartı (son 3 ay sabit — filter-bağımsız, günlük cache key)
            if (!stoppingToken.IsCancellationRequested)
            {
                await faController.GetSalesCycleData(null, null, null, null);
                faCount++;
            }

            // QW3: Sadece hafif aging özeti ısıt — Hedef Detay paneli için.
            // Rapor detay endpoint'leri (Firsat 0.85s, Teklif 0.3s) ilk istek üzerinden cache'lenir;
            // warmer'a almak SQL'i 3+ dk boyunca paralel meşgul edebiliyor (test edildi: 181s).
            if (!stoppingToken.IsCancellationRequested)
            {
                await faController.GetAgingData(null);
                faCount++;
            }

            var faMs = (int)(DateTime.UtcNow - faStart).TotalMilliseconds;
            _logger.LogInformation("FirsatAnaliz endpoint preload tamamlandı: {Ms}ms ({Count} çağrı — 3 dönem × 6 endpoint + detail)", faMs, faCount);
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FirsatAnaliz endpoint preload başarısız (fatal değil)");
        }

            var totalMs = (int)(DateTime.UtcNow - globalStart).TotalMilliseconds;
            _logger.LogInformation("FirsatAnalizStartupWarmer turu tamamlandı: {Ms}ms, sonraki tur {Interval}", totalMs, RefreshInterval);

            // Sonraki tura kadar bekle — cache hep sıcak kalsın
            try { await Task.Delay(RefreshInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
