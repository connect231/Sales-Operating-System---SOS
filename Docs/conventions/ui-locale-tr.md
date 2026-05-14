# UI / Locale / Türkçe Format Kuralları

Apple-kalitesinde 60fps, Türkçe locale, vanilla JS. UI değişikliklerinde uyulması gereken sabit kurallar.

> Bu belgeyi oku eğer: UI metni yazacaksan, sayı/tarih/para formatı değiştireceksen, yeni filtre/buton ekleyeceksen, animasyon/Tailwind kararı vereceksen.

## Türkçe UI

- **"Q2" yerine "2. Çeyrek"** — kısaltma kullanma.
- **Tarihler:** `dd.MM.yyyy` formatı (örn. `28.04.2026`).
- **Para birimi:** `₺` prefix, `N0` format (kuruş gösterilmez).
  - Doğru: `₺1.234.567`
  - Yanlış: `₺1,234,567.00`, `1.234.567 TL`
- Türkçe label'lara İngilizce karıştırma. "Q1" → "1. Çeyrek".

## Büyük / Küçük Harf Kuralı (Casing) — ZORUNLU

**Hepsi büyük harf (ALL CAPS / UPPERCASE) hiçbir yerde kullanılmaz.**

Bu kural **tüm UI metinleri** için geçerli: başlıklar, etiketler, tablo header'ları, badge'ler, butonlar, tooltip'ler, breadcrumb'lar, durum etiketleri.

### Yasak (asla kullanılmaz)
- CSS: `text-transform: uppercase`
- Tailwind class: `uppercase`
- HTML inline: `style="text-transform:uppercase"`
- Sabit string'lerde büyük harfli yazım: `"GERÇEKLEŞEN"`, `"BU AY HEDEFİ"`, `"KALAN"`, `"DURUM"`
- `.toUpperCase()` çağrısıyla görsel metin üretmek (data değeri için OK, görsel için değil)

### Kabul edilen iki stil

| Stil | Kullanım | Örnek |
|---|---|---|
| **Title case** (her kelime ilk harfi büyük) | Kart başlıkları, sayfa başlıkları, ana label'lar | "Bu Ay Hedef", "Fırsat Analizi", "Yeni Satış" |
| **Sentence case** (sadece ilk kelime büyük) | Tablo başlıkları, küçük etiketler, tooltip, alt metin | "Gerçekleşen", "Kalan iş günü", "Günde gerekli", "Henüz teklif verilmemiş" |

### Tipografik Hiyerarşi (uppercase yerine)
Visual emphasis için uppercase yerine bunları kullan:
- **Boyut:** 18px büyük rakam, 12px label, 10px alt-metin
- **Ağırlık:** `font-weight: 800` (extrabold), `700` (bold), `500` (medium)
- **Renk:** `#0f172a` (vurgulu), `#64748b` (orta), `#94a3b8` (silik)
- **`letter-spacing`:** Sadece **küçük** kullan (`-0.01em` headings için), pozitif değer (caps tracking) **yok**.

### Doğru / Yanlış Örnekler

```html
<!-- ❌ Yasak -->
<span class="text-[10px] uppercase tracking-wider">GERÇEKLEŞEN</span>
<div style="text-transform: uppercase;">BU AY HEDEFİ</div>
<th class="uppercase">DURUM</th>

<!-- ✅ Doğru -->
<span class="text-[10px] font-semibold text-slate-500">Gerçekleşen</span>
<div class="text-[12px] font-bold text-slate-700">Bu Ay Hedefi</div>
<th class="text-[10px] font-semibold text-slate-400">Durum</th>
```

### İstisna (yok denebilecek kadar dar)
- **Veri** olarak gelen büyük harfli string'ler (örn. SAP referans no `INV-2026-0001`, ürün kodu `STK-001`) olduğu gibi gösterilir — bunlar **görsel emphasis değil**, kaynaktaki veri formatı.

## Locale Örnekleri

### C#
```csharp
// Doğru
var fmt = new System.Globalization.CultureInfo("tr-TR");
amount.ToString("C0", fmt)  // ₺1.234.567

// Yanlış
amount.ToString("C0")  // OS locale'ine bağlı, deterministik değil
```

### JavaScript
```js
// Doğru
new Intl.NumberFormat('tr-TR', {
  style: 'currency',
  currency: 'TRY',
  maximumFractionDigits: 0
}).format(value)

// Yanlış
'$' + value.toFixed(2)
```

## Filtreler

- **Pill-nav:** Bu ay, Geçen ay, 1-4. Çeyrek, YTD.
- **Dinamik tarih:** Başlangıç/bitiş date picker + Uygula butonu.
- **Bu ay / çeyrekler tam dönem** — bugüne kısıtlanmaz (örn. Nisan filtresi 1–30 Nisan, bugün 28 olsa bile).
- **AJAX zorunlu:** Filtre değişiminde **sayfa reload YOK** — tüm kartlar AJAX ile güncellenir.

## Frontend Stack

- **Tailwind CDN** (geliştirme), ileride static build (bkz: `TODO.md`).
- **Vanilla JS** — jQuery / React / Vue / Alpine **EKLENMEZ**.
- **Animasyon:** `requestAnimationFrame` bazlı, 60fps hedefli.
- **Sidebar / Layout:** `Views/Shared/_Layout.cshtml` global CSS/JS.

## UI Değişiklik Doğrulama

UI değişikliği sonrası:
1. `dotnet run` ile uygulamayı çalıştır.
2. Browser screenshot ile görsel doğrulama yap.
3. Filtre değişiminin AJAX yaptığını ağ sekmesinden doğrula (sayfa reload olmamalı).
4. Türkçe label'ları kontrol et (ingilizce kaçak söz var mı).
