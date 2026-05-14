using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SOS.DbData;
using SOS.Models;
using SOS.Models.Kullanici;
using SOS.Services;


var builder = WebApplication.CreateBuilder(args);

// User secrets — sadece Development'ta aktif (üretimde env vars / Azure Key Vault)
// UserSecretsId string kullanıyoruz çünkü top-level Program için <T> overload attribute okumayabilir
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets("sos-sales-operating-system-2026");
}

// Add services to the container.
builder.Services.AddTransient<IEmailService, SmtpEmailService>();
var mvcBuilder = builder.Services.AddControllersWithViews();
mvcBuilder.AddRazorRuntimeCompilation(options =>
{
    // .NET 10: ref assembly'lerini packs dizininden ekle
    var dotnetRoot = System.Environment.GetEnvironmentVariable("DOTNET_ROOT")
        ?? System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), "dotnet");
    var tfm = $"net{System.Environment.Version.Major}.0";
    foreach (var packName in new[] { "Microsoft.NETCore.App.Ref", "Microsoft.AspNetCore.App.Ref" })
    {
        var packBase = System.IO.Path.Combine(dotnetRoot, "packs", packName);
        if (!System.IO.Directory.Exists(packBase)) continue;
        var latest = System.IO.Directory.GetDirectories(packBase)
            .Select(d => new { Path = d, Ver = System.IO.Path.GetFileName(d) })
            .Where(x => System.Version.TryParse(x.Ver, out _))
            .OrderByDescending(x => new System.Version(x.Ver))
            .FirstOrDefault();
        if (latest == null) continue;
        var refDir = System.IO.Path.Combine(latest.Path, "ref", tfm);
        if (!System.IO.Directory.Exists(refDir)) continue;
        foreach (var dll in System.IO.Directory.GetFiles(refDir, "*.dll"))
            options.AdditionalReferencePaths.Add(dll);
    }
    // Projenin kendi assembly'sini ve bağımlılıklarını ekle
    var appDir = AppContext.BaseDirectory;
    foreach (var dll in System.IO.Directory.GetFiles(appDir, "*.dll"))
        options.AdditionalReferencePaths.Add(dll);
});
builder.Services.AddScoped<ICompanyResolutionService, CompanyResolutionService>();
builder.Services.AddScoped<IDatabaseMigrationService, DatabaseMigrationService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IUrlEncryptionService, UrlEncryptionService>();
builder.Services.AddScoped<ILogService, DbLogService>();
builder.Services.AddScoped<ILoginAktiviteService, LoginAktiviteService>();
builder.Services.AddMemoryCache();
builder.Services.AddResponseCaching();
// Tahakkuk override servisi (Cockpit + FirsatAnaliz tarafından paylaşılır)
builder.Services.AddScoped<ITahakkukService, TahakkukService>();
builder.Services.AddScoped<ICockpitDataService, CockpitDataService>();
// 2026 Hedef sistemi (600M senaryo) — bant + karne sayfası
builder.Services.AddScoped<IHedefService, HedefService>();
// SOS-özel admin yetkilendirme: TBLSOS_ADMIN_KULLANICI tablosundaki email'ler tüm menülere erişir
builder.Services.AddScoped<IAdminAuthService, AdminAuthService>();
// Cockpit cache warmer — app startup'tan sonra her 4 dakikada cache'i DB'den yeniler
builder.Services.AddSingleton<CockpitCacheWarmerState>();
// DEV (localhost): 34 SP paralel preload bu makinede DB'yi boğuyor, lazy-cache yeterli
// builder.Services.AddHostedService<CockpitCacheWarmer>();
// FirsatAnaliz startup warmer — SQL page cache + 5 kritik endpoint (Bu Ay) preload, bir kez çalışır.
// Cold call 147s maliyetini arka plana iter; kullanıcı login sırasında ısınır.
if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddHostedService<FirsatAnalizStartupWarmer>();
}
// Performance Optimization: Response Compression
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
});

builder.Services.AddDbContextPool<DataContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    options.UseSqlServer(connectionString)
           .ConfigureWarnings(w => w.Ignore(SqlServerEventId.DecimalTypeDefaultWarning));
});
// MskDbContext: Normal scoped (ClaimsFactory, LogService vb. için)
// ConfigureWarnings: scaffolded model'lerde decimal precision tanımlı değil — DB sütun tipleri zaten doğru, runtime warning'i suppress et
builder.Services.AddDbContext<MskDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("MsKConnection"))
           .ConfigureWarnings(w => w.Ignore(SqlServerEventId.DecimalTypeDefaultWarning)));
// Factory: CockpitController kendi bağımsız context'ini oluşturur (concurrent access fix)
builder.Services.AddDbContextFactory<MskDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("MsKConnection"))
           .ConfigureWarnings(w => w.Ignore(SqlServerEventId.DecimalTypeDefaultWarning)),
    ServiceLifetime.Scoped);


builder.Services.AddIdentity<AppUser, AppRole>(options => { 
    options.SignIn.RequireConfirmedEmail = true; 

}).AddEntityFrameworkStores<DataContext>().AddDefaultTokenProviders()
  .AddErrorDescriber<SOS.Services.CustomIdentityErrorDescriber>()
  .AddClaimsPrincipalFactory<SOS.Services.CustomUserClaimsPrincipalFactory>();

builder.Services.Configure<IdentityOptions>(options =>
{
    // Strengthened password policy (Sprint 1.3)
    options.Password.RequiredLength = 8;
    options.Password.RequireNonAlphanumeric = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireDigit = true;

    options.User.RequireUniqueEmail = true;
    // options.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyz0123456789";

    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(3);
});

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromMinutes(60);
    options.SlidingExpiration = true;
});



var app = builder.Build();

// Run custom database migrations at startup (Sprint 2.3)
using (var scope = app.Services.CreateScope())
{
    var migrationService = scope.ServiceProvider.GetRequiredService<IDatabaseMigrationService>();
    await migrationService.ApplyCustomMigrationsAsync();
}

// Enforce Turkish Culture (tr-TR) for currency and date formatting
var cultureInfo = new System.Globalization.CultureInfo("tr-TR");
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseResponseCompression(); // Optimized Placement
app.UseResponseCaching();
// HTTPS redirect sadece production'da — dev launchSettings yalnız HTTP profili açıyor
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// Basic Content Security Policy
app.Use(async (context, next) =>
{
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline' 'unsafe-eval' https://cdn.jsdelivr.net https://unpkg.com https://cdn.tailwindcss.com; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdn.jsdelivr.net https://unpkg.com https://cdn.tailwindcss.com; font-src 'self' https://fonts.gstatic.com https://cdn.jsdelivr.net; img-src 'self' data: https:; connect-src 'self' https://cdn.jsdelivr.net;";
    await next();
});

// app.MapStaticAssets();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // Cache static files for 30 days
        const int durationInSeconds = 60 * 60 * 24 * 30;
        ctx.Context.Response.Headers["Cache-Control"] = "public,max-age=" + durationInSeconds;
    }
});

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Login}/{id?}")
    .WithStaticAssets();

app.Run();
