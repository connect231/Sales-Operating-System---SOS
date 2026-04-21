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
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SQL page cache warmup başarısız (fatal değil)");
        }

        if (stoppingToken.IsCancellationRequested) return;

        // ── 2) FirsatAnaliz minimal endpoint preload (Bu Ay × 5 kritik endpoint) ──
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var faController = ActivatorUtilities.CreateInstance<SOS.Controllers.FirsatAnalizController>(scope.ServiceProvider);
            var faStart = DateTime.UtcNow;

            // Sıralı çalışır — paralel DB connection pool starvation yaratıyordu
            // Controller filter pipeline'ı bypass (auth vs) — direkt action call
            await faController.GetKpiCore("month", null, null, null);
            await faController.GetOpportunitySummary("month", null, null, null);
            await faController.GetFunnelBreakdown("month", null, null, 2, null, null, null, null);
            await faController.GetFunnelBreakdown("month", null, null, 3, null, null, null, null);
            await faController.GetOpportunityDetail("month", null, null, null, null, null, null, null, null, 1, 15, 3);

            var faMs = (int)(DateTime.UtcNow - faStart).TotalMilliseconds;
            _logger.LogInformation("FirsatAnaliz endpoint preload tamamlandı: {Ms}ms (5 çağrı)", faMs);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FirsatAnaliz endpoint preload başarısız (fatal değil)");
        }

        var totalMs = (int)(DateTime.UtcNow - globalStart).TotalMilliseconds;
        _logger.LogInformation("FirsatAnalizStartupWarmer tüm turları bitirdi: {Ms}ms", totalMs);
    }
}
