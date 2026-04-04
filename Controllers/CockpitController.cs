using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using SOS.DbData;
using Microsoft.EntityFrameworkCore;
using SOS.Models.ViewModels;

namespace SOS.Controllers
{
    [Authorize]
    public class CockpitController : Controller
    {
        private readonly MskDbContext _context;
        private const decimal AYLIK_HEDEF = 50_000_000m;

        public CockpitController(MskDbContext context)
        {
            _context = context;
        }

        private (DateTime start, DateTime end, string filter, int months) ParseFilter(string? filter, string? startDate, string? endDate)
        {
            var now = DateTime.Now;
            var today = now.Date.AddHours(23).AddMinutes(59).AddSeconds(59);
            var year = now.Year;
            DateTime start;
            DateTime end;
            int months;
            var fmtP = System.Globalization.CultureInfo.InvariantCulture;
            var style = System.Globalization.DateTimeStyles.None;

            // Manuel aralık (öncelikli)
            if (!string.IsNullOrEmpty(startDate) && !string.IsNullOrEmpty(endDate)
                && DateTime.TryParseExact(startDate, "yyyy-MM-dd", fmtP, style, out var sd)
                && DateTime.TryParseExact(endDate, "yyyy-MM-dd", fmtP, style, out var ed))
            {
                filter = "range";
                start = sd.Date;
                end = ed.Date.AddHours(23).AddMinutes(59).AddSeconds(59);
                months = Math.Max(1, (end.Year - start.Year) * 12 + end.Month - start.Month + 1);
                return (start, end, filter, months);
            }

            switch (filter?.ToLowerInvariant())
            {
                case "ytd":
                    start = new DateTime(year, 1, 1);
                    end = today;
                    months = now.Month;
                    break;
                case "q1": // Ocak - Mart
                    start = new DateTime(year, 1, 1);
                    end = new DateTime(year, 3, 31, 23, 59, 59);
                    if (end > today) end = today;
                    months = 3;
                    break;
                case "q2": // Nisan - Haziran
                    start = new DateTime(year, 4, 1);
                    end = new DateTime(year, 6, 30, 23, 59, 59);
                    if (end > today) end = today;
                    months = 3;
                    break;
                case "q3": // Temmuz - Eylül
                    start = new DateTime(year, 7, 1);
                    end = new DateTime(year, 9, 30, 23, 59, 59);
                    if (end > today) end = today;
                    months = 3;
                    break;
                case "q4": // Ekim - Aralık
                    start = new DateTime(year, 10, 1);
                    end = new DateTime(year, 12, 31, 23, 59, 59);
                    if (end > today) end = today;
                    months = 3;
                    break;
                case "lastmonth": // Geçen ay
                    var lmMonth = now.Month == 1 ? 12 : now.Month - 1;
                    var lmYear = now.Month == 1 ? year - 1 : year;
                    start = new DateTime(lmYear, lmMonth, 1);
                    end = new DateTime(lmYear, lmMonth, DateTime.DaysInMonth(lmYear, lmMonth), 23, 59, 59);
                    months = 1;
                    break;
                default: // "month" veya null → Bulunduğumuz ay (default)
                    filter = "month";
                    start = new DateTime(year, now.Month, 1);
                    end = today;
                    months = 1;
                    break;
            }

            return (start, end, filter ?? "month", months);
        }

        // Negatif durumlar: İADE, İPTAL → para çıkışı (NULL = pozitif)
        private static readonly string[] _negativeDurumlar = { "İADE", "IADE", "İPTAL", "IPTAL" };

        // RET: toplama dahil edilmez (ne pozitif ne negatif), sadece detay listesinde gösterilir
        private static bool IsRetDurum(string? durum)
            => !string.IsNullOrWhiteSpace(durum)
               && durum.Trim().Equals("RET", StringComparison.OrdinalIgnoreCase);

        private static bool IsNegatifDurum(string? durum)
            => !string.IsNullOrWhiteSpace(durum)
               && _negativeDurumlar.Any(d => durum!.Trim().Equals(d, StringComparison.OrdinalIgnoreCase));

