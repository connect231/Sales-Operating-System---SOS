namespace SOS.Models.ViewModels;

// ═════════════════════════════════════════════════════════════════════════
//  HEDEF SİSTEMİ - ViewModels
//  Senaryo: 600M (2026)
//  3 katman: Bant şerit · Şirket Özeti · Temsilci Listesi · Temsilci Detay
// ═════════════════════════════════════════════════════════════════════════

/// <summary>
/// Fırsat Analizi sayfasının tepesindeki tek satır şerit için.
/// Filtre seçimine göre yıllık/dönemlik hedef + gerçekleşen + run-rate.
/// </summary>
public class HedefBantViewModel
{
    public string TemsilciAd { get; set; } = "Tüm Şirket";
    public string Donem { get; set; } = "";          // "2026 YTD", "Q1 2026", "Mart 2026" gibi

    public decimal YillikHedef { get; set; }
    public decimal DonemHedef { get; set; }          // seçili dönemin toplam hedefi
    public decimal Gerceklesen { get; set; }         // seçili dönemin gerçekleşeni (faturadan)

    public decimal HedefYS { get; set; }             // dönem yeni satış hedefi
    public decimal HedefYen { get; set; }            // dönem yenileme hedefi
    public decimal GerceklesenYS { get; set; }
    public decimal GerceklesenYen { get; set; }

    public decimal RunRate { get; set; }             // proje yıllık → ytdGerc × (365/ytdGün)
    public decimal AttainmentTimeAdj { get; set; }   // gerçekleşen / time-adjusted hedef (%)
    public decimal AttainmentRaw { get; set; }       // gerçekleşen / dönem hedefi (%)

    public string RenkSinifi { get; set; } = "neutral"; // "green" | "yellow" | "red" | "neutral"
}

/// <summary>
/// Karne sayfası — Tab 1: Şirket Özeti
/// 12 ay × 8 ürün heatmap + üst KPI satırı.
/// </summary>
public class HedefSirketOzetiViewModel
{
    public int Yil { get; set; }
    public decimal YillikHedef { get; set; }
    public decimal YtdHedef { get; set; }
    public decimal YtdGerceklesen { get; set; }
    public decimal RunRate { get; set; }            // YS_linear + Yen_capped (toplam yıl sonu tahmin)
    public decimal RunRateYS { get; set; }           // YS linear: ytdGercYS × (365/gunGecti)
    public decimal RunRateYen { get; set; }          // Yen cap'li: MIN(linear, YillikHedefYen)
    public decimal YSAcik { get; set; }              // Yıllık hedef − tahmin (sadece pozitifse açık vardır)
    public bool YenCappe { get; set; }               // Yen linear yıllık hedefi geçti mi (cap aktif mi)
    public decimal YSHizOrani { get; set; }          // mevcut YS hızı / hedef YS hızı (0..1+)
    public decimal Attainment { get; set; }
    public string RenkSinifi { get; set; } = "neutral";

    public decimal YillikHedefYS { get; set; }
    public decimal YillikHedefYen { get; set; }
    public decimal YtdHedefYS { get; set; }            // YTD'de YS hedefi (gün-orantılı)
    public decimal YtdHedefYen { get; set; }           // YTD'de Yen hedefi (gün-orantılı)
    public decimal GerceklesenYS { get; set; }
    public decimal GerceklesenYen { get; set; }

    public List<HedefHeatmapHucre> Heatmap { get; set; } = new();
    public List<HedefAyToplam> AyToplamlari { get; set; } = new();
    public List<HedefUrunToplam> UrunToplamlari { get; set; } = new();
}

public class HedefHeatmapHucre
{
    public int UrunId { get; set; }
    public string UrunAd { get; set; } = "";
    public byte Ay { get; set; }
    public decimal Hedef { get; set; }        // SatisTipi=Toplam
    public decimal HedefYS { get; set; }      // SatisTipi=YeniSatis
    public decimal HedefYen { get; set; }     // SatisTipi=Yenileme
    public decimal Gerceklesen { get; set; }
    public decimal GerceklesenYS { get; set; }
    public decimal GerceklesenYen { get; set; }
    public decimal Attainment { get; set; }   // gerc / hedef × 100
}

public class HedefAyToplam
{
    public byte Ay { get; set; }
    public decimal Hedef { get; set; }
    public decimal HedefYS { get; set; }
    public decimal HedefYen { get; set; }
    public decimal Gerceklesen { get; set; }
    public decimal GerceklesenYS { get; set; }
    public decimal GerceklesenYen { get; set; }
    public decimal Attainment { get; set; }
}

public class HedefUrunToplam
{
    public int UrunId { get; set; }
    public string UrunAd { get; set; } = "";
    public decimal HedefYS { get; set; }
    public decimal HedefYen { get; set; }
    public decimal HedefToplam { get; set; }
    public decimal GerceklesenYS { get; set; }
    public decimal GerceklesenYen { get; set; }
    public decimal GerceklesenToplam { get; set; }
    public decimal Attainment { get; set; }
}

