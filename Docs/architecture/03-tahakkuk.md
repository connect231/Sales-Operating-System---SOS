# Tahakkuk Sistemi (SAP Bazlı Tarih Override)

Bir faturanın **muhasebe dönemi**, fatura kesilme tarihinden farklı olabilir. Tahakkuk kaydı fatura tarihini override eder. Tüm raporlama (fatura kartı, tahsilat, CEI, YTD, Fırsat Analiz) `EfektifFaturaTarihi` üzerinden çalışır.

> Bu belgeyi oku eğer: tahakkuk import yapacaksan, "fatura yanlış ayda görünüyor" durumu varsa, SAP referans eşleşmesi tartışılıyorsa, `TBLSOS_FATURA_TAHAKKUK` üzerinde işlem varsa.

## Tablo

`TBLSOS_FATURA_TAHAKKUK`:
- `SapReferansNo` — **primary key** (Varuna `SAPOutReferenceCode`)
- `FaturaNo` — opsiyonel (matbu no, sonradan atanabilir)
- `TahakkukTarihi` — efektif tarih
- `Aktif` — soft delete

## Primary Key Mantığı

- **Primary:** `SapReferansNo` — SAP'den gelen referans, fatura kesilmeden önce de bilinir.
- **Secondary:** `FaturaNo` (matbu no, `SerialNumber`) — fatura kesildikten sonra atanır.
- **Neden çift anahtar?** Tahakkuk SAP entegrasyonu fatura matbu no'su atanmadan önce çalışabilmeli.

## Akış

```
LoadAllCachedDataAsync (her fatura için):
  tahakkuk varsa → EfektifFaturaTarihi = TahakkukTarihi
  yoksa          → EfektifFaturaTarihi = Fatura_Tarihi
```

## Servis

`Services/TahakkukService.cs`:
- `GetTahakkukMapAsync()` → `FaturaNo → TahakkukTarihi` map (15 dk cache)
- Dual-key map: SAP + FaturaNo compat (her ikisi de lookup'a girer).
- `Invalidate()` → tüm Cockpit cache'i de temizlenir, anında yansır.

## BulkImport

- **Primary eşleşme:** `SapReferansNo` ile.
- **Fallback:** `SipID` prefix matching (Excel formatı için).
- **UI:** Hem SAP no hem fatura no ile arama yapılabilir.

## Sentetik Faturalarla İlişki

- Varuna fallback (Excel'de henüz yok) faturalarda **sadece tahakkuklu olanlar** sentetik olarak eklenir.
- Tahakkuksuz Closed sipariş → VIEW'e girene kadar beklenir, dashboard'da görünmez.
- Detay: `01-veri-akisi.md` (Varuna Fallback bölümü).

## İlgili Dosyalar

- `Services/TahakkukService.cs`
- `Models/MsK/TBLSOS_FATURA_TAHAKKUK.cs`
- `Controllers/CockpitController.cs` — `LoadAllCachedDataAsync` (override uygulaması)
