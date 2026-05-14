using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SOS.Models.MsK;

/// <summary>
/// SOS uygulamasına özel admin kullanıcı listesi.
/// Bu tabloda email'i bulunan kullanıcılar tüm menülere erişebilir.
/// Diğer kullanıcılar yalnızca Cockpit + Fırsat Analizi sayfalarını görür.
/// Univera CRM'in `TBL_KULLANICI.LNGKULLANICITIPI` alanından bağımsızdır.
/// </summary>
[Table("TBLSOS_ADMIN_KULLANICI")]
public class TBLSOS_ADMIN_KULLANICI
{
    [Key]
    public int Id { get; set; }

    [Required]
    [StringLength(200)]
    public string Email { get; set; } = "";

    [StringLength(200)]
    public string? AdSoyad { get; set; }

    public bool Aktif { get; set; } = true;

    public DateTime EklenmeTarihi { get; set; } = DateTime.Now;

    [StringLength(200)]
    public string? EkleyenEmail { get; set; }
}
