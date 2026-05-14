# Stored Procedure Mimarisi

Dashboard kartları (fatura, tahsilat, sözleşme) **SP'lerden** beslenir. SP'ler tek kaynak — hem Cockpit hem Fırsat Analiz aynı `ICockpitDataService` üzerinden çağırır. **Bir SP değişince tüm ekranlar etkilenir.**

> Bu belgeyi oku eğer: yeni bir kart eklenecekse, SP içeriğini değiştireceksen, cache pattern'i değişecekse, "şu kart neden yavaş?" sorusu varsa.

## SP'ler

### SP_COCKPIT_FATURA(@Start, @End)
- VIEW ∩ Varuna(Closed) − İade/Ret + Tahakkuk sentetik
- **Satır bazlı döner:** `FaturaNo, EfektifTarih, NetTutar, Firma`
- Ürün dağılımı ve detay tablosu da bu SP'nin `FaturaNo` listesinden hesaplanır.

### SP_COCKPIT_TAHSILAT(@Start, @End)
- VIEW bazlı, İade/Ret hariç, Hukuki takip hariç.
- **Aggregate döner:** `TahsilEdilen, BekleyenBakiye, VadesiGelen`
- `Tahsil_Tarihi` bazlı PAY, `Fatura_Vade_Tarihi` bazlı PAYDA. Detay: `04-tahsilat-cei.md`.

### SP_COCKPIT_SOZLESME(@Start, @End)
- `FinishDate + 1` = yenileme ayı.
- Yeni sözleşme eski.Id'yi `RelatedContractId` ile referans gösteriyor (ters bağlantı, OUTER APPLY).
- **Hedef** = yeni sözleşme tutarı, **Gerçekleşen** = `Archived` olanlar.

## ICockpitDataService

`Services/CockpitDataService.cs`:

```
GetFaturalarAsync(start, end)      → List<FaturaRow> (satır bazlı)
GetFaturaOzetAsync(start, end)     → FaturaOzet (toplam/adet)
GetTahsilatOzetAsync(start, end)   → TahsilatOzet (aggregate)
GetSozlesmelerAsync(start, end)    → List<SozlesmeRow> (satır bazlı)
GetSozlesmeOzetAsync(start, end)   → SozlesmeOzet (yenilenen/bekleyen)
InvalidateAll()                    → tüm SP cache temizle
```

## Cache

- **TTL:** 5 dakika
- **Eşzamanlılık:** `SemaphoreSlim` ile double-check lock
- **Cache key formatı:** `sp_fat_20260301_20260331` (tarih bazlı)
- **CacheWarmer:** Startup'ta sabit SP'leri preload eder (haftalık, aylık, YTD)

## SP Çağrı Akışı (Filtre Değişiminde)

```
Kullanıcı filtre değiştirir → AJAX
  → 3 dönem SP parallel: Fatura + Tahsilat + Sözleşme (filtre tarihiyle)
  → 6 sabit SP cache'den: Nisan, YTD, Geçen Hafta, Bu Hafta, Aylık, Yıllık
  → LoadAllCachedDataAsync: ürün kırılımı, müşteri eşleşme (eski cache)
```

## SP Eklerken / Değiştirirken

1. SP içeriği `Services/DatabaseMigrationService.cs` içinde raw SQL ile `IF EXISTS DROP + CREATE` pattern (EF migration kullanılmıyor).
2. Yeni cache key sabiti `CockpitController` üst bloğundaki listeye eklenmeli.
3. `SemaphoreSlim _cacheLock` ile sarılmalı.
4. CacheWarmer'a preload kayıt eklenmeli (her başlatmada hazır olsun).
5. **Test:** SP çıktısını canlı SQL ile karşılaştır; Cockpit ve Fırsat Analiz aynı SP'den beslendiği için iki ekran toplamlarının birebir tutması gerekir.

## İlgili Dosyalar

- `Services/CockpitDataService.cs`
- `Services/DatabaseMigrationService.cs` — SP DDL'leri burada
- `Services/FirsatAnalizStartupWarmer.cs` — preload
- `Controllers/CockpitController.cs` — cache key listesi
