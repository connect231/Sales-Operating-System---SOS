using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SOS.Models.MsK;

[Table("TBLSOS_HEDEF_URUN")]
public class TBLSOS_HEDEF_URUN
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; }

    [Required]
    [StringLength(50)]
    public string Ad { get; set; } = "";

    public int SiraNo { get; set; }

    public bool Aktif { get; set; } = true;
}