        private static bool IsTahsilat(string? durum)
            => !string.IsNullOrWhiteSpace(durum)
               && durum.Trim().Equals("TAHSİL EDİLDİ", StringComparison.OrdinalIgnoreCase);

        private static bool IsTahsilatOrKrediKarti(string? durum)
            => !string.IsNullOrWhiteSpace(durum)
               && (durum.Trim().Equals("TAHSİL EDİLDİ", StringComparison.OrdinalIgnoreCase)
                   || durum.Trim().Equals("KREDİ KARTI", StringComparison.OrdinalIgnoreCase)
                   || durum.Trim().Equals("KREDI KARTI", StringComparison.OrdinalIgnoreCase));

        // Kümülatif toplam hesaplama
        private static List<Models.MsK.VIEW_CP_EXCEL_FATURA> KumulatifHesapla(List<Models.MsK.VIEW_CP_EXCEL_FATURA> list)
        {
            decimal running = 0;
            foreach (var f in list)
            {
                if (!IsRetDurum(f.Durum))
                {
                    var tutar = IsNegatifDurum(f.Durum) ? -(f.Fatura_Toplam ?? 0) : (f.Fatura_Toplam ?? 0);
                    running += tutar;
                }
                f.KumulatifToplam = running;
            }
            return list;
        }

        // Net tutar: RET hariç, negatif durumlar düşülür, diğerleri pozitif
        private static decimal NetTutar(IEnumerable<Models.MsK.VIEW_CP_EXCEL_FATURA> kayitlar)
            => kayitlar.Where(f => !IsRetDurum(f.Durum))
                       .Sum(f => IsNegatifDurum(f.Durum) ? -(f.Fatura_Toplam ?? 0) : (f.Fatura_Toplam ?? 0));

