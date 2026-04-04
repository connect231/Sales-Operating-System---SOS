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

    public virtual DbSet<AspNetRole> AspNetRoles { get; set; }

    public virtual DbSet<AspNetRoleClaim> AspNetRoleClaims { get; set; }

    public virtual DbSet<AspNetUser> AspNetUsers { get; set; }

    public virtual DbSet<AspNetUserClaim> AspNetUserClaims { get; set; }

    public virtual DbSet<AspNetUserLogin> AspNetUserLogins { get; set; }

    public virtual DbSet<AspNetUserToken> AspNetUserTokens { get; set; }

    public virtual DbSet<CONNECTSON> CONNECTSONs { get; set; }

    public virtual DbSet<InventoryAccountProduct> InventoryAccountProducts { get; set; }

    public virtual DbSet<PARAMETRELER> PARAMETRELERs { get; set; }

    public virtual DbSet<STOKKODUESLESTIRME> STOKKODUESLESTIRMEs { get; set; }

    public virtual DbSet<TALEPCONNECT> TALEPCONNECTs { get; set; }

    public virtual DbSet<TALEP_SON> TALEP_SONs { get; set; }

    public virtual DbSet<TBLPARAMETRE> TBLPARAMETREs { get; set; }

    public virtual DbSet<TBL_FINANS_ONAY> TBL_FINANS_ONAYs { get; set; }

    public virtual DbSet<TBL_KULLANICI> TBL_KULLANICIs { get; set; }

    public virtual DbSet<TBL_KULLANICI_FIRMA> TBL_KULLANICI_FIRMAs { get; set; }

    public virtual DbSet<TBL_LOG> TBL_LOGs { get; set; }

    public virtual DbSet<TBL_MENU> TBL_MENUs { get; set; }

    public virtual DbSet<TBL_MENU_YETKI_GRUBU> TBL_MENU_YETKI_GRUBUs { get; set; }

    public virtual DbSet<TBL_MENU_YETKI_GRUBU_MENU> TBL_MENU_YETKI_GRUBU_MENUs { get; set; }

    public virtual DbSet<TBL_N4BISSSEFILE> TBL_N4BISSSEFILEs { get; set; }

    public virtual DbSet<TBL_N4BISSUE> TBL_N4BISSUEs { get; set; }

    public virtual DbSet<TBL_POS_ISLEM> TBL_POS_ISLEMs { get; set; }

    public virtual DbSet<TBL_SISTEM_LOG> TBL_SISTEM_LOGs { get; set; }

    public virtual DbSet<TBL_TALEP> TBL_TALEPs { get; set; }

    public virtual DbSet<TBL_TALEP_AKISDURUMLARI> TBL_TALEP_AKISDURUMLARIs { get; set; }

    public virtual DbSet<TBL_TALEP_AKIS_LOG> TBL_TALEP_AKIS_LOGs { get; set; }

    public virtual DbSet<TBL_TALEP_FILE> TBL_TALEP_FILEs { get; set; }

    public virtual DbSet<TBL_TALEP_NOTLAR> TBL_TALEP_NOTLARs { get; set; }

    public virtual DbSet<TBL_VARUNA_SIPARI> TBL_VARUNA_SIPARIs { get; set; }

    public virtual DbSet<TBL_VARUNA_SIPARIS_20260121> TBL_VARUNA_SIPARIS_20260121s { get; set; }

    public virtual DbSet<TBL_VARUNA_SIPARIS_URUNLERI> TBL_VARUNA_SIPARIS_URUNLERIs { get; set; }

    public virtual DbSet<TBL_VARUNA_SIPARIS_URUNLERI_20260121> TBL_VARUNA_SIPARIS_URUNLERI_20260121s { get; set; }

    public virtual DbSet<TBL_VARUNA_SOZLESME> TBL_VARUNA_SOZLESMEs { get; set; }

    public virtual DbSet<TBL_VARUNA_SOZLESME_DOSYALAR> TBL_VARUNA_SOZLESME_DOSYALARs { get; set; }

    public virtual DbSet<TBL_VARUNA_SOZLESME_INVENTORYITEM> TBL_VARUNA_SOZLESME_INVENTORYITEMs { get; set; }

    public virtual DbSet<TBL_VARUNA_STOKKOD_GRUP> TBL_VARUNA_STOKKOD_GRUPs { get; set; }

    public virtual DbSet<TBL_VARUNA_TEKLIF> TBL_VARUNA_TEKLIFs { get; set; }

    public virtual DbSet<TBL_VARUNA_TEKLIF_URUNLERI> TBL_VARUNA_TEKLIF_URUNLERIs { get; set; }

    public virtual DbSet<TBL_VARUNA_URUN_GRUPLAMA> TBL_VARUNA_URUN_GRUPLAMAs { get; set; }

    public virtual DbSet<TBL_DUYURU> TBL_DUYURUs { get; set; }

    public virtual DbSet<TBL_ZABBIX_HOST_LIST> TBL_ZABBIX_HOST_LISTs { get; set; }

    public virtual DbSet<VIEW_N4BISSUE> VIEW_N4BISSUEs { get; set; }

    public virtual DbSet<VIEW_N4BISSUESLIFECYCLE> VIEW_N4BISSUESLIFECYCLEs { get; set; }

    public virtual DbSet<VIEW_N4B_CUSTOMER> VIEW_N4B_CUSTOMERs { get; set; }

    public virtual DbSet<VIEW_N4B_KATEGORILER> VIEW_N4B_KATEGORILERs { get; set; }

    public virtual DbSet<VIEW_ORTAK_PROJE_ISIMLERI> VIEW_ORTAK_PROJE_ISIMLERIs { get; set; }

    public virtual DbSet<VIEW_CP_EXCEL_FATURA> VIEW_CP_EXCEL_FATURAs { get; set; }

    public virtual DbSet<TBLSOS_ANA_URUN> TBLSOS_ANA_URUNs { get; set; }
    public virtual DbSet<TBLSOS_URUN_ESLESTIRME> TBLSOS_URUN_ESLESTIRMEs { get; set; }
    public virtual DbSet<TBLSOS_HEDEF_AYLIK> TBLSOS_HEDEF_AYLIKs { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.UseSqlServer("Name=ConnectionStrings:MsKConnection");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AspNetRole>(entity =>
        {
            entity.HasIndex(e => e.NormalizedName, "RoleNameIndex")
                .IsUnique()
                .HasFilter("([NormalizedName] IS NOT NULL)");
        });

        modelBuilder.Entity<AspNetUser>(entity =>
        {
            entity.HasIndex(e => e.NormalizedUserName, "UserNameIndex")
                .IsUnique()
                .HasFilter("([NormalizedUserName] IS NOT NULL)");

            entity.HasMany(d => d.Roles).WithMany(p => p.Users)
                .UsingEntity<Dictionary<string, object>>(
                    "AspNetUserRole",
                    r => r.HasOne<AspNetRole>().WithMany().HasForeignKey("RoleId"),
                    l => l.HasOne<AspNetUser>().WithMany().HasForeignKey("UserId"),
                    j =>
                    {
                        j.HasKey("UserId", "RoleId");
                        j.ToTable("AspNetUserRoles");
                        j.HasIndex(new[] { "RoleId" }, "IX_AspNetUserRoles_RoleId");
                    });
        });

        modelBuilder.Entity<InventoryAccountProduct>(entity =>
        {
            entity.HasKey(e => e.LNGKOD).HasName("PK__Inventor__E133217FCB727F2B");
        });

        modelBuilder.Entity<PARAMETRELER>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK_dbo.Parametrelers");
        });

        modelBuilder.Entity<TALEP_SON>(entity =>
        {
            entity.Property(e => e.ACANKULLANICI).UseCollation("Latin1_General_CI_AS");
            entity.Property(e => e.COST).UseCollation("Latin1_General_CI_AS");
            entity.Property(e => e.MADDEBASLIK).IsFixedLength();
            entity.Property(e => e.MADDEDURUM).UseCollation("Latin1_General_CI_AS");
            entity.Property(e => e.MOBIL).UseCollation("Latin1_General_CI_AS");
            entity.Property(e => e.PROJE).UseCollation("Latin1_General_CI_AS");
            entity.Property(e => e.SATISDURUMU).UseCollation("Latin1_General_CI_AS");
            entity.Property(e => e.URUN).UseCollation("Latin1_General_CI_AS");
            entity.Property(e => e.YARATICI).UseCollation("Latin1_General_CI_AS");
        });

        modelBuilder.Entity<TBL_KULLANICI>(entity =>
        {
            entity.Property(e => e.LNGKULLANICITIP).HasDefaultValue(3);
        });

        modelBuilder.Entity<TBL_MENU>(entity =>
        {
            entity.Property(e => e.AKTIF).HasDefaultValue(true);
            entity.Property(e => e.INTORDER).HasDefaultValue(0);

            entity.HasOne(d => d.LNGPARENTKODNavigation).WithMany(p => p.InverseLNGPARENTKODNavigation).HasConstraintName("FK_MENU_PARENT");
        });

        modelBuilder.Entity<TBL_MENU_YETKI_GRUBU_MENU>(entity =>
        {
            entity.HasOne(d => d.LNGGRUPKODNavigation).WithMany(p => p.TBL_MENU_YETKI_GRUBU_MENUs)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_GROUP");

            entity.HasOne(d => d.LNGMENUKODNavigation).WithMany(p => p.TBL_MENU_YETKI_GRUBU_MENUs)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_MENU");
        });

        modelBuilder.Entity<TBL_N4BISSSEFILE>(entity =>
        {
            entity.HasOne(d => d.LNGTBLISSUEKODNavigation).WithMany(p => p.TBL_N4BISSSEFILEs).HasConstraintName("FK_TBL_N4BISSSEFILES_TBL_N4BISSUES");
        });

        modelBuilder.Entity<TBL_N4BISSUE>(entity =>
        {
            entity.Property(e => e.ContactMethodID).HasDefaultValue(4648);
        });

        modelBuilder.Entity<TBL_TALEP>(entity =>
        {
            entity.Property(e => e.BYTDURUM).IsFixedLength();
            entity.Property(e => e.TRHKAYIT).HasDefaultValueSql("(getdate())");
        });

        modelBuilder.Entity<TBL_TALEP_AKIS_LOG>(entity =>
        {
            entity.HasOne(d => d.LNGDURUMKODNavigation).WithMany(p => p.TBL_TALEP_AKIS_LOGs).HasConstraintName("FK_TBL_TALEP_AKIS_LOG_TBL_TALEP_AKISDURUMLARI");
        });

        modelBuilder.Entity<TBL_TALEP_FILE>(entity =>
        {
            entity.HasOne(d => d.LNGTALEPKODNavigation).WithMany(p => p.TBL_TALEP_FILEs).HasConstraintName("FK_TBL_TALEP_FILES_TBL_TALEP");
        });

        modelBuilder.Entity<TBL_TALEP_NOTLAR>(entity =>
        {
            entity.HasOne(d => d.LNGTALEPKODNavigation).WithMany(p => p.TBL_TALEP_NOTLARs).HasConstraintName("FK_TBL_TALEP_NOTLAR_TBL_TALEP");
        });

        modelBuilder.Entity<TBL_VARUNA_SIPARIS_20260121>(entity =>
        {
            entity.Property(e => e.LNGKOD).ValueGeneratedOnAdd();
        });

        modelBuilder.Entity<TBL_VARUNA_SIPARIS_URUNLERI_20260121>(entity =>
        {
            entity.Property(e => e.LNGKOD).ValueGeneratedOnAdd();
        });

        modelBuilder.Entity<TBL_VARUNA_SOZLESME>(entity =>
        {
            entity.HasKey(e => e.LNGKOD).HasName("PK__TBL_VARU__E133217F602D71EF");
        });

        modelBuilder.Entity<TBL_VARUNA_SOZLESME_INVENTORYITEM>(entity =>
        {
            entity.HasKey(e => e.LNGKOD).HasName("PK__TBL_VARU__E133217F29AC7FFB");
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

        modelBuilder.Entity<VIEW_N4BISSUE>(entity =>
        {
            entity.ToView("VIEW_N4BISSUES");
        });

        modelBuilder.Entity<VIEW_N4BISSUESLIFECYCLE>(entity =>
        {
            entity.ToView("VIEW_N4BISSUESLIFECYCLE");
        });

        modelBuilder.Entity<VIEW_N4B_CUSTOMER>(entity =>
        {
            entity.ToView("VIEW_N4B_CUSTOMERS");
        });

        modelBuilder.Entity<VIEW_N4B_KATEGORILER>(entity =>
        {
            entity.ToView("VIEW_N4B_KATEGORILER");
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

