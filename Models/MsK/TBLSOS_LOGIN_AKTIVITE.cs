using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SOS.Models.MsK;

[Table("TBLSOS_LOGIN_AKTIVITE")]
public partial class TBLSOS_LOGIN_AKTIVITE
{
    [Key]
    public long Id { get; set; }

    public int KullaniciId { get; set; }

    [StringLength(256)]
    public string? Email { get; set; }

    [StringLength(256)]
    public string? AdSoyad { get; set; }

    public DateTime GirisZamani { get; set; }

    public DateTime? CikisZamani { get; set; }

    public DateTime SonAktiviteZamani { get; set; }

    public int SureSaniye { get; set; }

    public bool AktifMi { get; set; }

    [StringLength(64)]
    public string? IPAdresi { get; set; }

    [StringLength(512)]
    public string? UserAgent { get; set; }
}
