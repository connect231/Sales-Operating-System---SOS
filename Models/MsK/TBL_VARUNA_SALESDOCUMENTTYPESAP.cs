using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SOS.Models.MsK;

[Table("TBL_VARUNA_SALESDOCUMENTTYPESAP")]
public partial class TBL_VARUNA_SALESDOCUMENTTYPESAP
{
    [Key]
    [StringLength(64)]
    [Unicode(false)]
    public string Id { get; set; } = null!;

    [StringLength(64)]
    [Unicode(false)]
    public string? Code { get; set; }

    [StringLength(256)]
    public string? Name { get; set; }

    [Column(TypeName = "datetime")]
    public DateTime? CreatedOn { get; set; }

    [Column(TypeName = "datetime")]
    public DateTime? ModifiedOn { get; set; }
}
