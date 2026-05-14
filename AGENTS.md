# SOS Agent Operasyonel Kuralları

Bu dosya `CLAUDE.md`'yi tamamlar. CLAUDE.md proje özetini ve **konu → belge** yönlendirmesini verir; mimari/iş mantığı detayları `Docs/architecture/` ve `Docs/conventions/` altındadır. Bu dosya **operasyonel guardrail'leri** (komutlar, "yapma" listeleri, agent seçim rehberi) içerir.

> İlgili detay belgeler:
> - Veri akışı / fatura: [Docs/architecture/01-veri-akisi.md](Docs/architecture/01-veri-akisi.md)
> - SP / cache: [Docs/architecture/02-stored-procedures.md](Docs/architecture/02-stored-procedures.md)
> - Tahakkuk: [Docs/architecture/03-tahakkuk.md](Docs/architecture/03-tahakkuk.md)
> - Tahsilat / CEI: [Docs/architecture/04-tahsilat-cei.md](Docs/architecture/04-tahsilat-cei.md)
> - Hedef sistemi: [Docs/architecture/05-hedef-sistemi.md](Docs/architecture/05-hedef-sistemi.md)
> - Fırsat Analiz: [Docs/architecture/06-firsat-analiz.md](Docs/architecture/06-firsat-analiz.md)
> - UI / locale: [Docs/conventions/ui-locale-tr.md](Docs/conventions/ui-locale-tr.md)
> - Veri kuralları: [Docs/conventions/data-rules.md](Docs/conventions/data-rules.md)

## Build / Run / Test Komutları

```bash
# Build
dotnet build SOS.csproj

# Çalıştır (default: http://localhost:5165)
dotnet run --project SOS.csproj

# Background'da çalıştır (agent kullanımı)
bg_shell start command="dotnet run --project SOS.csproj" type=server ready_port=5165 group=sos

# Sıfır warning hedefi — bu çıktıyı her PR öncesi temizle
dotnet build SOS.csproj /warnaserror
```

## Önerilen Giriş Noktası: `/sos-yap`

Doğal dilde istek için **`/sos-yap "<istek>"`** orkestratörünü kullan. Otomatik:
- Bağlam yükler (CLAUDE.md + bu dosya + ilgili `Docs/architecture/*`)
- Doğru subagent'ı seçer (aşağıdaki tabloya göre)
- Doğrulama zincirini koşturur (build, lsp, auditor, screenshot, /security-review, /review)
- Tek konsolide rapor döner

Görsel akış ve örnekler: [Docs/agent-rota-haritasi.md](Docs/agent-rota-haritasi.md).

Manuel subagent çağırmak istersen aşağıdaki tabloyu kullanabilirsin.

## Hangi Agent Ne İçin?

