using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using SOS.Models.MsK;

namespace SOS.DbData;

public partial class MskDbContext : DbContext
{
    public MskDbContext()
    {
    }

    public MskDbContext(DbContextOptions<MskDbContext> options)
        : base(options)
    {
    }

    // ── Identity (scaffolded, sadece AspNetUsers erişilir) ──
    public virtual DbSet<AspNetUser> AspNetUsers { get; set; }

    // ── Eski sistem kullanıcı/parametre tabloları ──
    public virtual DbSet<PARAMETRELER> PARAMETRELERs { get; set; }
    public virtual DbSet<TBL_KULLANICI> TBL_KULLANICIs { get; set; }
    public virtual DbSet<TBL_KULLANICI_FIRMA> TBL_KULLANICI_FIRMAs { get; set; }
    public virtual DbSet<TBL_SISTEM_LOG> TBL_SISTEM_LOGs { get; set; }

    // ── Varuna / CRM entegrasyon tabloları ──
    public virtual DbSet<TBL_VARUNA_SIPARI> TBL_VARUNA_SIPARIs { get; set; }
    public virtual DbSet<TBL_VARUNA_SIPARIS_URUNLERI> TBL_VARUNA_SIPARIS_URUNLERIs { get; set; }
    public virtual DbSet<TBL_VARUNA_SOZLESME> TBL_VARUNA_SOZLESMEs { get; set; }
    public virtual DbSet<TBL_VARUNA_TEKLIF> TBL_VARUNA_TEKLIFs { get; set; }
    public virtual DbSet<TBL_VARUNA_TEKLIF_URUNLERI> TBL_VARUNA_TEKLIF_URUNLERIs { get; set; }
    public virtual DbSet<TBL_VARUNA_URUN_GRUPLAMA> TBL_VARUNA_URUN_GRUPLAMAs { get; set; }
    public virtual DbSet<TBL_VARUNA_OPPORTUNITIES> TBL_VARUNA_OPPORTUNITIESs { get; set; }
    public virtual DbSet<TBL_VARUNA_SALESDOCUMENTTYPESAP> TBL_VARUNA_SALESDOCUMENTTYPESAPs { get; set; }

    // ── SOS aggregate view / tabloları ──
    public virtual DbSet<VIEW_CP_EXCEL_FATURA> VIEW_CP_EXCEL_FATURAs { get; set; }
    public virtual DbSet<VIEW_ORTAK_PROJE_ISIMLERI> VIEW_ORTAK_PROJE_ISIMLERIs { get; set; }

    public virtual DbSet<TBLSOS_ANA_URUN> TBLSOS_ANA_URUNs { get; set; }
    public virtual DbSet<TBLSOS_ADMIN_KULLANICI> TBLSOS_ADMIN_KULLANICIs { get; set; }
    public virtual DbSet<TBLSOS_URUN_ESLESTIRME> TBLSOS_URUN_ESLESTIRMEs { get; set; }
    public virtual DbSet<TBLSOS_HEDEF_AYLIK> TBLSOS_HEDEF_AYLIKs { get; set; }
    public virtual DbSet<TBLSOS_CRM_KULLANICI_GECICI> TBLSOS_CRM_KULLANICI_GECICIs { get; set; }
    public virtual DbSet<TBLSOS_CRM_PERSON_ODATA> TBLSOS_CRM_PERSON_ODATAs { get; set; }
    public virtual DbSet<TBLSOS_VARUNA_FIRSAT_ODATA> TBLSOS_VARUNA_FIRSAT_ODATAs { get; set; }
    public virtual DbSet<TBLSOS_FATURA_TAHAKKUK> TBLSOS_FATURA_TAHAKKUKs { get; set; }

    // ── 2026 Hedef Sistemi (600M senaryo) ──
    public virtual DbSet<TBLSOS_HEDEF_SENARYO> TBLSOS_HEDEF_SENARYOs { get; set; }
    public virtual DbSet<TBLSOS_HEDEF_URUN> TBLSOS_HEDEF_URUNs { get; set; }
    public virtual DbSet<TBLSOS_HEDEF_TEMSILCI> TBLSOS_HEDEF_TEMSILCIs { get; set; }
    public virtual DbSet<TBLSOS_HEDEF_URUN_YILLIK> TBLSOS_HEDEF_URUN_YILLIKs { get; set; }
    public virtual DbSet<TBLSOS_HEDEF_URUN_AYLIK> TBLSOS_HEDEF_URUN_AYLIKs { get; set; }
    public virtual DbSet<TBLSOS_HEDEF_TEMSILCI_AYLIK> TBLSOS_HEDEF_TEMSILCI_AYLIKs { get; set; }

    // ── Account Representatives & Person ──
    public virtual DbSet<TBL_VARUNA_ACCOUNT_REPRESENTATIVES> TBL_VARUNA_ACCOUNT_REPRESENTATIVESs { get; set; }
    public virtual DbSet<TBL_VARUNA_PERSON> TBL_VARUNA_PERSONs { get; set; }

    // ── Login aktivite (kim, ne zaman, ne kadar süre) ──
    public virtual DbSet<TBLSOS_LOGIN_AKTIVITE> TBLSOS_LOGIN_AKTIVITEs { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.UseSqlServer("Name=ConnectionStrings:MsKConnection");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AspNetUser>(entity =>
        {
            entity.HasIndex(e => e.NormalizedUserName, "UserNameIndex")
                .IsUnique()
                .HasFilter("([NormalizedUserName] IS NOT NULL)");
        });

        modelBuilder.Entity<PARAMETRELER>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK_dbo.Parametrelers");
        });

        modelBuilder.Entity<TBL_KULLANICI>(entity =>
        {
            entity.Property(e => e.LNGKULLANICITIP).HasDefaultValue(3);
        });

        modelBuilder.Entity<TBL_VARUNA_SOZLESME>(entity =>
        {
            entity.HasKey(e => e.LNGKOD).HasName("PK__TBL_VARU__E133217F602D71EF");
        });

        modelBuilder.Entity<TBL_VARUNA_TEKLIF>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__TBL_VARU__3214EC070CCD6DBA");
            entity.Property(e => e.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<TBL_VARUNA_TEKLIF_URUNLERI>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__TBL_VARU__3214EC0708ED4C53");
            entity.Property(e => e.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<VIEW_ORTAK_PROJE_ISIMLERI>(entity =>
        {
            entity.ToView("VIEW_ORTAK_PROJE_ISIMLERI");
            entity.Property(e => e.LNGKOD).ValueGeneratedOnAdd();
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
