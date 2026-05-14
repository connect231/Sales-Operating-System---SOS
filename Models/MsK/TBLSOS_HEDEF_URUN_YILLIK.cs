using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SOS.Models.MsK;

[Table("TBLSOS_HEDEF_URUN_YILLIK")]
public class TBLSOS_HEDEF_URUN_YILLIK
{
    [Key]
    public int Id { get; set; }

    public int SenaryoId { get; set; }

    [ForeignKey(nameof(SenaryoId))]
    public TBLSOS_HEDEF_SENARYO? Senaryo { get; set; }

    public int UrunId { get; set; }

    [ForeignKey(nameof(UrunId))]
    public TBLSOS_HEDEF_URUN? Urun { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal HedefYeniSatis { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal HedefYenileme { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal HedefToplam { get; set; }
}
