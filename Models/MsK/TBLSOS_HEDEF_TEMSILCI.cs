using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SOS.Models.MsK;

[Table("TBLSOS_HEDEF_TEMSILCI")]
public class TBLSOS_HEDEF_TEMSILCI
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    public string Ad { get; set; } = "";

    [Required]
    [StringLength(20)]
    public string Kanal { get; set; } = "";  // 'Direkt' | 'Kanal'

    [StringLength(64)]
    public string? CrmPersonId { get; set; }  // TBLSOS_CRM_PERSON_ODATA.Id eşleşmesi

    public int SiraNo { get; set; }

    public bool Aktif { get; set; } = true;
}
