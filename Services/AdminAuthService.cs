using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SOS.DbData;
using SOS.Models.MsK;

namespace SOS.Services;

/// <summary>
/// SOS uygulamasına özel admin yetkilendirme servisi.
/// `TBLSOS_ADMIN_KULLANICI` tablosundaki aktif email'leri kontrol eder.
/// 5 dk in-memory cache — admin değişimi sonrası `InvalidateCache()` çağrılmalı.
/// </summary>
public interface IAdminAuthService
{
    Task<bool> IsAdminAsync(string? email);
    Task<List<TBLSOS_ADMIN_KULLANICI>> GetAllAsync();
    Task<TBLSOS_ADMIN_KULLANICI?> AddAsync(string email, string? adSoyad, string? ekleyenEmail);
    Task<bool> RemoveAsync(int id);
    Task<bool> SetAktifAsync(int id, bool aktif);
    void InvalidateCache();
}

public class AdminAuthService : IAdminAuthService
{
    private readonly IDbContextFactory<MskDbContext> _contextFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AdminAuthService> _logger;
    private static readonly TimeSpan CacheTTL = TimeSpan.FromMinutes(5);
    private const string CacheKey = "sos_admin_emails_v1";

    public AdminAuthService(
        IDbContextFactory<MskDbContext> contextFactory,
        IMemoryCache cache,
        ILogger<AdminAuthService> logger)
    {
        _contextFactory = contextFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<bool> IsAdminAsync(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        var set = await GetAdminEmailSetAsync();
        return set.Contains(email.Trim().ToLowerInvariant());
    }

    private async Task<HashSet<string>> GetAdminEmailSetAsync()
    {
        if (_cache.TryGetValue(CacheKey, out HashSet<string>? cached) && cached != null)
            return cached;

        using var db = _contextFactory.CreateDbContext();
        var emails = await db.TBLSOS_ADMIN_KULLANICIs
            .AsNoTracking()
            .Where(a => a.Aktif)
            .Select(a => a.Email.ToLower())
            .ToListAsync();

        var set = new HashSet<string>(emails, StringComparer.OrdinalIgnoreCase);
        _cache.Set(CacheKey, set, CacheTTL);
        return set;
    }

    public async Task<List<TBLSOS_ADMIN_KULLANICI>> GetAllAsync()
    {
        using var db = _contextFactory.CreateDbContext();
        return await db.TBLSOS_ADMIN_KULLANICIs
            .AsNoTracking()
            .OrderByDescending(a => a.Aktif)
            .ThenBy(a => a.Email)
            .ToListAsync();
    }

    public async Task<TBLSOS_ADMIN_KULLANICI?> AddAsync(string email, string? adSoyad, string? ekleyenEmail)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var normalized = email.Trim().ToLowerInvariant();

        using var db = _contextFactory.CreateDbContext();

        // Var mı? Pasifse aktive et, yoksa oluştur
        var existing = await db.TBLSOS_ADMIN_KULLANICIs
            .FirstOrDefaultAsync(a => a.Email.ToLower() == normalized);

        if (existing != null)
        {
            if (!existing.Aktif)
            {
                existing.Aktif = true;
                existing.EkleyenEmail = ekleyenEmail;
                existing.EklenmeTarihi = DateTime.Now;
                if (!string.IsNullOrWhiteSpace(adSoyad)) existing.AdSoyad = adSoyad;
                await db.SaveChangesAsync();
            }
            InvalidateCache();
            return existing;
        }

        var item = new TBLSOS_ADMIN_KULLANICI
        {
            Email = normalized,
            AdSoyad = string.IsNullOrWhiteSpace(adSoyad) ? null : adSoyad.Trim(),
            Aktif = true,
            EklenmeTarihi = DateTime.Now,
            EkleyenEmail = ekleyenEmail
        };
        db.TBLSOS_ADMIN_KULLANICIs.Add(item);
        await db.SaveChangesAsync();
        InvalidateCache();
        _logger.LogInformation("Admin eklendi: {Email} (ekleyen: {Ekleyen})", normalized, ekleyenEmail);
        return item;
    }

    public async Task<bool> RemoveAsync(int id)
    {
        using var db = _contextFactory.CreateDbContext();
        var item = await db.TBLSOS_ADMIN_KULLANICIs.FirstOrDefaultAsync(a => a.Id == id);
        if (item == null) return false;
        db.TBLSOS_ADMIN_KULLANICIs.Remove(item);
        await db.SaveChangesAsync();
        InvalidateCache();
        _logger.LogInformation("Admin silindi: {Email}", item.Email);
        return true;
    }

    public async Task<bool> SetAktifAsync(int id, bool aktif)
    {
        using var db = _contextFactory.CreateDbContext();
        var item = await db.TBLSOS_ADMIN_KULLANICIs.FirstOrDefaultAsync(a => a.Id == id);
        if (item == null) return false;
        item.Aktif = aktif;
        await db.SaveChangesAsync();
        InvalidateCache();
        return true;
    }

    public void InvalidateCache() => _cache.Remove(CacheKey);
}
