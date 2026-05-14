# Hedef Sistemi

Aylık ve yıllık satış hedefleri **DB'den** beslenir. Hardcoded sabit YOKTUR.

> Bu belgeyi oku eğer: hedef rakamı değiştirilecekse, yeni ürün kategorisi eklenecekse, "ürün eşleşmesi neden 'Diğer' kategorisinde?" sorusu varsa, hedef vs gerçekleşen tablosu üzerinde işlem varsa.

## Mevcut Tablolar (Aktif)

### TBLSOS_HEDEF_URUN_AYLIK (BİRİNCİL KAYNAK — 2026-04 itibariyle)
- Senaryo bazlı ürün × ay × satış-tipi hedef matrisi (`SenaryoId`, `UrunId`, `Ay`, `SatisTipi` ∈ `Toplam` | `YeniSatis` | `Yenileme`).
- Cockpit, Fırsat Analizi ve Karne sayfaları **tek kaynaktan** beslenir: `IHedefService.GetGenelAylikSozlukAsync(yil)` ve `GetGenelHedefRangeAsync(yil, start, end)`.
- Şu anki aktif senaryo: `SENARYO_ID = 1` (600M). UI'dan senaryo seçimi henüz yok — single-active varsayımı.
- 2026 yıllık toplamı (Toplam satır): ₺600.132.211,63 (Karne UI'da `₺600,1M`).

### TBLSOS_HEDEF_AYLIK (FALLBACK / LEGACY)
- Aylık global hedefler — eski Parametre/Index sayfası buraya yazıyor (`Tip=GENEL`, `AnaUrunId=NULL`).
- Yeni `TBLSOS_HEDEF_URUN_AYLIK` boşsa `IHedefService` bu tabloya düşer → backward compat.
- 2026 toplamı: ₺600.000.000.
- **Yeni metrik / kart yazarken bu tabloya değil, HedefService helper'larına git.**

### TBLSOS_ANA_URUN
- 8 ana ürün kategorisi: Enroute, Stokbar, Quest, ServiceCore, Varuna, Hosting, E-Dönüşüm, BFG.
- Yeni kategori eklerken bu tabloya satır eklenir + `TBLSOS_URUN_ESLESTIRME`'de StokKodu ile bağlanır.

### TBLSOS_URUN_ESLESTIRME
- StockCode → AnaUrunId eşleşmesi.
- ~145 kayıt (Excel onaylı), 206 unique StokKodu.
- DB'de nadiren duplicate StokKodu olabilir → **GroupBy + First** ile temizlenir (kodda `LoadAllCachedDataAsync`).
- Eşleşmeyen StokKodu kalemleri **"Diğer"** kategorisine düşer (finansal tutarlılık için — kalemin TL değeri kaybolmamalı).

## Yeni Hedef Tabloları (Senaryo Bazlı, Detay Hedef)

> Bu tabloların kapsamı/akışı henüz finalize edilmedi. Aşağıdaki kayıt sadece varlık tespitidir; içerik güncellendikçe bu bölüm doldurulur.

- `TBLSOS_HEDEF_SENARYO` — hedef senaryosu (versiyon/varyant).
- `TBLSOS_HEDEF_TEMSILCI` — temsilci başına yıllık hedef.
- `TBLSOS_HEDEF_TEMSILCI_AYLIK` — temsilci başına aylık kırılım.
- `TBLSOS_HEDEF_URUN` — ürün başına yıllık hedef.
- `TBLSOS_HEDEF_URUN_AYLIK` — ürün başına aylık kırılım.
- `TBLSOS_HEDEF_URUN_YILLIK` — ürün başına yıllık özet.

`Services/HedefService.cs` bu tabloların okuma/yazma işlemlerini yönetir; `Controllers/HedefController.cs` UI tarafı.

## Hedef vs Gerçekleşen Hesaplama

- **Hedef:** `TBLSOS_HEDEF_AYLIK` toplam dönem değeri.
- **Gerçekleşen:** Fatura kartı dip toplamı (bkz: `01-veri-akisi.md` — İade/Ret hariç, Varuna eşleşen + Excel fallback).
- **Gerçekleşme oranı:** `Gerçekleşen / Hedef × 100`.

## İlgili Dosyalar

- `Services/HedefService.cs`
- `Controllers/HedefController.cs`
- `Models/MsK/TBLSOS_HEDEF_*.cs`
- `Views/Hedef/`
- `Hedefler/` — seed verileri / Excel kaynakları
