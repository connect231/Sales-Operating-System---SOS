using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SOS.Models.MsK;

[Table("TBLSOS_URUN_ESLESTIRME")]
public class TBLSOS_URUN_ESLESTIRME
{
    [Key]
    public int Id { get; set; }

    [Required]
    [StringLength(128)]
    public string StokKodu { get; set; } = "";    // TBL_VARUNA_SIPARIS_URUNLERI.StockCode

    [StringLength(512)]
    public string? UrunAdi { get; set; }           // ProductName (bilgi amaçlı)

    [StringLength(20)]
    public string? Mask { get; set; }              // Eşleşen mask (EY, CDH vs.) veya "(özel)"

    [StringLength(50)]
    public string? LisansTipi { get; set; }        // Lisans/Hizmet/Kontör/Komisyon

    public int AnaUrunId { get; set; }             // FK → TBLSOS_ANA_URUN.Id

    [ForeignKey("AnaUrunId")]
    public TBLSOS_ANA_URUN? AnaUrun { get; set; }
}
