
using SOS.Models.MsK;

namespace SOS.Models.ViewModels
{
    public class CockpitViewModel
    {
        // Faturalar (İade + NULL/Boş)
        public decimal FaturalarToplam { get; set; }
        public int FaturalarAdet { get; set; }

        // Tahsilatlar (Tahsil Edildi)
        public decimal TahsilatlarToplam { get; set; }
        public int TahsilatlarAdet { get; set; }

        // Sözleşmeler (Archived)
        public decimal SozlesmelerToplam { get; set; }
        public int SozlesmelerAdet { get; set; }

        // Trend verileri (önceki dönem karşılaştırma)
        public decimal FaturalarTrend { get; set; }
        public decimal TahsilatlarTrend { get; set; }
        public decimal SozlesmelerTrend { get; set; }

        // Hedef (dönem bazlı dinamik)
        public decimal AylikHedef { get; set; }
        public decimal HedefGerceklesme { get; set; }
        public decimal HedefKalan { get; set; }
        public decimal HedefYuzde { get; set; }
        public int HedefAySayisi { get; set; }

        // YTD Hedef
        public decimal YtdHedef { get; set; }
        public decimal YtdGerceklesme { get; set; }
        public decimal YtdKalan { get; set; }
        public decimal YtdYuzde { get; set; }

        // Filtre bilgileri
        public string AktifFiltre { get; set; } = "ytd";
        public DateTime FiltreBaslangic { get; set; }
        public DateTime FiltreBitis { get; set; }

        // Tahsilat kartı: dönem bazlı toplam/tahsil/kalan/yüzde
        public decimal TahsilEdilecek { get; set; }  // = FaturalarToplam (dönemdeki tüm faturalar)
        public decimal TahsilKalan { get; set; }     // = FaturalarToplam - TahsilatlarToplam
        public decimal TahsilatYuzde { get; set; }   // = TahsilatlarToplam / FaturalarToplam * 100
        public decimal OncekiDonemToplam { get; set; }  // Önceki eşdeğer dönemin fatura toplamı
        public decimal OncekiDonemTahsilat { get; set; } // Önceki eşdeğer dönemin tahsilat toplamı
        public decimal OncekiDonemKalan { get; set; }    // Önceki dönemin kalan bakiyesi

        // CEI Dönem (seçilen filtre başlangıcı → bugün)
        public decimal CeiDonemTahsilat { get; set; }
        public decimal CeiDonemVadesiGecmis { get; set; }
        public decimal CeiDonemOran { get; set; }

        // CEI Aylık (mevcut takvim ayı)
        public decimal CeiAylikTahsilat { get; set; }
        public decimal CeiAylikVadesiGecmis { get; set; }
        public decimal CeiAylikOran { get; set; }

        // CEI Yıllık (YTD — 01.01.2026 → bugün)
        public decimal CeiYillikTahsilat { get; set; }
        public decimal CeiYillikVadesiGecmis { get; set; }
        public decimal CeiYillikOran { get; set; }

        // 2025 yılından kalan bakiye
        public decimal Legacy2025Bakiye { get; set; }

        // Beklenen tahsilat (dönem kalanında)
        public decimal BeklenenTahsilat { get; set; }
        public int BeklenenAdet { get; set; }
        public List<VIEW_CP_EXCEL_FATURA> BeklenenDetaylari { get; set; } = new();

        // Vadesi Geçmiş Alacak (01.01.2025'ten itibaren)
        public decimal VadesiGecmisAlacak { get; set; }
        public int VadesiGecmisAdet { get; set; }

        // Detay listeleri
        public List<VIEW_CP_EXCEL_FATURA> FaturaDetaylari { get; set; } = new();
        public List<VIEW_CP_EXCEL_FATURA> TahsilatDetaylari { get; set; } = new();
        public List<TBL_VARUNA_SOZLESME> SozlesmeDetaylari { get; set; } = new();
        // İzole üst kartlar (global filtrelerden bağımsız)
        public decimal FixedCurrentMonthTarget { get; set; }
        public decimal FixedCurrentMonthActual { get; set; }
        public decimal FixedCurrentMonthPct { get; set; }
        public decimal FixedYTDTarget { get; set; }
        public decimal FixedYTDActual { get; set; }
        public decimal FixedYTDPct { get; set; }
    }
}
