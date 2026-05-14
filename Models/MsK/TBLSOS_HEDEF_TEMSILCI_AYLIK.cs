using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SOS.Models.MsK;

[Table("TBLSOS_HEDEF_TEMSILCI_AYLIK")]
public class TBLSOS_HEDEF_TEMSILCI_AYLIK
{
    [Key]
    public long Id { get; set; }

    public int SenaryoId { get; set; }

    [ForeignKey(nameof(SenaryoId))]
    public TBLSOS_HEDEF_SENARYO? Senaryo { get; set; }

    public int TemsilciId { get; set; }

    [ForeignKey(nameof(TemsilciId))]
    public TBLSOS_HEDEF_TEMSILCI? Temsilci { get; set; }

    public int UrunId { get; set; }

    [ForeignKey(nameof(UrunId))]
    public TBLSOS_HEDEF_URUN? Urun { get; set; }

    public byte Ay { get; set; }   // 1..12

    [Required]
    [StringLength(20)]
    public string SatisTipi { get; set; } = "";  // 'YeniSatis' | 'Yenileme'

    [Column(TypeName = "decimal(18,2)")]
    public decimal HedefTutar { get; set; }
}
