using System.ComponentModel.DataAnnotations;

namespace SOS.Models.Kullanici.Account;

public class AccountLoginModel
{
    [Required]
    [Display(Name = "Eposta")]
    [EmailAddress]
    public string Email { get; set; } = null!;

    // DEV/PROD: Şifresiz email login — Password kaldırıldı.
    public string Password { get; set; } = "";

    public bool BeniHatirla { get; set; } = true;

}
