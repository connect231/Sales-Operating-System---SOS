# Veri Akışı ve Ürün Eşleşme Zinciri

Cockpit ve Fırsat Analiz ekranlarında gösterilen rakamların **kaynak zinciri** ve **temel hesaplama mantığı**. Bir tutar tutmadığında ilk bakılacak belge.

> Bu belgeyi oku eğer: fatura tutarı tutmuyorsa, ürün dağılımı tutmuyorsa, "şu rakam nereden geliyor?" sorusu varsa, yeni bir veri kaynağı eklenecekse.

## Temel Veri Akışı

> **Tek doğruluk kaynağı:** Varuna `TBL_VARUNA_SIPARIS` (Closed siparişler). `VIEW_CP_EXCEL_FATURA` sadece müşteri portalından akan ek alanları (`Tahsil_Edilen`, `Bekleyen_Bakiye`, `Hukuki_Durum`) sağlar. Excel girişi gecikse bile dashboard Varuna üzerinden gerçek zamanlı çalışır.

```
TBL_VARUNA_SIPARIS (Closed + TotalNetAmount > 0) ← birincil
  ⨝ VIEW_CP_EXCEL_FATURA (varsa Tahsilat/Hukuki alanları)
  ↓ SerialNumber = Fatura_No
  ↓
TBL_VARUNA_SIPARIS → TotalNetAmount (KDV hariç TL, sipariş başlığı)
  ↓ OrderId = TBL_VARUNA_SIPARIS_URUNLERI.CrmOrderId
  ↓
TBL_VARUNA_SIPARIS_URUNLERI → kalemler (döviz bazlı Total, StockCode)
  ↓ StockCode = TBLSOS_URUN_ESLESTIRME.StokKodu
  ↓
TBLSOS_URUN_ESLESTIRME → AnaUrunId → TBLSOS_ANA_URUN.Ad (ürün grubu)
```

Eşleşme **satır bazlı** (mask bazlı değil) — aynı mask farklı ana ürüne gidebilir.

## Fatura Kartı (Dip Toplam)

- **Birincil kaynak:** `TBL_VARUNA_SIPARIS.TotalNetAmount` (KDV hariç TL)
- **Fallback (Varuna'da eşleşmeyen faturalar):** `VIEW_CP_EXCEL_FATURA.Fatura_Toplam`
- **Tüm faturalar dahil**: Varuna eşleşen + eşleşmeyen — sonuçta hepsi gerçek fatura.
- Varuna'da eşleşmeyen faturalar **dip toplama dahil edilir**, ayrıca küçük bir not olarak ("Varuna dışı: N fatura · ₺X") gösterilir.
- **İade/İptal/Ret durumlu faturalar HİÇ sayılmaz** (tutar 0, adet 0). Detay: `Docs/conventions/data-rules.md`.

## Ürün Bazlı Fatura Dağılımı

- **Kaynak:** `TBL_VARUNA_SIPARIS_URUNLERI` kalemlerinden oransal TL dağıtımı.
- **Hesap:** `(kalem.Total / toplamDöviz) * TotalNetAmount` = kalemin TL tutarı.
- Her kalemin `StockCode` → `TBLSOS_URUN_ESLESTIRME` → ürün grubuna atanır.
- **Fatura seviyesi:** Varuna'da eşleşmeyen faturalar (`VarunaEslesti=false`) ürün kırılımına **GİRMEZ**.
- **Kalem seviyesi:** Eşleşen fatura içinde `StockCode` `TBLSOS_URUN_ESLESTIRME`'de bulunmazsa → **"Diğer"** kategorisine düşer (finansal tutarlılık için — kalemin TL değeri kaybolmamalı).
- **ÖNEMLİ kontrol:** Ürün kırılımı toplamı = Fatura kartı dip toplamı. Tutmuyorsa BUG.

## Sentetik Fatura (Excel'de Olmayan Varuna Closed Siparişler)

- Müşteri portalı VIEW (`VIEW_CP_EXCEL_FATURA`) Varuna'ya göre gecikmeli akabilir. Dashboard bu gecikmeyi beklemez.
- `LoadAllCachedDataAsync` (`Controllers/CockpitController.cs`) ve `SP_COCKPIT_FATURA` Varuna'da `Closed` + `TotalNetAmount > 0` olup VIEW'da `Fatura_No` karşılığı **olmayan** siparişleri **sentetik fatura** olarak otomatik ekler. **Tahakkuk şartı yok.**
- Sentetik kayıt:
  - `NetTutar = TotalNetAmount`
  - `EfektifFaturaTarihi = COALESCE(tahakkuk, InvoiceDate, ModifiedOn)` (öncelik sırasıyla)
  - `VarunaEslesti = true`
  - `Durum = null`
- VIEW'a `Fatura_No` ile gerçek kayıt akınca → `SerialNumber == Fatura_No` eşleşmesiyle sentetik kaydı **deduplicate** olur, gerçek satır geçer (tek kayıt görünür).
- Hem Cockpit hem Fırsat Analiz aynı kaynaktan beslendiği için her iki ekranda da senkron görünür.

## İlgili Dosyalar

- `Controllers/CockpitController.cs` — `LoadAllCachedDataAsync`
- `Services/CockpitDataService.cs` — SP çağrı katmanı
- `Models/MsK/TBLSOS_URUN_ESLESTIRME.cs`, `TBLSOS_ANA_URUN.cs`
