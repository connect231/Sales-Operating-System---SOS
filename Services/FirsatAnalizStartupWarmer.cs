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
            using var db = factory.CreateDbContext();

            var sqlStart = DateTime.UtcNow;
            // Ağır tabloları paralel touch — SQL Server plan cache ve page cache ısınır
            // COUNT yerine TOP 1 — büyük tablolarda tam scan'ı tetiklemez
            var warmupTasks = new List<Task>
            {
                db.TBL_VARUNA_OPPORTUNITIESs.AsNoTracking().OrderByDescending(o => o.CloseDate).Take(100).ToListAsync(stoppingToken),
                db.TBL_VARUNA_TEKLIFs.AsNoTracking().OrderByDescending(t => t.CreatedOn).Take(100).ToListAsync(stoppingToken),
                db.TBL_VARUNA_SIPARIs.AsNoTracking().OrderByDescending(s => s.CreateOrderDate).Take(100).ToListAsync(stoppingToken),
                db.TBL_VARUNA_SIPARIS_URUNLERIs.AsNoTracking().Take(200).ToListAsync(stoppingToken),
                db.TBL_VARUNA_PERSONs.AsNoTracking().Take(500).ToListAsync(stoppingToken),
                db.TBL_VARUNA_ACCOUNT_REPRESENTATIVESs.AsNoTracking().Take(500).ToListAsync(stoppingToken)
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

        // ── 2) FirsatAnaliz minimal endpoint preload (Bu Ay × 5 kritik endpoint) ──
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var faController = ActivatorUtilities.CreateInstance<SOS.Controllers.FirsatAnalizController>(scope.ServiceProvider);
            var faStart = DateTime.UtcNow;

            // Sıralı çalışır — paralel DB connection pool starvation yaratıyordu
            // Controller filter pipeline'ı bypass (auth vs) — direkt action call
            // 3 dönem × (KpiCore + Summary + Funnel 2/3/4/5) = 18 çağrı + month detail + 2 bonus = 21 preload
            int faCount = 0;
            foreach (var flt in new[] { "month", "lastmonth", "ytd" })
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