| Görev | Doğru Agent | İlgili Belge |
|---|---|---|
| CockpitController'a yeni endpoint eklemek | `dotnet-cockpit-engineer` | [02-stored-procedures.md](Docs/architecture/02-stored-procedures.md) |
| Tahsilat hesabı neden tutmuyor? | `finans-hesaplama-auditor` (önce) → düzeltme için `dotnet-cockpit-engineer` | [04-tahsilat-cei.md](Docs/architecture/04-tahsilat-cei.md) + [data-rules.md](Docs/conventions/data-rules.md) |
| Fatura/ürün dağılımı tutmuyor | `finans-hesaplama-auditor` | [01-veri-akisi.md](Docs/architecture/01-veri-akisi.md) |
| Tahakkuk import / SAP override | `dotnet-cockpit-engineer` | [03-tahakkuk.md](Docs/architecture/03-tahakkuk.md) |
| Hedef tablosu / yeni hedef metriği | `dotnet-cockpit-engineer` | [05-hedef-sistemi.md](Docs/architecture/05-hedef-sistemi.md) |
| Fırsat Analiz tutarlılığı | `finans-hesaplama-auditor` | [06-firsat-analiz.md](Docs/architecture/06-firsat-analiz.md) |
| Yeni metrik kartı tasarımı | `razor-ui-polisher` | [ui-locale-tr.md](Docs/conventions/ui-locale-tr.md) |
| Yavaş sorgu / N+1 sorunu | `sql-ef-query-pro` | — |
| Yeni tablo / kolon / SP eklemek | `sql-ef-query-pro` (raw SQL `DatabaseMigrationService`'e ekler) | [02-stored-procedures.md](Docs/architecture/02-stored-procedures.md) |
| Genel araştırma / dosya keşfi | `Explore` | — |
| Bağımsız küçük iş | `general-purpose` | — |

## Pazarlıksız Yasaklar

1. **EF Migration ekleme.** `dotnet ef migrations add` çalıştırma. Şema değişikliği `Services/DatabaseMigrationService.cs` içine raw SQL.
2. **Hardcoded finansal sabit ekleme.** Aylık hedef, ürün listesi, eşleştirme — hepsi DB'den (`TBLSOS_*`).
3. **`AYLIK_HEDEF` constant** veya benzeri hardcoded para değeri kodda görünmez.
4. **CockpitController'a `MskDbContext` direkt enjekte etme.** Her zaman `IDbContextFactory<MskDbContext>` + `using var db = factory.CreateDbContext()`.
5. **DEV mode auto-login'i kaldırma.** `AccountController.Login GET` içindeki otomatik giriş bilinçli — sadece `// DEV:` yorumunu koru.
6. **İade/Ret faturaları tahsilat hesabına dahil etme.**
7. **Tahsilat tarih hesaplarında sadece `Fatura_Vade_Tarihi` kullan.** Ödeme sözü mantığı projeden tamamen kaldırıldı.
8. **"Diğer" / "Eşleşmemiş" kategorisi ürün kırılımına ekleme.** Varuna'da eşleşmeyen fatura ürün dağılımına girmez.
9. **Türkçe label'lara İngilizce karıştırma.** "Q1" → "1. Çeyrek".
10. **Kuruş gösterme.** `N0` format, `₺` prefix.
11. **jQuery / React / Vue / Alpine ekleme.** Vanilla JS.
12. **Sayfa reload ile filtre değiştirme.** AJAX zorunlu.
13. **UPPERCASE / ALL CAPS Türkçe metin kullanma.** `text-transform: uppercase`, Tailwind `uppercase` class'ı, sabit string'lerde "BU AY HEDEFİ" gibi büyük harfli yazım YASAK. Title case ("Bu Ay Hedef") veya sentence case ("Gerçekleşen") kullan. Detay: [Docs/conventions/ui-locale-tr.md](Docs/conventions/ui-locale-tr.md) — "Büyük / Küçük Harf Kuralı".

## Doğrulama Zorunlulukları

Herhangi bir kod değişikliği sonrası:

1. ✅ `dotnet build SOS.csproj` — sıfır error, sıfır yeni warning
2. ✅ `lsp diagnostics` — değişen tüm dosyalar temiz
3. ✅ Finansal hesap değiştiyse → `finans-hesaplama-auditor` agent'ı çağır (DB içi tutarlılık + SP/C# eşleşme denetimi)
4. ✅ UI değiştiyse → `bg_shell` ile uygulamayı çalıştır + `browser_screenshot` ile görsel doğrula

## Cache Davranışı

- Cache TTL **5 dakika**
- Yeni cache key sabiti `CockpitController` üst bloğundaki listeye eklenmeli
- `SemaphoreSlim _cacheLock` ile sarılmalı
- Yeni tablo → yeni cache key → preload pattern

## Doğruluk Kaynağı

Tek doğruluk kaynağı **canlı DB**: `TBL_VARUNA_SIPARIS` (Closed siparişler) + `VIEW_CP_EXCEL_FATURA` (tahsilat/hukuki alanları). Excel dosyası referans olarak kullanılmaz — Varuna gerçek zamanlı, müşteri portalı VIEW'u ise gecikmeli akabilir; sentetik fatura mekanizması bu gecikmeyi otomatik kapatır.

- Yıllık hedef: ₺600M (`TBLSOS_HEDEF_AYLIK` toplamı)
- Hedef ile gerçekleşmenin tutarlılığı `TBLSOS_*` tablolarından okunur, hardcoded sabit yok.

Dashboard tutar denetimi: SP çıktısı (`EXEC SP_COCKPIT_FATURA @Start, @End`) ile C# `LoadAllCachedDataAsync` sonucu **birebir** tutmalı. ₺1'in üzerinde sapma BUG'dır.

## Türkçe Locale Hatırlatması

```csharp
// Doğru
var fmt = new System.Globalization.CultureInfo("tr-TR");
amount.ToString("C0", fmt)  // ₺1.234.567

// Yanlış
amount.ToString("C0")  // OS locale'ine bağlı
```

```js
// Doğru
new Intl.NumberFormat('tr-TR', { style: 'currency', currency: 'TRY', maximumFractionDigits: 0 }).format(value)

// Yanlış
'$' + value.toFixed(2)
```

## Branch / Commit

- Türkçe commit mesajı kabul (proje stili).
- Mimari değişikliklerde ilgili `Docs/architecture/*.md` belgesi de güncellenmeli (kod ile belge senkron kalmalı).
- Yeni alan eklenirse: yeni `Docs/architecture/NN-<konu>.md` aç, `CLAUDE.md`'deki yönlendirme tablosuna ekle.
