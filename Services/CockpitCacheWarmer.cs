using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SOS.Controllers;
using SOS.DbData;

namespace SOS.Services;

/// <summary>
/// Cache warmer'ın durumu — diğer servislerden / controller'dan okunabilir.
/// Singleton olarak register edilir.
/// </summary>
public class CockpitCacheWarmerState
{
    public DateTime? LastRefreshAt { get; set; }
    public int LastRefreshDurationMs { get; set; }
    public long RefreshCount; // Interlocked field — property değil
    public long FailureCount; // Interlocked field — property değil
    public string? LastError { get; set; }
    public DateTime? LastErrorAt { get; set; }
    public int LastTaskFailures { get; set; } // son cycle'da kaç SP fail etti
}

/// <summary>
/// Arka planda CockpitController cache'ini hep sıcak tutar.
/// - Startup'tan 5 sn sonra ilk warm-up (DB migration tamamlansın diye)
/// - Sonra her 4 dk'da bir refresh (TTL 15 dk → her zaman buffer'lı)
/// - Uykuya giden kullanıcı cold path'e asla düşmez; ilk sayfa açılışı ~50ms
/// </summary>
public class CockpitCacheWarmer : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CockpitCacheWarmer> _logger;
    private readonly CockpitCacheWarmerState _state;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(4);

    public CockpitCacheWarmer(
        IServiceScopeFactory scopeFactory,
        ILogger<CockpitCacheWarmer> logger,
        CockpitCacheWarmerState state)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _state = state;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var startedAt = DateTime.UtcNow;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MskDbContext>>();
                var cache = scope.ServiceProvider.GetRequiredService<IMemoryCache>();
                var hedefService = scope.ServiceProvider.GetRequiredService<IHedefService>();
                var taskFailures = 0;

                // forceRefresh=true → cache bypass, DB'den taze data
                // Bu kritik task — fail olursa cycle başarısız sayılır, exception fırlatılır
                await CockpitController.LoadAllCachedDataAsync(contextFactory, cache, hedefService, forceRefresh: true);

                // SP cache'lerini ısıt — sabit tarihli sorgular
                var cockpitData = scope.ServiceProvider.GetRequiredService<ICockpitDataService>();
                var now = DateTime.Now;
                var bugun = now.Date;
                var today = bugun.AddDays(1).AddSeconds(-1);
                var ayBas = new DateTime(now.Year, now.Month, 1);
                var aySon = new DateTime(now.Year, now.Month, DateTime.DaysInMonth(now.Year, now.Month), 23, 59, 59);
                var ytdBas = new DateTime(now.Year, 1, 1);
                var dow = bugun.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)bugun.DayOfWeek - 1;
                var haftaBas = bugun.AddDays(-dow);
                var haftaSon = haftaBas.AddDays(4).AddHours(23).AddMinutes(59).AddSeconds(59);
                var gecenBas = haftaBas.AddDays(-7);
                var gecenSon = gecenBas.AddDays(4).AddHours(23).AddMinutes(59).AddSeconds(59);

                // Sabit SP'ler (üst kartlar + CEI)
                var fixedTasks = new List<Task>
                {
                    cockpitData.GetFaturaOzetAsync(ayBas, aySon),
                    cockpitData.GetFaturaOzetAsync(ytdBas, today),
                    cockpitData.GetTahsilatOzetAsync(gecenBas, gecenSon),
                    cockpitData.GetTahsilatOzetAsync(haftaBas, haftaSon),
                    cockpitData.GetTahsilatOzetAsync(ayBas, aySon),
                    // Tahsilat YTD payda: ay sonuna kadar (CockpitController ile aynı semantik)
                    cockpitData.GetTahsilatOzetAsync(ytdBas, aySon)
                };

                // Tüm pill-nav filtre dönemlerini de ısıt — PreloadAllFilters anında dönebilsin
                var year = now.Year;
                var lmMonth = now.Month == 1 ? 12 : now.Month - 1;
                var lmYear = now.Month == 1 ? year - 1 : year;
                var filterPeriods = new (DateTime s, DateTime e)[]
                {
                    (ayBas, aySon), // month
                    (new DateTime(lmYear, lmMonth, 1), new DateTime(lmYear, lmMonth, DateTime.DaysInMonth(lmYear, lmMonth), 23,59,59)), // lastmonth
                    (new DateTime(year,1,1), new DateTime(year,3,31,23,59,59)),   // q1
                    (new DateTime(year,4,1), new DateTime(year,6,30,23,59,59)),   // q2
                    (new DateTime(year,7,1), new DateTime(year,9,30,23,59,59)),   // q3
                    (new DateTime(year,10,1), new DateTime(year,12,31,23,59,59)), // q4
                    (ytdBas, today) // ytd
                };
                foreach (var (s, e) in filterPeriods)
                {
                    fixedTasks.Add(cockpitData.GetFaturaOzetAsync(s, e));
                    fixedTasks.Add(cockpitData.GetTahsilatOzetAsync(s, e));
                    fixedTasks.Add(cockpitData.GetSozlesmeOzetAsync(s, e));
                    fixedTasks.Add(cockpitData.GetFaturalarAsync(s, e));

                    // FırsatAnaliz prev dönem karşılaştırması için geçen dönem SP'lerini de ısıt
                    var prevDur = e - s;
                    var prevS = s.AddDays(-prevDur.TotalDays);
                    var prevE = s.AddSeconds(-1);
                    fixedTasks.Add(cockpitData.GetFaturaOzetAsync(prevS, prevE));
                }

                // GetMonthlyBreakdown 12 ay × 2 SP = 24 paralel çağrı içerir.
                // Endpoint'in kendi 5dk cache'ini ısıt — kullanıcı GetMonthlyBreakdown çağırırsa cache hit.
                // Cold path 9s'den ~ms'ye düşer.
                for (int m = 1; m <= 12; m++)
                {
                    var ms = new DateTime(year, m, 1);
                    var me = new DateTime(year, m, DateTime.DaysInMonth(year, m), 23, 59, 59);
                    fixedTasks.Add(cockpitData.GetFaturaOzetAsync(ms, me));
                    fixedTasks.Add(cockpitData.GetTahsilatOzetAsync(ms, me));
                }

                // Her task'ı bağımsız bekle — biri timeout/hata atarsa diğerleri etkilenmesin
                var wrapped = fixedTasks.Select(async t =>
                {
                    try { await t; }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref taskFailures);
                        _logger.LogWarning(ex, "CacheWarmer: bir SP refresh task'ı fail etti, diğerleri devam ediyor");
                    }
                });
                await Task.WhenAll(wrapped);

                // GetMonthlyBreakdown endpoint cache'ini ısıt — controller direkt çağrılır.
                // SP'ler yukarıda preload edildi, bu çağrı sadece in-memory aggregate yapar.
                try
                {
                    var cockpitController = ActivatorUtilities.CreateInstance<SOS.Controllers.CockpitController>(scope.ServiceProvider);
                    await cockpitController.GetMonthlyBreakdown();
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref taskFailures);
                    _logger.LogWarning(ex, "CacheWarmer: GetMonthlyBreakdown preload fail (fatal değil)");
                }

                // NOT: FirsatAnaliz preload artık FirsatAnalizStartupWarmer (HostedService) tarafından yapılır.

                var elapsed = DateTime.UtcNow - startedAt;
                _state.LastRefreshAt = DateTime.UtcNow;
                _state.LastRefreshDurationMs = (int)elapsed.TotalMilliseconds;
                _state.LastTaskFailures = taskFailures;
                Interlocked.Increment(ref _state.RefreshCount);
                _logger.LogInformation("Cockpit cache refreshed in {ElapsedMs}ms ({Failures} task fail, toplam refresh: {Count})",
                    _state.LastRefreshDurationMs, taskFailures, _state.RefreshCount);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _state.FailureCount++;
                _state.LastError = ex.Message;
                _state.LastErrorAt = DateTime.UtcNow;
                _logger.LogError(ex, "Cockpit cache warmer refresh failed — will retry in {Interval}", RefreshInterval);
            }

            try
            {
                await Task.Delay(RefreshInterval, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }
}
