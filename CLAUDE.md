# SOS — Sales Operating System

Finansal cockpit dashboard uygulaması. ASP.NET Core MVC (.NET 10), Razor Views, Tailwind CSS, SQL Server.

**Proje amacı:** Satış ekibinin fatura, tahsilat, sözleşme ve hedef takibini tek ekrandan yapması. Apple-kalitesinde 60fps UI.

## Teknoloji

- **Backend:** ASP.NET Core MVC, Entity Framework Core, `IDbContextFactory` (thread-safety), `IMemoryCache` (5 dk TTL), `SemaphoreSlim`.
- **Frontend:** Razor Views, Tailwind CDN, vanilla JS (no React/Vue), `requestAnimationFrame` animasyonlar.
- **DB:** SQL Server `10.135.140.17\yazdes` / `UNIVERA_CUSTOMER_PORTAL`.
- **Target:** `net10.0`.

## Ne Üzerinde Çalışıyorsan, Önce Bu Belgeyi Oku

| Konu | Belge |
|---|---|
| Fatura tutarı, ürün dağılımı, Varuna eşleşmesi | [Docs/architecture/01-veri-akisi.md](Docs/architecture/01-veri-akisi.md) |
| SP'ler, cache, `ICockpitDataService` | [Docs/architecture/02-stored-procedures.md](Docs/architecture/02-stored-procedures.md) |
| Tahakkuk (SAP bazlı tarih override) | [Docs/architecture/03-tahakkuk.md](Docs/architecture/03-tahakkuk.md) |
| Tahsilat, vade hesabı, CEI | [Docs/architecture/04-tahsilat-cei.md](Docs/architecture/04-tahsilat-cei.md) |
| Hedefler, ana ürünler, ürün eşleşme | [Docs/architecture/05-hedef-sistemi.md](Docs/architecture/05-hedef-sistemi.md) |
| Fırsat Analiz / Cockpit tutarlılığı | [Docs/architecture/06-firsat-analiz.md](Docs/architecture/06-firsat-analiz.md) |
| Türkçe UI, locale, format kuralları | [Docs/conventions/ui-locale-tr.md](Docs/conventions/ui-locale-tr.md) |
| İade/Ret, hukuki takip, deduplikasyon | [Docs/conventions/data-rules.md](Docs/conventions/data-rules.md) |
| Build, agent rehberi, yasaklar | [AGENTS.md](AGENTS.md) |
| Agent rota haritası, /sos-yap akışı | [Docs/agent-rota-haritasi.md](Docs/agent-rota-haritasi.md) |
| Teknik borç, ileride yapılacaklar | [TODO.md](TODO.md) |

> **Kural:** Bir konuyu birden çok yerde tutma. CLAUDE.md sadece üst seviye yönlendirme + çekirdek invariant'lar içerir. Detay her zaman `Docs/` altında.

## Çekirdek Mimari Invariant'lar

> Bunlar projenin temel taşları — değişmesi tüm sistemi etkiler. Değiştirmeden önce mutlaka konuş.

### DbContext Pattern
`AddDbContext` + `AddDbContextFactory(ServiceLifetime.Scoped)` birlikte kullanılır.
- `AddDbContext` → ClaimsFactory, LogService gibi scoped servisler için.
- `IDbContextFactory` → CockpitController vb. için: `using var db = _contextFactory.CreateDbContext()`.

### Stored Procedure Tek Kaynak
Dashboard kartları **SP'lerden** beslenir. Hem Cockpit hem Fırsat Analiz aynı `ICockpitDataService` üzerinden SP çağırır. Detay: [02-stored-procedures.md](Docs/architecture/02-stored-procedures.md).

### Tahakkuk Override Tüm Raporlamada
Tüm raporlama (fatura, tahsilat, CEI, YTD, Fırsat Analiz) `EfektifFaturaTarihi` üzerinden çalışır. Detay: [03-tahakkuk.md](Docs/architecture/03-tahakkuk.md).

### Migration Sistemi
`Services/DatabaseMigrationService.cs` — raw SQL ile `IF NOT EXISTS` pattern. **EF Migration kullanılmıyor.** Yeni tablo eklerken buraya eklenir, uygulama başlangıcında otomatik çalışır.

### DEV Mode
Login şifresiz — `AccountController.Login GET` ilk kullanıcıyı otomatik giriş yapar. Production'da `PasswordCheck` yeniden aktif edilmeli.

### UI Casing — UPPERCASE Yasak
Sistemin **hiçbir yerinde** ALL CAPS / UPPERCASE Türkçe metin kullanılmaz. Yasak: `text-transform: uppercase`, Tailwind `uppercase` class'ı, sabit string'lerde `"GERÇEKLEŞEN"` gibi büyük harfli yazım. Kullan: **Title case** ("Bu Ay Hedef") veya **Sentence case** ("Gerçekleşen", "Kalan iş günü"). Detay ve örnekler: [Docs/conventions/ui-locale-tr.md](Docs/conventions/ui-locale-tr.md).

## Ana Dosyalar

- `Controllers/CockpitController.cs` — ana dashboard, AJAX endpoints, `LoadAllCachedDataAsync` (ortak veri kaynağı).
- `Controllers/FirsatAnalizController.cs` — fırsat hunisi, opportunity bazlı analiz.
- `Services/CockpitDataService.cs` — SP çağrı katmanı.
- `Services/TahakkukService.cs` — tahakkuk map cache + invalidate.
- `Services/HedefService.cs` — hedef tabloları.
- `Services/DatabaseMigrationService.cs` — tablo + SP DDL.
- `Views/Cockpit/Index.cshtml`, `Views/FirsatAnaliz/Index.cshtml`, `Views/Shared/_Layout.cshtml`.
- `DbData/MskDbContext.cs` — EF DbSets.

## Doküman Eklerken / Güncellerken

- **Yeni modül belgesi:** `Docs/architecture/NN-<konu>.md` (numara sırayla).
- **Yeni convention:** `Docs/conventions/<konu>.md`.
- Yeni belgeyi yukarıdaki yönlendirme tablosuna **mutlaka ekle** — keşfedilebilir olmalı.
- CLAUDE.md kısa kalmalı; detay belgenin kendisinde.
