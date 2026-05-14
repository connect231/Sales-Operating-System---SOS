# Tahsilat ve CEI (Collection Effectiveness Index)

Tahsilat kartı, vadesi gelen-gelmemiş hesabı ve haftalık/aylık/YTD CEI başarı oranı.

> Bu belgeyi oku eğer: tahsilat tutarı tutmuyorsa, vade hesabı tartışılıyorsa, CEI oranı yanlış görünüyorsa, hukuki takip durumu sorgulanıyorsa.

## Tarih Mantığı (Kritik Ayrım)

| Kart | Hangi tarih bazlı? |
|---|---|
| Fatura kartı | `EfektifFaturaTarihi` (tahakkuk varsa o, yoksa `Fatura_Tarihi`) |
| Tahsilat kartı | `Fatura_Vade_Tarihi` |
| Vadesi geçmiş | `Fatura_Vade_Tarihi` < dönem başı **VE** bakiye > 0 |

> **NOT:** Ödeme sözü tarihi mantığı projeden tamamen kaldırıldı. Sadece `Fatura_Vade_Tarihi` kullanılır.

## Hariç Tutulanlar

- **İade/Ret durumlu faturalar** tahsilat hesaplarından **HARİÇ** (`Docs/conventions/data-rules.md`).
- **Hukuki takipteki faturalar** PAYDA'dan hariç:
  - SP filtresi: `ISNULL(LTRIM(RTRIM(Hukuki_Durum)), '') = ''`

## Tahsilat Kartı Gösterimi

| Alan | Hesap |
|---|---|
| **Büyük tutar** | `SUM(Fatura_Toplam)` — dönemdeki toplam |
| **Tahsil edilen** | `SUM(Tahsil_Edilen)` |
| **Kalan** | `SUM(Bekleyen_Bakiye)` |

Bakiye = `Fatura_Toplam - Tahsil_Edilen`.

**Kaynak:** `VIEW_CP_EXCEL_FATURA` — `Fatura_Toplam`, `Tahsil_Edilen`, `Bekleyen_Bakiye`, `Tahsil_Tarihi`, `Fatura_Vade_Tarihi`.

## CEI Tahsilat Başarı Oranları (Haftalık / Aylık / YTD / Çeyrek)

```
PAY    = SUM(Tahsil_Edilen)  -- Tahsil_Tarihi dönemde olan faturalardan
PAYDA  = PAY + Bakiye_AsOf(@End)
         -- Bakiye_AsOf(@End) = Vade ≤ @End olan her fatura için:
         --     bugünkü Bekleyen_Bakiye  + (Tahsil_Tarihi > @End olan tahsilat tutarı)
         --     → yani "dönem sonu günündeki açık bakiye"
ORAN   = PAY / PAYDA × 100
```

- **PAYDA** = dönem sonuna kadar vadesi gelen tüm alacak (tahsil edilenler + dönem sonunda hâlâ açık olanlar).
- **Kritik:** PAYDA **dönem sonu snapshot'ı** olmalı, anlık değil. Aksi takdirde geçmiş dönemlerin oranı zamanla şişer (dönem sonrası yapılan tahsilatlar PAYDA'dan düşer). Bu yüzden "Bekleyen_Bakiye > 0" gibi anlık filtre kullanmıyoruz; bunun yerine dönem sonrası tahsilatları geri ekliyoruz.
- Cari dönemde (`Tahsil_Tarihi > @End` boş set) davranış değişmez; sadece geçmiş dönemler için snapshot etkisi vardır.
- Tüm CEI kartları (haftalık/aylık/YTD/çeyrek) aynı formül, sadece tarih aralığı farklı.

## SP

`SP_COCKPIT_TAHSILAT(@Start, @End)` — VIEW bazlı, İade/Ret + Hukuki takip hariç. Aggregate döner. Detay: `02-stored-procedures.md`.

## İlgili Dosyalar

- `Services/CockpitDataService.cs` — `GetTahsilatOzetAsync`
- `Services/DatabaseMigrationService.cs` — `SP_COCKPIT_TAHSILAT` DDL
- `Controllers/CockpitController.cs` — CEI hesabı, vadesi geçmiş kartı
