# SOS — Sales Operating System

Finansal cockpit dashboard uygulaması. ASP.NET Core MVC (.NET 10), Razor Views, Tailwind CSS, SQL Server.

## Proje Amacı
Satış ekibinin fatura, tahsilat, sözleşme ve hedef takibini tek ekrandan yapabilmesi. Apple-kalitesinde 60fps UI.

## Teknoloji
- **Backend:** ASP.NET Core MVC, Entity Framework Core, IDbContextFactory (thread-safety), IMemoryCache (5 dk TTL), SemaphoreSlim
- **Frontend:** Razor Views, Tailwind CDN, vanilla JS (no React/Vue), requestAnimationFrame animasyonlar
- **DB:** SQL Server `10.135.140.17\yazdes` / `UNIVERA_CUSTOMER_PORTAL`
- **Target:** net10.0

## Kritik Mimari Kararlar

### DbContext Pattern
`AddDbContext` + `AddDbContextFactory(ServiceLifetime.Scoped)` birlikte kullanılıyor.
- `AddDbContext` → ClaimsFactory, LogService gibi scoped servisler için
- `IDbContextFactory` → CockpitController'da `using var db = _contextFactory.CreateDbContext()` ile bağımsız context

### Tahsilat Tarih Mantığı
**Efektif tarih** = `Odeme_Sozu_Tarihi ?? Fatura_Vade_Tarihi`
- Ödeme sözü tarihi varsa KESİNLİKLE ona bak, vade tarihine bakma
- Fatura kartı → `Fatura_Tarihi` bazlı
- Tahsilat kartı → efektif tarih bazlı
- Vadesi geçmiş → efektif tarih < dönem başı VE bakiye > 0
- İade/Ret durumlu faturalar tahsilat hesaplarından HARİÇ

### Hedef Sistemi (DB bazlı)
- `TBLSOS_HEDEF_AYLIK` → aylık hedefler (ay bazlı farklı tutarlar, toplam ₺600M/yıl)
- `TBLSOS_ANA_URUN` → 8 ana ürün kategorisi (Enroute, Stokbar, Quest, ServiceCore, Varuna, Hosting, E-Dönüşüm, BFG)
- `TBLSOS_URUN_ESLESTIRME` → StockCode → Ana ürün eşleşmesi (145 kayıt, Excel onaylı)
- Hardcoded `AYLIK_HEDEF` YOK — tümü DB'den

### Ürün Eşleşme Zinciri
```
Fatura.Fatura_No → TBL_VARUNA_SIPARIS.SerialNumber → .OrderId
  → TBL_VARUNA_SIPARIS_URUNLERI.CrmOrderId → .StockCode
  → TBLSOS_URUN_ESLESTIRME → TBLSOS_ANA_URUN
```
Eşleşme satır bazlı (mask bazlı değil) — aynı mask farklı ana ürüne gidebilir.

### Tahsilat Kartı Gösterimi
- **Büyük tutar** = `SUM(Fatura_Toplam)` — dönemdeki toplam
- **Tahsil edilen** = `SUM(Tahsil_Edilen)`
- **Kalan** = `SUM(Bekleyen_Bakiye)`

### Filtreler
- Pill-nav: Bu ay, Geçen ay, 1-4. Çeyrek, YTD
- Dinamik tarih: başlangıç/bitiş date picker + Uygula butonu
- Bu ay/çeyrekler tam dönem (bugüne kısıtlanmaz)
- AJAX ile filtre değişiminde tüm kartlar güncellenir (sayfa reload yok)

## DEV Mode
- Login şifresiz — `AccountController.Login GET` ilk kullanıcıyı otomatik giriş yapar
- Production'da `PasswordCheck` yeniden aktif edilmeli

## Migration Sistemi
`DatabaseMigrationService.cs` — raw SQL ile IF NOT EXISTS pattern. EF Migration kullanılmıyor.
Yeni tablo eklerken buraya eklenir, uygulama başlangıcında otomatik çalışır.

## Türkçe UI Kuralları
- "Q2" yerine "2. Çeyrek"
- İlk harf büyük, hepsi büyük OLMAZ
- Tarihler dd.MM.yyyy formatı
- Para birimi: ₺ prefix, N0 format (kuruş gösterilmez)

## Dosya Yapısı
- `Controllers/CockpitController.cs` — ana dashboard, single-pass metrics, AJAX endpoints
- `Views/Cockpit/Index.cshtml` — dashboard UI, JS AJAX callback
- `Views/Shared/_Layout.cshtml` — global CSS/JS, sidebar, Apple-quality rendering
- `Models/ViewModels/CockpitViewModel.cs` — dashboard ViewModel
- `Services/DatabaseMigrationService.cs` — tablo oluşturma + seed
- `DbData/MskDbContext.cs` — EF DbSets
