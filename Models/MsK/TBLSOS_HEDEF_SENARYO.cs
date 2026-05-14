using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SOS.Models.MsK;

[Table("TBLSOS_HEDEF_SENARYO")]
public class TBLSOS_HEDEF_SENARYO
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; }

    [Required]
    [StringLength(20)]
    public string Kod { get; set; } = "";

    [Required]
    [StringLength(100)]
    public string Ad { get; set; } = "";

    public int Yil { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal YillikToplam { get; set; }

    public bool Aktif { get; set; } = true;
}
