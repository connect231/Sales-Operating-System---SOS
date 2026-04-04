using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SOS.Models.MsK;

[Table("TBLSOS_HEDEF_AYLIK")]
public class TBLSOS_HEDEF_AYLIK
{
    [Key]
    public int Id { get; set; }

    public int Yil { get; set; }           // 2026

    public int Ay { get; set; }            // 1-12

    [Required]
    [StringLength(20)]
    public string Tip { get; set; } = "GENEL";  // "GENEL" veya "URUN"

    public int? AnaUrunId { get; set; }    // NULL = GENEL, dolu = ürün bazlı hedef

    [ForeignKey("AnaUrunId")]
    public TBLSOS_ANA_URUN? AnaUrun { get; set; }

    [Column(TypeName = "money")]
    public decimal HedefTutar { get; set; } // ₺42.000.000 vs.

    public bool Aktif { get; set; } = true;
}