/// <summary>
/// Tab 2 alt panel — Excel benzeri matris: temsilci × ürün × yıllık (Toplam/YS/Yen).
/// </summary>
public class HedefTemsilciUrunMatrisViewModel
{
    public List<HedefTemsilciUrunMatrisRow> Satirlar { get; set; } = new();
    public List<string> UrunSirasi { get; set; } = new();   // sütun başlıkları (8 ürün)
    public Dictionary<string, decimal> UrunToplamYS { get; set; } = new();   // ürün adı → yıllık YS toplam
    public Dictionary<string, decimal> UrunToplamYen { get; set; } = new();
    public Dictionary<string, decimal> UrunToplamTop { get; set; } = new();
    public decimal GrandToplam { get; set; }
    public decimal GrandYS { get; set; }
    public decimal GrandYen { get; set; }
}

public class HedefTemsilciUrunMatrisRow
{
    public int TemsilciId { get; set; }
    public string TemsilciAd { get; set; } = "";
    public string Kanal { get; set; } = "";
    public Dictionary<string, decimal> UrunToplam { get; set; } = new();   // ürün adı → toplam
    public Dictionary<string, decimal> UrunYS { get; set; } = new();
    public Dictionary<string, decimal> UrunYen { get; set; } = new();
    public decimal SatirToplam { get; set; }
    public decimal SatirYS { get; set; }
    public decimal SatirYen { get; set; }
}

/// <summary>
/// Karne sayfası — Tab 2: Temsilciler
/// 9 satır liste, en geride kalan üstte sıralanır.
/// </summary>
public class HedefTemsilciSatirViewModel
{
    public int TemsilciId { get; set; }
    public string Ad { get; set; } = "";
    public string Kanal { get; set; } = "";        // "Direkt" | "Kanal"
    public string? CrmPersonId { get; set; }

    public decimal YillikHedef { get; set; }
    public decimal YtdHedef { get; set; }
    public decimal YtdGerceklesen { get; set; }
    public decimal RunRate { get; set; }
    public decimal AttainmentTimeAdj { get; set; }
    public decimal Attainment { get; set; }

    public decimal HedefYS { get; set; }
    public decimal HedefYen { get; set; }
    public decimal YtdHedefYS { get; set; }    // YTD'de YS hedefi (gün-orantılı) — gerçekleşen ile aynı dönem
    public decimal YtdHedefYen { get; set; }   // YTD'de Yen hedefi (gün-orantılı)
    public decimal GerceklesenYS { get; set; }
    public decimal GerceklesenYen { get; set; }

    public string RenkSinifi { get; set; } = "neutral";
}

/// <summary>
/// Karne sayfası — Tab 3: Temsilci Detay
/// Bir temsilciye tıklayınca açılır.
/// </summary>
public class HedefTemsilciDetayViewModel
{
    public int TemsilciId { get; set; }
    public string Ad { get; set; } = "";
    public string Kanal { get; set; } = "";
    public string? CrmPersonId { get; set; }

    public decimal YillikHedef { get; set; }
    public decimal YillikHedefYS { get; set; }
    public decimal YillikHedefYen { get; set; }

    public decimal YtdHedef { get; set; }
    public decimal YtdGerceklesen { get; set; }
    public decimal RunRate { get; set; }
    public decimal AttainmentTimeAdj { get; set; }

    public decimal GerceklesenYS { get; set; }
    public decimal GerceklesenYen { get; set; }

    public List<HedefHeatmapHucre> UrunAyMatris { get; set; } = new();    // 8×12
    public List<HedefAyToplam> AyToplamlari { get; set; } = new();
    public List<HedefUrunToplam> UrunToplamlari { get; set; } = new();

    // Pipeline coverage: açık fırsat+teklif tutarı / kalan hedef
    public decimal AcikPipelineTutar { get; set; }
    public decimal KalanHedef { get; set; }
    public decimal PipelineCoverage { get; set; }    // ratio
}

/// <summary>
/// Karne sayfası — Tab 1: Şirket Özeti, ürün kart grid'i için satır.
/// Temsilci kartının ürün eksenli karşılığı — aynı kart şablonu kullanılır.
/// </summary>
public class HedefUrunSatirViewModel
{
    public int UrunId { get; set; }
    public string UrunAd { get; set; } = "";
    public int SiraNo { get; set; }

    public decimal YillikHedef { get; set; }
    public decimal YillikHedefYS { get; set; }
    public decimal YillikHedefYen { get; set; }

    public decimal YtdHedef { get; set; }
    public decimal YtdHedefYS { get; set; }    // YTD'de YS hedefi (gün-orantılı)
    public decimal YtdHedefYen { get; set; }   // YTD'de Yen hedefi (gün-orantılı)
    public decimal YtdGerceklesen { get; set; }
    public decimal RunRate { get; set; }
    public decimal AttainmentTimeAdj { get; set; }
    public decimal Attainment { get; set; }

    public decimal GerceklesenYS { get; set; }
    public decimal GerceklesenYen { get; set; }

    public string RenkSinifi { get; set; } = "neutral";
}

/// <summary>
/// Ürün-bazlı detay — ⓘ ikonundan açılan yan panel
/// </summary>
public class HedefUrunDetayViewModel
{
    public int UrunId { get; set; }
    public string UrunAd { get; set; } = "";
    public decimal YillikHedef { get; set; }
    public decimal YillikHedefYS { get; set; }
    public decimal YillikHedefYen { get; set; }
    public decimal YtdHedef { get; set; }
    public decimal YtdGerceklesen { get; set; }
    public decimal Attainment { get; set; }
    public string RenkSinifi { get; set; } = "neutral";
    public List<HedefAyToplam> AyToplamlari { get; set; } = new();
    public List<HedefTemsilciSatirViewModel> TemsilciDagilimi { get; set; } = new();
}
