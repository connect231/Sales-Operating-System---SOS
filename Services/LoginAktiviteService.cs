using Microsoft.EntityFrameworkCore;
using SOS.DbData;
using SOS.Models.MsK;

namespace SOS.Services;

public interface ILoginAktiviteService
{
    Task<long?> RecordLoginAsync(int kullaniciId, string? email, string? adSoyad, string? ipAdresi, string? userAgent);
    Task RecordHeartbeatAsync(int kullaniciId);
    Task RecordLogoutAsync(int kullaniciId);
    Task SweepStaleSessionsAsync(int timeoutDakika = 15);
}

/// <summary>
/// Kullanıcı login/logout/heartbeat aktivitelerini TBLSOS_LOGIN_AKTIVITE tablosuna yazar.
/// - Login → yeni satır (AktifMi=1, GirisZamani=now)
/// - Heartbeat → SonAktiviteZamani + SureSaniye güncelle (60sn'de bir AJAX)
/// - Logout → CikisZamani + SureSaniye final + AktifMi=0
/// </summary>
public class LoginAktiviteService : ILoginAktiviteService
{
    private readonly IDbContextFactory<MskDbContext> _contextFactory;
    private readonly ILogger<LoginAktiviteService> _logger;

    public LoginAktiviteService(IDbContextFactory<MskDbContext> contextFactory, ILogger<LoginAktiviteService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<long?> RecordLoginAsync(int kullaniciId, string? email, string? adSoyad, string? ipAdresi, string? userAgent)
    {
        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync();

            // Aynı kullanıcının açık kalmış eski oturumlarını kapat (yeni login geldi)
            var now = DateTime.Now;
            var openOnes = await db.TBLSOS_LOGIN_AKTIVITEs
                .Where(x => x.KullaniciId == kullaniciId && x.AktifMi)
                .ToListAsync();
            foreach (var o in openOnes)
            {
                o.AktifMi = false;
                o.CikisZamani = o.SonAktiviteZamani;
                o.SureSaniye = (int)Math.Max(0, (o.SonAktiviteZamani - o.GirisZamani).TotalSeconds);
            }

            var entity = new TBLSOS_LOGIN_AKTIVITE
            {
                KullaniciId = kullaniciId,
                Email = Truncate(email, 256),
                AdSoyad = Truncate(adSoyad, 256),
                GirisZamani = now,
                SonAktiviteZamani = now,
                SureSaniye = 0,
                AktifMi = true,
                IPAdresi = Truncate(ipAdresi, 64),
                UserAgent = Truncate(userAgent, 512)
            };
            db.TBLSOS_LOGIN_AKTIVITEs.Add(entity);
            await db.SaveChangesAsync();
            return entity.Id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RecordLoginAsync failed for user {Uid}", kullaniciId);
            return null;
        }
    }

    public async Task RecordHeartbeatAsync(int kullaniciId)
    {
        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync();
            var open = await db.TBLSOS_LOGIN_AKTIVITEs
                .Where(x => x.KullaniciId == kullaniciId && x.AktifMi)
                .OrderByDescending(x => x.GirisZamani)
                .FirstOrDefaultAsync();
            if (open == null) return;

            var now = DateTime.Now;
            open.SonAktiviteZamani = now;
            open.SureSaniye = (int)Math.Max(0, (now - open.GirisZamani).TotalSeconds);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RecordHeartbeatAsync failed for user {Uid}", kullaniciId);
        }
    }

    public async Task RecordLogoutAsync(int kullaniciId)
    {
        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync();
            var open = await db.TBLSOS_LOGIN_AKTIVITEs
                .Where(x => x.KullaniciId == kullaniciId && x.AktifMi)
                .OrderByDescending(x => x.GirisZamani)
                .FirstOrDefaultAsync();
            if (open == null) return;

            var now = DateTime.Now;
            open.CikisZamani = now;
            open.SonAktiviteZamani = now;
            open.SureSaniye = (int)Math.Max(0, (now - open.GirisZamani).TotalSeconds);
            open.AktifMi = false;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RecordLogoutAsync failed for user {Uid}", kullaniciId);
        }
    }

    public async Task SweepStaleSessionsAsync(int timeoutDakika = 15)
    {
        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync();
            var threshold = DateTime.Now.AddMinutes(-timeoutDakika);
            var stale = await db.TBLSOS_LOGIN_AKTIVITEs
                .Where(x => x.AktifMi && x.SonAktiviteZamani < threshold)
                .ToListAsync();
            foreach (var s in stale)
            {
                s.AktifMi = false;
                s.CikisZamani = s.SonAktiviteZamani;
                s.SureSaniye = (int)Math.Max(0, (s.SonAktiviteZamani - s.GirisZamani).TotalSeconds);
            }
            if (stale.Count > 0) await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SweepStaleSessionsAsync failed");
        }
    }

    private static string? Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Length <= max ? s : s.Substring(0, max);
    }
}