        public async Task<IActionResult> Index(string? filter, string? startDate, string? endDate)
        {
            var (start, end, activeFilter, months) = ParseFilter(filter, startDate, endDate);

            // Faturalar: Fatura_Tarihi bazlı filtreleme
            var allFaturalar = await _context.VIEW_CP_EXCEL_FATURAs
                .Where(f => f.Fatura_Tarihi.HasValue
                         && f.Fatura_Tarihi.Value >= start
                         && f.Fatura_Tarihi.Value <= end)
                .ToListAsync();

            // Tüm fatura kayıtları (tahsilat dahil) — brüt toplam
            var faturalar = allFaturalar.ToList();

            // Ürün detayları: JOIN Fatura_No → SerialNumber → OrderId → CrmOrderId
            var faturaNoList = faturalar.Select(f => f.Fatura_No).Where(n => n != null).Distinct().ToList();
            var siparisler = await _context.TBL_VARUNA_SIPARIs
                .Where(s => s.SerialNumber != null && faturaNoList.Contains(s.SerialNumber))
                .Select(s => new { s.SerialNumber, s.OrderId, s.AccountTitle })
                .ToListAsync();

            var orderIds = siparisler.Select(s => s.OrderId).Where(o => o != null).Distinct().ToList();
            var urunler = await _context.TBL_VARUNA_SIPARIS_URUNLERIs
                .Where(u => u.CrmOrderId != null && orderIds.Contains(u.CrmOrderId))
                .Select(u => new { u.CrmOrderId, u.ProductName, u.Quantity })
                .ToListAsync();

            // Fatura → Müşteri + Ürün mapping
            var faturaUrunMap = siparisler
                .Join(urunler, s => s.OrderId, u => u.CrmOrderId,
                      (s, u) => new { s.SerialNumber, s.AccountTitle, u.ProductName, u.Quantity })
                .GroupBy(x => x.SerialNumber)
                .ToDictionary(g => g.Key!, g => g.First());

            // Müşteri bilgisi (ürün eşleşmesi olmasa bile)
            var faturaMusteriMap = siparisler
                .GroupBy(s => s.SerialNumber)
                .ToDictionary(g => g.Key!, g => g.First().AccountTitle);

            foreach (var f in faturalar)
            {
                if (f.Fatura_No != null)
                {
                    if (faturaUrunMap.TryGetValue(f.Fatura_No, out var urun))
                    {
                        f.MusteriUnvan = urun.AccountTitle;
                        f.UrunAdi = urun.ProductName;
                        f.Miktar = urun.Quantity;
                    }
                    else if (faturaMusteriMap.TryGetValue(f.Fatura_No, out var musteri))
                    {
                        f.MusteriUnvan = musteri;
                    }
                }
            }

            // Tahsilatlar: Tahsil Edildi + Kredi Kartı
            // Tarih önceliği: Odeme_Sozu_Tarihi dolu ise onu, yoksa Tahsil_Tarihi kullan
            var allTahsilatKayitlari = await _context.VIEW_CP_EXCEL_FATURAs
                .Where(f => (f.Odeme_Sozu_Tarihi.HasValue || f.Tahsil_Tarihi.HasValue))
                .ToListAsync();

            var tahsilatlar = allTahsilatKayitlari
                .Where(f => IsTahsilatOrKrediKarti(f.Durum))
                .Where(f => {
                    var tarih = f.Odeme_Sozu_Tarihi ?? f.Tahsil_Tarihi;
                    return tarih.HasValue && tarih.Value >= start && tarih.Value <= end;
                })
                .ToList();

            // Tahsilatlara da müşteri + ürün mapping uygula
            foreach (var t in tahsilatlar)
            {
                if (t.Fatura_No != null)
                {
                    if (faturaUrunMap.TryGetValue(t.Fatura_No, out var turun))
                    {
                        t.MusteriUnvan = turun.AccountTitle;
                        t.UrunAdi = turun.ProductName;
                        t.Miktar = turun.Quantity;
                    }
                    else if (faturaMusteriMap.TryGetValue(t.Fatura_No, out var tmusteri))
                    {
                        t.MusteriUnvan = tmusteri;
                    }
                }
            }

            // Sözleşmeler (Archived — tarih bağımsız)
            var sozlesmeler = await _context.TBL_VARUNA_SOZLESMEs
                .Where(s => s.ContractStatus == "Archived")
                .ToListAsync();

            // Trend: önceki eşdeğer dönem
            var prevDuration = end - start;
            var prevStart = start.AddDays(-prevDuration.TotalDays);
            var prevEnd = start.AddSeconds(-1);

            var prevFaturalar = await _context.VIEW_CP_EXCEL_FATURAs
                .Where(f => f.Fatura_Tarihi.HasValue
                         && f.Fatura_Tarihi.Value >= prevStart
                         && f.Fatura_Tarihi.Value <= prevEnd)
                .ToListAsync();

            var prevTahsilatlar = await _context.VIEW_CP_EXCEL_FATURAs
                .Where(f => f.Odeme_Sozu_Tarihi.HasValue || f.Tahsil_Tarihi.HasValue)
                .ToListAsync();

            var prevFatToplam = NetTutar(prevFaturalar);
            var prevTahToplam = prevTahsilatlar
                .Where(f => IsTahsilatOrKrediKarti(f.Durum))
                .Where(f => {
                    var t = f.Odeme_Sozu_Tarihi ?? f.Tahsil_Tarihi;
                    return t.HasValue && t.Value >= prevStart && t.Value <= prevEnd;
                })
                .Sum(f => f.Fatura_Toplam ?? 0);

            var fatToplam = NetTutar(faturalar);
            var tahToplam = tahsilatlar.Sum(f => f.Fatura_Toplam ?? 0);
            var sozToplam = sozlesmeler.Sum(s => s.TotalAmount ?? 0);

            // ═══ TAHSİLAT KARTI: Dönemdeki faturalar vs tahsilat ═══
            // Toplam = dönemdeki tüm faturaların tutarı
            // Tahsil Edilen = dönemdeki tahsilat (TAHSİL EDİLDİ + KREDİ KARTI)
            // Kalan = Toplam - Tahsil Edilen
            // Yüzde = Tahsil Edilen / Toplam * 100
            var tahsilEdilecek = fatToplam;  // dönemdeki tüm faturalar
            var tahsilKalan = Math.Max(fatToplam - tahToplam, 0);
            var tahsilatYuzde = fatToplam > 0
                ? Math.Round(tahToplam / fatToplam * 100, 1)
                : 0;
            // Önceki eşdeğer dönem kalan bakiyesi
            var oncekiDonemKalan = Math.Max(prevFatToplam - prevTahToplam, 0);

            // ═══ CEI 1: DÖNEM BAŞARISI (filtre start → bugün) ═══
            var donemSonuCei = end;
            if (activeFilter == "month")
                donemSonuCei = new DateTime(end.Year, end.Month, DateTime.DaysInMonth(end.Year, end.Month), 23, 59, 59);

            var ceiDonemVgNull = await _context.VIEW_CP_EXCEL_FATURAs
                .Where(f => f.Fatura_Vade_Tarihi.HasValue
                         && f.Fatura_Vade_Tarihi.Value >= start
                         && f.Fatura_Vade_Tarihi.Value <= donemSonuCei
                         && (f.Durum == null || f.Durum.Trim() == ""))
                .ToListAsync();
            var ceiDonemVgBakiye = ceiDonemVgNull.Sum(f => (f.Fatura_Toplam ?? 0) - (f.Tahsil_Edilen ?? 0));
            var ceiDonemTahsilat = tahToplam;
            var ceiDonemOran = tahsilEdilecek > 0
                ? Math.Round(ceiDonemTahsilat / tahsilEdilecek * 100, 1)
                : 0;

            // ═══ CEI 2: AYLIK BAŞARI (mevcut takvim ayı) ═══
            var now2 = DateTime.Now;
            var ayBaslangic = new DateTime(now2.Year, now2.Month, 1);
            var aySonu = new DateTime(now2.Year, now2.Month, DateTime.DaysInMonth(now2.Year, now2.Month), 23, 59, 59);

            var allTahData = await _context.VIEW_CP_EXCEL_FATURAs
                .Where(f => f.Odeme_Sozu_Tarihi.HasValue || f.Tahsil_Tarihi.HasValue)
                .ToListAsync();

            var aylikTah = allTahData
                .Where(f => IsTahsilatOrKrediKarti(f.Durum))
                .Where(f => { var t = f.Odeme_Sozu_Tarihi ?? f.Tahsil_Tarihi; return t.HasValue && t.Value >= ayBaslangic && t.Value <= aySonu; })
                .Sum(f => f.Fatura_Toplam ?? 0);
            var aylikVg = await _context.VIEW_CP_EXCEL_FATURAs
                .Where(f => f.Fatura_Vade_Tarihi.HasValue
                         && f.Fatura_Vade_Tarihi.Value >= ayBaslangic
                         && f.Fatura_Vade_Tarihi.Value <= aySonu
                         && (f.Durum == null || f.Durum.Trim() == ""))
                .SumAsync(f => (f.Fatura_Toplam ?? 0) - (f.Tahsil_Edilen ?? 0));
            var ceiAylikOran = (aylikTah + aylikVg) > 0
                ? Math.Round(aylikTah / (aylikTah + aylikVg) * 100, 1) : 0;

            // ═══ CEI 3: YTD BAŞARI (01.01.2026 → bugün) ═══
            var ytdCeiStart = new DateTime(2026, 1, 1);
            var ytdCeiEnd = DateTime.Now.Date.AddHours(23).AddMinutes(59).AddSeconds(59);
            var ytdTahToplam = allTahData
                .Where(f => IsTahsilatOrKrediKarti(f.Durum))
                .Where(f => { var t = f.Odeme_Sozu_Tarihi ?? f.Tahsil_Tarihi; return t.HasValue && t.Value >= ytdCeiStart && t.Value <= ytdCeiEnd; })
                .Sum(f => f.Fatura_Toplam ?? 0);
            var ytdVgBakiye = await _context.VIEW_CP_EXCEL_FATURAs
                .Where(f => f.Fatura_Vade_Tarihi.HasValue
                         && f.Fatura_Vade_Tarihi.Value >= ytdCeiStart
                         && f.Fatura_Vade_Tarihi.Value < DateTime.Now.Date
                         && (f.Durum == null || f.Durum.Trim() == ""))
                .SumAsync(f => (f.Fatura_Toplam ?? 0) - (f.Tahsil_Edilen ?? 0));
            var ceiYillikOran = (ytdTahToplam + ytdVgBakiye) > 0
                ? Math.Round(ytdTahToplam / (ytdTahToplam + ytdVgBakiye) * 100, 1) : 0;

            // 2025 mirası (sadece gösterim amaçlı)
            var legacy2025Bakiye = await _context.VIEW_CP_EXCEL_FATURAs
                .Where(f => f.Fatura_Vade_Tarihi.HasValue
                         && f.Fatura_Vade_Tarihi.Value >= new DateTime(2025, 1, 1)
                         && f.Fatura_Vade_Tarihi.Value < new DateTime(2026, 1, 1)
                         && (f.Durum == null || f.Durum.Trim() == ""))
                .SumAsync(f => (f.Fatura_Toplam ?? 0) - (f.Tahsil_Edilen ?? 0));

            // Vadesi Geçmiş Alacak: 01.01.2025 → seçilen dönemin ilk günü (kümülatif backlog)
            var vadesiGecmisBaslangic = new DateTime(2025, 1, 1);
            var vadesiGecmisReferans = start; // Filtrenin başlangıç tarihi (örn: Mart → 01.03.2026)
            var vadesiGecmisAlacaklar = await _context.VIEW_CP_EXCEL_FATURAs
                .Where(f => f.Fatura_Vade_Tarihi.HasValue
                         && f.Fatura_Vade_Tarihi.Value >= vadesiGecmisBaslangic
                         && f.Fatura_Vade_Tarihi.Value < vadesiGecmisReferans
                         && (f.Durum == null || f.Durum.Trim() == ""))
                .ToListAsync();

            var vadesiGecmisAlacak = vadesiGecmisAlacaklar.Sum(f => (f.Fatura_Toplam ?? 0) - (f.Tahsil_Edilen ?? 0));
            var vadesiGecmisAdet = vadesiGecmisAlacaklar.Count;

            // Beklenen tahsilat: Vade > bugün AND Vade <= dönem gerçek sonu AND Durum NULL
            var bugun = DateTime.Now.Date;
            // Dönem sonu: filtre end'i bugün ise ayın son gününe genişlet
            var donemSonu = end;
            if (activeFilter == "month" || activeFilter == "lastmonth")
            {
                donemSonu = new DateTime(end.Year, end.Month, DateTime.DaysInMonth(end.Year, end.Month), 23, 59, 59);
            }
            var beklenenList = await _context.VIEW_CP_EXCEL_FATURAs
                .Where(f => f.Fatura_Vade_Tarihi.HasValue
                         && f.Fatura_Vade_Tarihi.Value > bugun
                         && f.Fatura_Vade_Tarihi.Value <= donemSonu
                         && (f.Durum == null || f.Durum.Trim() == ""))
                .ToListAsync();
            // Beklenen faturalar için müşteri mapping (ana sorguya dahil olmayabilirler)
            var beklenenNoList = beklenenList.Select(f => f.Fatura_No).Where(n => n != null && !faturaMusteriMap.ContainsKey(n)).Distinct().ToList();
            if (beklenenNoList.Count > 0)
            {
                var beklenenSiparisler = await _context.TBL_VARUNA_SIPARIs
                    .Where(s => s.SerialNumber != null && beklenenNoList.Contains(s.SerialNumber))
                    .Select(s => new { s.SerialNumber, s.AccountTitle })
                    .ToListAsync();
                foreach (var bs in beklenenSiparisler)
                    if (bs.SerialNumber != null && !faturaMusteriMap.ContainsKey(bs.SerialNumber))
                        faturaMusteriMap[bs.SerialNumber] = bs.AccountTitle;
            }
            foreach (var b in beklenenList)
            {
                if (b.Fatura_No != null && faturaMusteriMap.TryGetValue(b.Fatura_No, out var bmusteri))
                    b.MusteriUnvan = bmusteri;
            }
            var beklenenTahsilat = beklenenList.Sum(f => (f.Fatura_Toplam ?? 0) - (f.Tahsil_Edilen ?? 0));
            var beklenenAdet = beklenenList.Count;

            // Dinamik hedef: aylık 50M × ay sayısı — Gerçekleşen = Fatura
            var donemHedef = AYLIK_HEDEF * months;
            var hedefGerceklesme = fatToplam;
            var hedefKalan = Math.Max(donemHedef - hedefGerceklesme, 0);
            var hedefYuzde = donemHedef > 0
                ? Math.Round(Math.Min(hedefGerceklesme / donemHedef * 100, 100), 1)
                : 0;

            // YTD Hedef: 01.01.2026 → filtrenin bitiş tarihine kadar kümülatif
            var ytdAySayisi = Math.Max(1, (end.Year - 2026) * 12 + end.Month);
            var ytdHedef = AYLIK_HEDEF * ytdAySayisi;
            // YTD fatura toplamı (yıl başından filtrenin bitiş tarihine kadar)
            var ytdFaturalar = await _context.VIEW_CP_EXCEL_FATURAs
                .Where(f => f.Fatura_Tarihi.HasValue
                         && f.Fatura_Tarihi.Value >= ytdCeiStart
                         && f.Fatura_Tarihi.Value <= end)
                .ToListAsync();
            var ytdGerceklesme = NetTutar(ytdFaturalar);
            var ytdKalan = Math.Max(ytdHedef - ytdGerceklesme, 0);
            var ytdYuzde = ytdHedef > 0
                ? Math.Round(Math.Min(ytdGerceklesme / ytdHedef * 100, 100), 1)
                : 0;


            // Dinamik: Mevcut Ay (otomatik hesaplanır)
            var fixedMonthStart = new DateTime(now2.Year, now2.Month, 1);
            var fixedMonthEnd = new DateTime(now2.Year, now2.Month, DateTime.DaysInMonth(now2.Year, now2.Month), 23, 59, 59);
            // Mevcut aydaki tüm faturalar = hedef (toplam)
            var fixedMonthFaturalar = await _context.VIEW_CP_EXCEL_FATURAs
                .Where(f => f.Fatura_Tarihi.HasValue
                    && f.Fatura_Tarihi.Value >= fixedMonthStart
                    && f.Fatura_Tarihi.Value <= fixedMonthEnd)
                .ToListAsync();
            var fixedMonthTarget = NetTutar(fixedMonthFaturalar);
            // Mevcut aydaki tahsilat
            var fixedMonthActual = allTahData
                .Where(f => IsTahsilatOrKrediKarti(f.Durum))
                .Where(f => { var t = f.Odeme_Sozu_Tarihi ?? f.Tahsil_Tarihi; return t.HasValue && t.Value >= fixedMonthStart && t.Value <= fixedMonthEnd; })
                .Sum(f => f.Fatura_Toplam ?? 0);
            var fixedMonthPct = fixedMonthTarget > 0 ? Math.Round(fixedMonthActual / fixedMonthTarget * 100, 1) : 0;

            // Dinamik: YTD (01.01.YYYY → bugün)
            var fixedYTDStart = new DateTime(now2.Year, 1, 1);
            var fixedYTDEnd = now2.Date.AddHours(23).AddMinutes(59).AddSeconds(59);
            // YTD tüm faturalar = hedef (toplam)
            var fixedYTDFaturalar = await _context.VIEW_CP_EXCEL_FATURAs
                .Where(f => f.Fatura_Tarihi.HasValue
                    && f.Fatura_Tarihi.Value >= fixedYTDStart
                    && f.Fatura_Tarihi.Value <= fixedYTDEnd)
                .ToListAsync();
            var fixedYTDTarget = NetTutar(fixedYTDFaturalar);
            // YTD tahsilat
            var fixedYTDActual = allTahData
                .Where(f => IsTahsilatOrKrediKarti(f.Durum))
                .Where(f => { var t = f.Odeme_Sozu_Tarihi ?? f.Tahsil_Tarihi; return t.HasValue && t.Value >= fixedYTDStart && t.Value <= fixedYTDEnd; })
                .Sum(f => f.Fatura_Toplam ?? 0);
            var fixedYTDPct = fixedYTDTarget > 0 ? Math.Round(fixedYTDActual / fixedYTDTarget * 100, 1) : 0;

            var vm = new CockpitViewModel
            {
                FaturalarToplam = fatToplam,
                FaturalarAdet = faturalar.Count,
                TahsilatlarToplam = tahsilatlar.Sum(f => f.Fatura_Toplam ?? 0),
                TahsilatlarAdet = tahsilatlar.Count,
                SozlesmelerToplam = sozToplam,
                SozlesmelerAdet = sozlesmeler.Count,
                FaturalarTrend = prevFatToplam > 0 ? Math.Round((fatToplam - prevFatToplam) / prevFatToplam * 100, 1) : 0,
                TahsilatlarTrend = prevTahToplam > 0 ? Math.Round((tahToplam - prevTahToplam) / prevTahToplam * 100, 1) : 0,
                SozlesmelerTrend = 0,
                AylikHedef = donemHedef,
                HedefGerceklesme = hedefGerceklesme,
                HedefKalan = hedefKalan,
                HedefYuzde = hedefYuzde,
                HedefAySayisi = months,
                YtdHedef = ytdHedef,
                YtdGerceklesme = ytdGerceklesme,
                YtdKalan = ytdKalan,
                YtdYuzde = ytdHedef > 0 ? Math.Round(Math.Min(ytdGerceklesme / ytdHedef * 100, 100), 1) : 0,
                AktifFiltre = activeFilter,
                FiltreBaslangic = start,
                FiltreBitis = end,
                FaturaDetaylari = KumulatifHesapla(faturalar.OrderBy(f => f.Fatura_Tarihi).ToList()),
                TahsilatDetaylari = tahsilatlar.OrderByDescending(f => f.Odeme_Sozu_Tarihi ?? f.Tahsil_Tarihi).ToList(),
                SozlesmeDetaylari = sozlesmeler.OrderByDescending(s => s.TotalAmount).ToList(),
                TahsilEdilecek = tahsilEdilecek,
                TahsilKalan = tahsilKalan,
                TahsilatYuzde = tahsilatYuzde,
                OncekiDonemToplam = prevFatToplam,
                OncekiDonemTahsilat = prevTahToplam,
                OncekiDonemKalan = oncekiDonemKalan,
                CeiDonemTahsilat = ceiDonemTahsilat,
                CeiDonemVadesiGecmis = ceiDonemVgBakiye,
                CeiDonemOran = ceiDonemOran,
                CeiAylikTahsilat = aylikTah,
                CeiAylikVadesiGecmis = aylikVg,
                CeiAylikOran = ceiAylikOran,
                CeiYillikTahsilat = ytdTahToplam,
                CeiYillikVadesiGecmis = ytdVgBakiye,
                CeiYillikOran = ceiYillikOran,
                Legacy2025Bakiye = legacy2025Bakiye,
                BeklenenTahsilat = beklenenTahsilat,
                BeklenenAdet = beklenenAdet,
                BeklenenDetaylari = beklenenList.OrderBy(f => f.Fatura_Vade_Tarihi).ToList(),
                VadesiGecmisAlacak = vadesiGecmisAlacak,
                VadesiGecmisAdet = vadesiGecmisAdet,

                // İzole üst kartlar
                FixedCurrentMonthTarget = fixedMonthTarget,
                FixedCurrentMonthActual = fixedMonthActual,
                FixedCurrentMonthPct = fixedMonthPct,
                FixedYTDTarget = fixedYTDTarget,
                FixedYTDActual = fixedYTDActual,
                FixedYTDPct = fixedYTDPct
            };

            return View(vm);
        }

