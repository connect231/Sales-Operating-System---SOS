using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SOS.Models.MsK;

[Keyless]
public partial class VIEW_N4BISSUE
{
    public int Bildirim_No { get; set; }

    public int? Kategori_No { get; set; }

    [StringLength(200)]
    public string? Kategori_Adi { get; set; }

    [StringLength(200)]
    public string? Kategori_Yolu { get; set; }

    [StringLength(200)]
    public string? Bildirim_Tipi { get; set; }

    [StringLength(200)]
    public string? Bildirim_Durumu { get; set; }

    [StringLength(200)]
    public string? Oncelik { get; set; }

    [StringLength(200)]
    public string? Gelis_Kanali { get; set; }

    [StringLength(200)]
    public string? Tercih_Edilen_Geri_Donus_Metodu2 { get; set; }

    [StringLength(200)]
    public string? Cevap_Verilen_Kanallar1 { get; set; }

    [Column(TypeName = "datetime")]
    public DateTime? Bildirim_Tarihi { get; set; }

    [Column(TypeName = "datetime")]
    public DateTime? Olusturulma_Tarihi { get; set; }

    public string? Bildirim_Aciklamasi { get; set; }

    public string? Cozum_Aciklamasi { get; set; }

    public string? Musteri_Notu { get; set; }

    [StringLength(350)]
    public string? Musteri_Adi { get; set; }

    [StringLength(200)]
    public string? Bildirim_Sahibi_Kullanici_Adi { get; set; }

    [StringLength(200)]
    public string? Bildirim_Sahibi { get; set; }

    [StringLength(200)]
    public string? Bildirim_Giren_Kullanici_Adi { get; set; }

    [StringLength(200)]
    public string? Bildirim_Giren { get; set; }

    [StringLength(200)]
    public string? Bildirim_Ustlenen_Kullanici_Adi { get; set; }

    [StringLength(200)]
    public string? Bildirim_Ustlenen { get; set; }

    [Column(TypeName = "datetime")]
    public DateTime? Sonislemtarih { get; set; }

    [Column(TypeName = "decimal(28, 2)")]
    public decimal? SLA_Sure { get; set; }

    [StringLength(50)]
    public string? SLA_Uyum { get; set; }

    [Column(TypeName = "decimal(28, 2)")]
    public decimal? SLA_Kalan_Sure { get; set; }

    [Column(TypeName = "decimal(28, 2)")]
    public decimal? SLA_Toplam_Gecen_Sure { get; set; }

    [Column(TypeName = "decimal(28, 2)")]
    public decimal? SLA_Ilk_Mudahale_Sure { get; set; }

    [Column(TypeName = "decimal(28, 2)")]
    public decimal? SLA_Ilk_Mudahale_Kalan_Sure { get; set; }

    [Column(TypeName = "decimal(28, 2)")]
    public decimal? SLA_Ilk_Mudahale_Top_Gecen_Sure { get; set; }

    [StringLength(50)]
    public string? SLA_Ilk_Mudahale_Uyum { get; set; }

    [Column(TypeName = "decimal(28, 2)")]
    public decimal? SLA_YD_Cozum_Sure { get; set; }

    [StringLength(50)]
    public string? SLA_YD_Cozum_Uyum { get; set; }

    [Column(TypeName = "decimal(28, 2)")]
    public decimal? SLA_YD_Cozum_Kalan_Sure { get; set; }

    [Column(TypeName = "decimal(28, 2)")]
    public decimal? SLA_YD_Cozum_Toplam_Gecen_Sure { get; set; }

    [StringLength(1024)]
    [Unicode(false)]
    public string? Bildirim_Url { get; set; }

    [StringLength(512)]
    public string? Bildirim_Bekletme_Neden { get; set; }

    [StringLength(50)]
    public string? Support_L1_L2 { get; set; }

    [StringLength(200)]
    public string? Kullanici_Bolum { get; set; }
}

