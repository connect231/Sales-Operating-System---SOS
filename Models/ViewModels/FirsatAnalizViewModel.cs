namespace SOS.Models.ViewModels;

public class FirsatAnalizViewModel
{
    public string AktifFiltre { get; set; } = "month";
    public DateTime FiltreBaslangic { get; set; }
    public DateTime FiltreBitis { get; set; }

    // Filter options (loaded on initial render)
    public List<FilterOption> Kisiler { get; set; } = new();
    public List<FilterOption> Urunler { get; set; } = new();
}

public record FilterOption(string Id, string Name);

// --- AJAX JSON DTO'lar ---

public record FirsatKpiDto(
    decimal PipelineToplam,
    int AktifFirsatAdet,
    decimal AktifFirsatTrend,
    int AcikTeklifAdet,
    decimal AcikTeklifToplam,
    int AcikSiparisAdet,
    decimal AcikSiparisToplam,
    int KapaliSiparisAdet,
    decimal KapaliSiparisToplam,
    decimal KazanmaOraniCount,
    decimal KazanmaOraniRevenue,
    decimal OrtAnlasma
);

public record FunnelStageDto(
    string Name,
    int Count,
    decimal Value,
    decimal ConversionRate,
    string Color
);

public record StatusBreakdownDto(
    string StatusName,
    int Count,
    decimal TotalValue,
    string Color,
    string Icon
);

public record StatusBreakdownGroupDto(
    string GroupTitle,
    decimal GrandTotal,
    int GrandCount,
    List<StatusBreakdownDto> Items
);

public record ChartDatasetDto(
    string Label,
    decimal[] Data,
    string BackgroundColor,
    string BorderColor
);

public record ChartResponseDto(
    string[] Labels,
    List<ChartDatasetDto> Datasets
);

public record LeaderboardEntryDto(
    int Rank,
    string Name,
    decimal PipelineValue,
    int TotalDeals,
    int WonDeals,
    decimal WinRate,
    decimal AvgDealSize
);

public record RiskAlertDto(
    string Type,
    string Severity,
    string Title,
    string Message,
    int Count,
    decimal Value,
    string Icon
);

public record DetailRowDto(
    string Id,
    string TeklifNo,
    string MusteriAdi,
    string UrunAdi,
    string Durum,
    decimal Tutar,
    decimal? Kar,
    DateTime? Tarih,
    string Sahip,
    string DurumRenk
);

public record DetailResponseDto(
    List<DetailRowDto> Rows,
    int TotalCount,
    int Page,
    int PageSize
);