        [HttpGet]
        public async Task<IActionResult> GetDailyBreakdown(string type, string? filter, string? startDate, string? endDate)
        {
            var (start, end, _, _) = ParseFilter(filter, startDate, endDate);

            switch (type?.ToLowerInvariant())
            {
                case "faturalar":
                {
                    var data = await _context.VIEW_CP_EXCEL_FATURAs
                        .Where(f => f.Fatura_Tarihi.HasValue
                                 && f.Fatura_Tarihi.Value >= start
                                 && f.Fatura_Tarihi.Value <= end)
                        .ToListAsync();

                    var daily = data
                        .GroupBy(f => f.Fatura_Tarihi!.Value.Date)
                        .Select(g => new { Tarih = g.Key.ToString("yyyy-MM-dd"), Toplam = NetTutar(g), Adet = g.Count() })
                        .OrderBy(x => x.Tarih).ToList();
                    return Json(daily);
                }
                case "tahsilatlar":
                {
                    var data = await _context.VIEW_CP_EXCEL_FATURAs
                        .Where(f => f.Odeme_Sozu_Tarihi.HasValue || f.Tahsil_Tarihi.HasValue)
                        .ToListAsync();

                    var daily = data.Where(f => IsTahsilatOrKrediKarti(f.Durum))
                        .Where(f => {
                            var t = f.Odeme_Sozu_Tarihi ?? f.Tahsil_Tarihi;
                            return t.HasValue && t.Value >= start && t.Value <= end;
                        })
                        .GroupBy(f => (f.Odeme_Sozu_Tarihi ?? f.Tahsil_Tarihi)!.Value.Date)
                        .Select(g => new { Tarih = g.Key.ToString("yyyy-MM-dd"), Toplam = g.Sum(x => x.Fatura_Toplam ?? 0), Adet = g.Count() })
                        .OrderBy(x => x.Tarih).ToList();
                    return Json(daily);
                }
                case "sozlesmeler":
                {
                    var data = await _context.TBL_VARUNA_SOZLESMEs
                        .Where(s => s.ContractStatus == "Archived")
                        .ToListAsync();

                    var daily = data.Where(s => s.CreatedOn.HasValue)
                        .GroupBy(s => s.CreatedOn!.Value.Date)
                        .Select(g => new { Tarih = g.Key.ToString("yyyy-MM-dd"), Toplam = g.Sum(x => x.TotalAmount ?? 0), Adet = g.Count() })
                        .OrderBy(x => x.Tarih).ToList();
                    return Json(daily);
                }
                default:
                    return BadRequest(new { error = "Geçersiz tip" });
            }
        }
        [HttpGet]
        public async Task<IActionResult> GetKalemDetay(string faturaNo)
        {
            if (string.IsNullOrEmpty(faturaNo))
                return BadRequest(new { error = "Fatura no gerekli" });

            // Fatura_No → SerialNumber → OrderId → CrmOrderId → Ürünler
            var siparis = await _context.TBL_VARUNA_SIPARIs
                .Where(s => s.SerialNumber == faturaNo)
                .Select(s => new { s.OrderId })
                .FirstOrDefaultAsync();

            if (siparis?.OrderId == null)
                return Json(new List<object>());

            var urunler = await _context.TBL_VARUNA_SIPARIS_URUNLERIs
                .Where(u => u.CrmOrderId == siparis.OrderId)
                .Select(u => new
                {
                    UrunAdi = u.ProductName,
                    StokKodu = u.StockCode,
                    Miktar = u.Quantity,
                    BirimFiyat = u.UnitPrice,
                    Toplam = u.Total,
                    KDV = u.Tax
                })
                .ToListAsync();

            return Json(urunler);
        }
    }
}

