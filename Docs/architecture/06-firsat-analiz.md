# Fırsat Analiz — Cockpit Tutarlılığı

Fırsat Analiz ekranı, satış hunisini (Fırsat → Teklif → Sipariş → Fatura) gösterir. **Fatura kartı Cockpit ile birebir aynı veriyi kullanmak zorundadır.**

> Bu belgeyi oku eğer: Fırsat Analiz'de bir kart Cockpit ile farklı tutarlı görünüyorsa, huni geçiş oranları sorgulanıyorsa, yeni bir aşama eklenecekse, opportunity bazlı analiz değişecekse.

> Detaylı ekran/kod referansı: `Docs/FirsatAnaliz.md`, `Docs/FirsatAnaliz_KodReferans.md`.

## Aşamalar (Bağımsızlık İlkesi)

**Genel prensip:** Her aşama bağımsızdır — fırsatı/teklifi olmayan sipariş veya fatura olabilir. Bir kart diğerinin alt kümesi DEĞİLDİR.

### Fatura Kartı
- `CockpitController.LoadAllCachedDataAsync` ile **aynı cached veri**.
- İade/İptal/Ret filtresi + Varuna eşleşme + sentetik fallback dahil.
- **Kontrol:** Cockpit ile aynı dönemde aynı tutar — tutmuyorsa BUG.

### Teklif Kartı
- Dönemdeki **TÜM teklifler** (`CreatedOn` bazlı).
- Fırsata bağlı olma zorunluluğu **YOK**.

### Sipariş Kartı
- Dönemdeki **TÜM siparişler**:
  - `CreateOrderDate` dönemde, **VEYA**
  - `Closed` + efektif fatura tarihi dönemde.
- Zincir bağımlılığı yok (fırsat veya teklif olmasa da sayılır).

### Fırsat Kartı
- CRM `TBLSOS_VARUNA_FIRSAT_ODATA` bazlı.
- **Hariç:** `Lost` durumu **VE** kapalı siparişli olanlar.

## Cockpit Tutarlılığı Doğrulama

Fırsat Analiz fatura kartı Cockpit ile aynı kaynaktan beslenir. Doğrulama kontrolü:

1. Aynı dönemde Cockpit fatura kartı tutarı = Fırsat Analiz fatura kartı tutarı.
2. Aynı dönemde Cockpit ürün dağılımı toplamı = Fatura kartı dip toplamı.
3. Sapma varsa: cache invalidate (`InvalidateAll()`) sonrası tekrar bak. Hala varsa kod bug'ı.

## İlgili Dosyalar

- `Controllers/FirsatAnalizController.cs`
- `Views/FirsatAnaliz/Index.cshtml`
- `Services/FirsatAnalizStartupWarmer.cs`
- `Docs/FirsatAnaliz.md` — kullanıcı dokümantasyonu (ekran detayları)
- `Docs/FirsatAnaliz_KodReferans.md` — kod katmanı referansı
