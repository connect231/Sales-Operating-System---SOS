using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SOS.Models.MsK;

[Table("TBLSOS_ANA_URUN")]
public class TBLSOS_ANA_URUN
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; }

    [Required]
    [StringLength(50)]
    public string Kod { get; set; } = "";  // "ENROUTE", "STOKBAR", "QUEST" vs.

    [Required]
    [StringLength(100)]
    public string Ad { get; set; } = "";   // "Enroute", "Stokbar", "Quest" vs.

    public int Sira { get; set; }          // Görüntüleme sırası

    public bool Aktif { get; set; } = true;
}
