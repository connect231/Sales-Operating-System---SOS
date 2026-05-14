# Veri Kuralları (İade/Ret, Hukuki Takip, Deduplikasyon)

Tüm finansal hesaplarda uygulanan kalıcı veri filtreleri ve deduplikasyon kuralları.

> Bu belgeyi oku eğer: bir fatura "neden burada görünüyor / görünmüyor?" sorusu varsa, dedupe sebebiyle satır kaybolduysa, yeni bir filtre/exclusion eklemen gerekiyorsa.

## İade / Ret / İptal Kuralı

- **İade/Ret/İptal durumlu faturalar tamamen atlanır** — ne pozitif, ne negatif (`continue`).
- **Sebep:** Müşteri portalı VIEW'unda iade net satışa düşülmüş şekilde tutuluyor (negatif satır yok). Varuna'da orijinal pozitif kayıt duruyor. İkisini birden saymak çift sayım olur.
- **VIEW'de İade/Ret + Varuna eşleşen** kayıtlar → `varunaTutarMap`'ten **blacklist** ile çıkarılır.
- **Sentetik faturalar:** Varuna'da Closed olan ama VIEW'da bulunmayan tüm siparişler eklenir; tahakkuk şartı **yok** (tahakkuk varsa tarih onu kullanır, yoksa `InvoiceDate` → `ModifiedOn` fallback).

**Sonuç:**
- Fatura kartında: tutar 0, adet 0.
- Tahsilat kartında: hesaplara HARİÇ.
- Ürün dağılımında: GİRMEZ.

## Hukuki Takip

- `VIEW_CP_EXCEL_FATURA.Hukuki_Durum` kolonu **dolu olan** faturalar tahsilat **PAYDA'sından hariç**.
- SP filtresi (`SP_COCKPIT_TAHSILAT`):
  ```sql
  ISNULL(LTRIM(RTRIM(Hukuki_Durum)), '') = ''
  ```

## Deduplikasyon

| Kaynak | Anahtar | Yöntem |
|---|---|---|
| `VIEW_CP_EXCEL_FATURA` | `Fatura_No` | GroupBy + First |
| `TBL_VARUNA_SIPARIS_URUNLERI` | `CrmOrderId + StockCode` | GroupBy (kodda) |
| `TBLSOS_URUN_ESLESTIRME` | `StokKodu` | GroupBy + First (DB'de duplicate olabilir; 206 unique kalır) |

> `TBLSOS_URUN_ESLESTIRME` ideal olarak StokKodu başına tek satırdır, ama DB'ye nadiren çift girim oluşabiliyor — kod bunu `GroupBy + First` ile tolere ediyor.

## Sentetik Fatura Deduplikasyonu

Varuna fallback ile eklenen sentetik faturalar, Excel'e girildiğinde otomatik olarak gerçek kayıtla değişir:
- **Anahtar:** `SerialNumber` (Varuna) ↔ `Fatura_No` (Excel).
- Aynı Fatura_No ile gerçek kayıt varsa sentetik versiyon dropp edilir.
- Detay: `Docs/architecture/01-veri-akisi.md` (Varuna Fallback bölümü).

## "Diğer" Kategorisi (Ürün Dağılımı)

- Eşleşen fatura içinde `StockCode` `TBLSOS_URUN_ESLESTIRME`'de bulunmazsa → **"Diğer"**.
- **Sebep:** Finansal tutarlılık — kalemin TL değeri kaybolmamalı.
- **Yasak:** "Diğer" bağımsız bir ürün kategorisi olarak ürün listesine eklenmez (bu kategori sadece eşleşmeyen kalemler için bir bucket'tır).

## Doğrulama Sırası (Tutar Tutmadığında)

1. İade/Ret faturaları yanlışlıkla dahil mi?
2. Tahakkuk override mu uygulanıyor (efektif tarih farkı)?
3. Varuna eşleşmesi var mı (sentetik fallback)?
4. Cache stale mi (`InvalidateAll()` dene)?
5. SP çıktısı (`EXEC SP_COCKPIT_FATURA @Start, @End`) ile C# `LoadAllCachedDataAsync` sonucu birebir mi?
6. Hâlâ tutmuyorsa: `finans-hesaplama-auditor` subagent.
