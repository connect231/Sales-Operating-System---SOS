using Microsoft.EntityFrameworkCore;
using SOS.DbData;

namespace SOS.Services
{
    public interface IDatabaseMigrationService
    {
        Task ApplyCustomMigrationsAsync();
    }

    /// <summary>
    /// SOS'a özgü şema migration'larını uygular. EF Migration yerine raw SQL + IF NOT EXISTS pattern.
    /// Yeni tablo / kolon eklemek için bu sınıfa yeni ExecuteSqlAsync çağrısı ekle.
    /// </summary>
    public class DatabaseMigrationService : IDatabaseMigrationService
    {
        private readonly MskDbContext _context;
        private readonly ILogger<DatabaseMigrationService> _logger;

        public DatabaseMigrationService(MskDbContext context, ILogger<DatabaseMigrationService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task ApplyCustomMigrationsAsync()
        {
            try
            {
                _logger.LogInformation("Starting SOS database migrations...");

                // ── TBLSOS_ANA_URUN: 8 ana ürün kategorisi ──
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'TBLSOS_ANA_URUN') " +
                    "CREATE TABLE TBLSOS_ANA_URUN (" +
                    "  Id INT NOT NULL PRIMARY KEY, " +
                    "  Kod NVARCHAR(50) NOT NULL, " +
                    "  Ad NVARCHAR(100) NOT NULL, " +
                    "  Sira INT NOT NULL DEFAULT 0, " +
                    "  Aktif BIT NOT NULL DEFAULT 1" +
                    ")");

                // ── TBLSOS_ADMIN_KULLANICI: SOS-özel admin yetkilendirme ──
                // Bu listedeki email'ler tüm menülere erişebilir; diğerleri sadece Cockpit + Fırsat Analizi.
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'TBLSOS_ADMIN_KULLANICI') " +
                    "CREATE TABLE TBLSOS_ADMIN_KULLANICI (" +
                    "  Id INT IDENTITY(1,1) PRIMARY KEY, " +
                    "  Email NVARCHAR(200) NOT NULL UNIQUE, " +
                    "  AdSoyad NVARCHAR(200) NULL, " +
                    "  Aktif BIT NOT NULL DEFAULT 1, " +
                    "  EklenmeTarihi DATETIME NOT NULL DEFAULT GETDATE(), " +
                    "  EkleyenEmail NVARCHAR(200) NULL" +
                    ")");

                // İlk admin seed — sistem yöneticisi
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT 1 FROM TBLSOS_ADMIN_KULLANICI WHERE Email = 'melih.bulut@univera.com.tr') " +
                    "INSERT INTO TBLSOS_ADMIN_KULLANICI (Email, AdSoyad, Aktif, EkleyenEmail) " +
                    "VALUES ('melih.bulut@univera.com.tr', 'Melih Bulut', 1, 'system')");

                // ── TBLSOS_URUN_ESLESTIRME: StockCode → AnaUrun ──
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'TBLSOS_URUN_ESLESTIRME') " +
                    "CREATE TABLE TBLSOS_URUN_ESLESTIRME (" +
                    "  Id INT IDENTITY(1,1) PRIMARY KEY, " +
                    "  StokKodu NVARCHAR(128) NOT NULL, " +
                    "  UrunAdi NVARCHAR(512) NULL, " +
                    "  Mask NVARCHAR(20) NULL, " +
                    "  LisansTipi NVARCHAR(50) NULL, " +
                    "  AnaUrunId INT NOT NULL REFERENCES TBLSOS_ANA_URUN(Id)" +
                    ")");

                // ── TBLSOS_HEDEF_AYLIK: Aylık hedefler ──
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'TBLSOS_HEDEF_AYLIK') " +
                    "CREATE TABLE TBLSOS_HEDEF_AYLIK (" +
                    "  Id INT IDENTITY(1,1) PRIMARY KEY, " +
                    "  Yil INT NOT NULL, " +
                    "  Ay INT NOT NULL, " +
                    "  Tip NVARCHAR(20) NOT NULL DEFAULT 'GENEL', " +
                    "  AnaUrunId INT NULL REFERENCES TBLSOS_ANA_URUN(Id), " +
                    "  HedefTutar MONEY NOT NULL, " +
                    "  Aktif BIT NOT NULL DEFAULT 1" +
                    ")");

                // ── Seed: 8 ana ürün kategorisi ──
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT 1 FROM TBLSOS_ANA_URUN) " +
                    "INSERT INTO TBLSOS_ANA_URUN (Id, Kod, Ad, Sira, Aktif) VALUES " +
                    "(1, 'BFG', N'BFG', 1, 1), (2, 'E_DONUSUM', N'E-Dönüşüm', 2, 1), (3, 'ENROUTE', N'Enroute', 3, 1), (4, 'HOSTING', N'Hosting', 4, 1), (5, 'QUEST', N'Quest', 5, 1), (6, 'SERVICECORE', N'ServiceCore', 6, 1), (7, 'STOKBAR', N'Stokbar', 7, 1), (8, 'VARUNA', N'Varuna', 8, 1)");

                // ── Seed: Ürün eşleştirme — Excel kaynak bazlı ──
                // Sadece boşsa seed et (runtime'da Excel'den eklenen kodlar korunur)
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT 1 FROM TBLSOS_URUN_ESLESTIRME) " +
                    "INSERT INTO TBLSOS_URUN_ESLESTIRME (StokKodu, UrunAdi, Mask, LisansTipi, AnaUrunId) VALUES " +
                    "(N'500001210', N'Hakediş Bedeli', N'(özel)', N'', 1), (N'PP.01.002', N'Panorama Platform 8 (P8) Geçiş Lisansı', N'PP', N'Lisans', 3), (N'UniDox', N'UniDox Connector Lisansı', N'(özel)', N'', 2), (N'SM.01.003', N'Logo/Netsis Entegrasyonu', N'SM', N'Yazılım', 7), (N'SMH.02.001', N'StockMate Yazılım Bakımı Hizmeti', N'SMH', N'Hizmet', 7), (N'SMY.01.001', N'StockMate Depo Stok Yönetimi Yazılımı', N'SMY', N'Yazılım', 7), (N'SMY.01.002', N'StockMate Dağıtım Kanalı / Tesis Lisansı', N'SMY', N'Yazılım', 7), (N'SMY.01.003', N'StockMate Ek Kullanıcı Lisansı (+1)', N'SMY', N'Yazılım', 7), (N'SMY.01.004', N'StockMate Ek Kullanıcı Lisansı (+5)', N'SMY', N'Yazılım', 7), (N'SMY.02.001', N'StockMate Mobile Depo Personeli El Terminali Lisansı', N'SMY', N'Yazılım', 7), (N'UH.01.002', N'Unidox Kontör', N'UH', N'Kontör', 2), (N'zzzUH.01.002', N'Unidox Kontör-HATALI KOD', N'zzzUH', N'Kontör', 2), (N'500001765', N'EnRoute Pan.Dağıtım Kan.(Bayi/Şb) Lis PX', N'(özel)', N'', 3), (N'EH.01.001', N'EnRoute Panorama - Proje ve Ürün Yönetimi Danışmanlığı Hizmeti', N'EH', N'Hizmet', 3), (N'EH.01.002', N'EnRoute Panorama - Proje Destek Uzmanı Hizmeti', N'EH', N'Hizmet', 3), (N'EH.01.003', N'EnRoute Panorama - Kurulum ve Eğitim Hizmeti', N'EH', N'Hizmet', 3), (N'EH.01.005', N'EnRoute Panorama - Online Eğitim Hizmeti', N'EH', N'Hizmet', 3), (N'EH.01.006', N'EnRoute Panorama - Süreç Danışmanlığı Hizmeti', N'EH', N'Hizmet', 3), (N'EH.02.001', N'EnRoute Panorama - Yazılım Bakımı, Yaşatma ve Merkezi Destek Hizmeti', N'EH', N'Hizmet', 3), (N'EH.02.004', N'EnRoute Panorama FundManager Module - Fon ve Bütçe Yönetimi Yazılım Bakımı Hizmeti', N'EH', N'Hizmet', 3), (N'EH.02.005', N'ENROUTE PANORAMA CHANNEL BALANCE MODULE - BAYİ STOK DENGELEME YAZILIM BAKIMI HİZMETİ', N'EH', N'Hizmet', 3), (N'EH.02.009', N'ENROUTE P ASSET TRACKER DEMİRBAŞ YAZ. BH', N'EH', N'Hizmet', 3), (N'EH.02.013', N'EnRoute Panorama Business Analytics (QSENSE) Site Lisansı Bakımı', N'EH', N'Hizmet', 3), (N'EH.02.014', N'ENROUTE PANORAMA BUSİNESS ANALYTİCS  (QSENSE) KULLANICI LİSANSI BAKIMI', N'EH', N'Hizmet', 3), (N'EH.02.016', N'EnRoute - E-Defter Saklama Hizmeti', N'EH', N'Hizmet', 2)");

                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT 1 FROM TBLSOS_URUN_ESLESTIRME WHERE StokKodu = N'EH.02.018') " +
                    "INSERT INTO TBLSOS_URUN_ESLESTIRME (StokKodu, UrunAdi, Mask, LisansTipi, AnaUrunId) VALUES " +
                    "(N'EH.02.018', N'ENROUTE PANORAMA WEBCONNECTOR BAKIM HİZM', N'EH', N'Hizmet', 3), (N'EH.02.098', N'EnRoute Panorama Veri Çekme Bakım Çağrı Merkezi Destek Hizmeti', N'EH', N'Hizmet', 3), (N'EH.03.001', N'EnRoute Panorama - Çağrı Merkezi Hizmeti (MSD)', N'EH', N'Hizmet', 3), (N'EH.03.003', N'EnRoute Panorama - Uzman Destek Hizmeti', N'EH', N'Hizmet', 3), (N'EH.03.006', N'EnRoute Panorama - Uzaktan Kurulum ve Eğitim Hizmeti', N'EH', N'Hizmet', 3), (N'EH.03.008', N'EnRoute Panorama - Çağrı Merkezi Hizmeti (DDI / OutSource)', N'EH', N'Hizmet', 3), (N'EH.03.011', N'Enroute Panorama - E-Dönüşüm Modülü Kurulum Hizmeti', N'EH', N'Hizmet', 2), (N'EH.05.001', N'EnRoute Panorama - Yazılım Geliştirme Hizmeti', N'EH', N'Hizmet', 3), (N'EH.05.002', N'EnRoute Panorama - Rapor Geliştirme Hizmeti', N'EH', N'Hizmet', 9), (N'EH.06.001', N'EnRoute Panorama - Hosting Hizmeti', N'EH', N'Hizmet', 4), (N'EY.01.002', N'EnRoute Panorama Mobile Sales & Distribution Module (DDI)', N'EY', N'Yazılım', 3), (N'EY.01.011', N'EnRoute Panorama - Dağıtım Kanalı \"Web Connector\"  3rd Party Entegrarasyon Uygulama Lisansı', N'EY', N'Yazılım', 3), (N'EY.01.011 PX', N'EnRoute PX- WebConnector 3rdPrtyEntUygLn', N'EY', N'Yazılım', 3), (N'EY.01.014', N'EnRoute Panorama - Platform Back Office Kullanıcı Lisansı', N'EY', N'Yazılım', 3), (N'EY.01.021', N'EnRoute Panorama - Dağıtım Kanalı (Bayi/Distribütör/Şube) Lisansı', N'EY', N'Yazılım', 3), (N'EY.01.025', N'EnRoute Panorama - Dağıtım Kanalı  \"Web Connector\" Web Service Lisansı', N'EY', N'Yazılım', 3), (N'EY.02.011', N'EnRoute Panorama - Panel - Çoklu Proje Birleştirme Mobil Uygulama Lisansı', N'EY', N'Yazılım', 3), (N'EY.02.011 PX', N'EnRoute PX-Panel Çok PrjBir. MobUy Lsn E', N'EY', N'Yazılım', 3), (N'EY.03.001', N'ENROUTE PANORAMA PAAS LİSANSI (KİRALAMA HİZMETİ)', N'EY', N'Yazılım', 3), (N'EY.04.001', N'EnRoute Panorama - Modül Lisansları Kiralama Hizmeti', N'EY', N'Yazılım', 3), (N'EY.04.002', N'EnRoute Panorama - Fund Manager - Fon ve Bütçe Yönetimi Modül Lisansı', N'EY', N'Yazılım', 3), (N'EY.04.002 PX', N'EnRoute PX-Fon Bütçe Yön. Mod. Lsns Ent', N'EY', N'Yazılım', 3), (N'EY.04.005', N'EnRoute Panorama - Dağıtıcı Lisansları ve Hizmetleri Kiralama Hizmeti', N'EY', N'Yazılım', 3), (N'EY.04.006', N'EnRoute Panorama - Kullanıcı Lisansları Kiralama Hizmeti', N'EY', N'Yazılım', 3), (N'EY.04.006 PX', N'0nRoute PX-Kull Lsns Kira Hiz Ent', N'EY', N'Yazılım', 3)");

                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT 1 FROM TBLSOS_URUN_ESLESTIRME WHERE StokKodu = N'EY.04.007') " +
                    "INSERT INTO TBLSOS_URUN_ESLESTIRME (StokKodu, UrunAdi, Mask, LisansTipi, AnaUrunId) VALUES " +
                    "(N'EY.04.007', N'EnRoute Panorama - Asset Tracker - Demirbaş Takip Modül Lisansı', N'EY', N'Yazılım', 3), (N'EY.04.012', N'EnRoute Panorama - E-Dönüşüm Modül Lisansı', N'EY', N'Yazılım', 3), (N'EY.04.012 PX', N'EnRoute PX-E-Dönüşüm Modül Lisansı', N'EY', N'Yazılım', 3), (N'EY.04.014', N'EnRoute - E-Dönüşüm Lisans Komisyonu', N'EY', N'Komisyon', 2), (N'EY.05.009', N'EnRoute Panorama - Business Analytics - Qlik Sense Analyzer User Lisansı', N'EY', N'Yazılım', 3), (N'EY.05.010', N'EnRoute Panorama - Business Analytics - Qlik Sense Professional User Lisansı', N'EY', N'Yazılım', 3), (N'EY.05.010 PX', N'EnRoute Pan. BA Qlik Sense Prof. Lis PX', N'EY', N'Yazılım', 3), (N'EYS.01.001', N'EnRoute Panorama - Mobil Satış & Dağıtım Çözüm Lisansı', N'EYS', N'Yazılım', 3), (N'EYS.01.001 PX', N'EnRoute PX-Mobil Satış Dağıtım Çözüm Lsn', N'EYS', N'Yazılım', 3), (N'EYS.01.002', N'EnRoute Panorama Mobile Sales & Distribution Module (DDI)', N'EYS', N'Yazılım', 3), (N'EYS.01.014', N'EnRoute Panorama - Platform Back Office Kullanıcı Lisansı', N'EYS', N'Yazılım', 3), (N'EYS.01.014 PX', N'EnRoute PX Panorama - Platform Back Office', N'EYS', N'Yazılım', 3), (N'EYS.01.021', N'EnRoute Panorama - Dağıtım Kanalı (Bayi/Distribütör/Şube) Lisansı', N'EYS', N'Yazılım', 3), (N'EYS.01.021 PX', N'EnRoute PX-DağtmKnalı Bayi/Distr./Şub Ls', N'EYS', N'Yazılım', 3), (N'EYS.01.025', N'EnRoute Panorama - Dağıtım Kanalı  \"Web Connector\" Web Service Lisansı', N'EYS', N'Yazılım', 3), (N'EYS.02.011', N'EnRoute Panorama - Panel - Çoklu Proje Birleştirme Mobil Uygulama Lisansı', N'EYS', N'Yazılım', 3), (N'EYS.02.033', N'EnRoute Panorama - Mobil Kullanıcı Lisansı', N'EYS', N'Yazılım', 3), (N'EYS.02.033 PX', N'EnRoute PX Panorama - Mobil Kullanıcı Ls', N'EYS', N'Yazılım', 3), (N'EYS.04.003', N'EnRoute Panorama - Kullanıcı Lisansları Kiralama Hizmeti', N'EYS', N'Yazılım', 3), (N'EYS.04.012', N'EnRoute Panorama - E-Dönüşüm Modül Lisansı', N'EYS', N'Yazılım', 2), (N'OH.01.002', N'Outsource Proje Yönetim Hizmeti', N'OH', N'Hizmet', 3), (N'OH.02.001', N'Outsource Yazılım Bakımı Hizmeti', N'OH', N'Hizmet', 3), (N'OH.02.002', N'Outsource Donanım Bakımı Hizmeti', N'OH', N'Hizmet', 4), (N'WPH.02.001', N'WebPlus Yazılım Bakımı Hizmeti', N'WPH', N'Hizmet', 3), (N'WPH.03.001', N'WEBPLUS HOSTİNG HİZMETİ', N'WPH', N'Hizmet', 4)");

                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT 1 FROM TBLSOS_URUN_ESLESTIRME WHERE StokKodu = N'WPY.01.001') " +
                    "INSERT INTO TBLSOS_URUN_ESLESTIRME (StokKodu, UrunAdi, Mask, LisansTipi, AnaUrunId) VALUES " +
                    "(N'WPY.01.001', N'WebPlus Distribution Satış-Dağıtım Yazılımı (Enterprise)', N'WPY', N'Yazılım', 3), (N'WPY.01.002', N'WebPlus Dağıtım Kanalı Lisansı', N'WPY', N'Yazılım', 3), (N'WPY.01.003', N'Web Plus Ek Kullanıcı Lisansı (1 Kullanıcı)', N'WPY', N'Yazılım', 3), (N'WPY.01.003 PX', N'Web Pls PX Ek Kull Lsnsı(1 Kull)-Ent.', N'WPY', N'Yazılım', 3), (N'WPY.01.004', N'Web Plus Ek Kullanıcı Lisansı (5 Kullanıcı)', N'WPY', N'Yazılım', 3), (N'WPY.01.004 PX', N'Web Pls PX Ek Kull Lsnsı(5 Kull)', N'WPY', N'Yazılım', 3), (N'WPY.01.005', N'WebPlus Distribution Satış-Dağıtım Yazılımı (Standart)', N'WPY', N'Yazılım', 3), (N'WPY.01.006', N'WebPlus Distribution Satış-Dağıtım Yazılımı (Light)', N'WPY', N'Yazılım', 3), (N'WPY.01.007', N'WebPlus E-Dönüşüm Lisansı', N'WPY', N'Yazılım', 3), (N'WPY.01.008', N'WebPlus Distribution Satış-Dağıtım Yazılımı (Enterprise) Kiralama Hizmeti', N'WPY', N'Yazılım', 3), (N'WPY.02.001', N'WebPlus (Android) Satış Temsilcisi Lisansı', N'WPY', N'Yazılım', 3), (N'WPY.02.001 PX', N'WebPlus  PX (Andrd) Satş Tem. Lsnsı-Ent.', N'WPY', N'Yazılım', 3), (N'WPY.02.002', N'WebPlus DeliveryMan (Dağıtıcı) El Terminali Lisansı - Android', N'WPY', N'Yazılım', 3), (N'WPY.02.003', N'WarehouseMan (Depo Personeli) El Terminali Lisansı - Android', N'WPY', N'Yazılım', 3), (N'WPY.02.008', N'WebPlus (IOS) Satış Temsilcisi Lisansı', N'WPY', N'Yazılım', 3), (N'WPY.04.012', N'WebPlus Enterprise \"Web Connector\" Web Service Lisansı', N'WPY', N'Yazılım', 3), (N'QMY.01.001 PX', N'QuestMt PX- Mobil İş Çözümü', N'(özel)', N'', 5), (N'QH.01.001', N'Quest Panorama - Proje ve Ürün Yönetimi Danışmanlığı Hizmeti', N'QH', N'Hizmet', 5), (N'QH.01.008', N'Quest Panorama - Q-Capture - Nöral Ağ Yeni Kategori Tanımlama (200 SKU)', N'QH', N'Hizmet', 5), (N'QH.01.013', N'Quest Panorama - Q-Capture - Görsel Tanımlama Sunucu Aylık Bakım Hizmeti', N'QH', N'Hizmet', 5), (N'QH.02.001', N'Quest Panorama - Yazılım Bakımı, Yaşatma ve Merkezi Destek Hizmeti', N'QH', N'Hizmet', 5), (N'QH.03.001', N'Quest Panorama  - Çağrı Merkezi Hizmeti', N'QH', N'Hizmet', 5), (N'QH.03.005', N'Quest Panorama - Kullanıcı Lisansları Kiralama Hizmeti (Enterprise)', N'QH', N'Hizmet', 5), (N'QH.06.001', N'Quest Panorama - Yazılım Geliştirme Hizmeti', N'QH', N'Hizmet', 5), (N'QH.07.001', N'Quest Panorama - Rapor Geliştirme Hizmeti', N'QH', N'Hizmet', 9)");

                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT 1 FROM TBLSOS_URUN_ESLESTIRME WHERE StokKodu = N'QH.07.002') " +
                    "INSERT INTO TBLSOS_URUN_ESLESTIRME (StokKodu, UrunAdi, Mask, LisansTipi, AnaUrunId) VALUES " +
                    "(N'QH.07.002', N'Quest Panorama - PY Rapor Geliştirme Hizmeti', N'QH', N'Hizmet', 9), (N'QH.08.001', N'Quest Panorama - Hosting Hizmeti', N'QH', N'Hizmet', 4), (N'QMH.02.001', N'QuestMate Yazılım Bakımı Hizmeti', N'QMH', N'Hizmet', 5), (N'QY.02.007', N'Quest Panorama - Mobil Kullanıcısı Lisansı', N'QY', N'Yazılım', 5), (N'QY.04.004', N'Quest Panorama - Q-Auditor Mobil Kullanıcı Lisansı', N'QY', N'Yazılım', 5), (N'QY.05.003', N'Quest Panorama - Business Analytics - İş Zekası Raporlama (MS Power BI) Site Lisansı', N'QY', N'Yazılım', 5), (N'QY.06.001', N'Quest Panorama - Business Analytics  - Qlik Sense Analyzer User Lisansı', N'QY', N'Yazılım', 5), (N'QY.06.002', N'Quest Panorama - Business Analytics  - Qlik Sense Professional User Lisansı', N'QY', N'Yazılım', 5), (N'QYS.01.001', N'Quest Panorama - Veri Toplama & Mobil Ekip Yönetimi Çözüm Lisansı', N'QYS', N'Yazılım', 5), (N'QYS.01.006', N'Quest Panorama - Platform Back Office Kullanıcı Lisansı', N'QYS', N'Yazılım', 5), (N'QYS.02.007', N'Quest Panorama - Mobil Kullanıcısı Lisansı', N'QYS', N'Yazılım', 5), (N'FURKAN-0101', N'CallDesk PX-Dağıtıcı Lisans ve Hzm. Kira.', N'(özel)', N'', 6), (N'CDH.01.001', N'Calldesk Panorama - Proje ve Ürün Yönetimi Danışmanlığı Hizmeti', N'CDH', N'Hizmet', 6), (N'CDH.01.003', N'Calldesk Panorama - Kurulum ve Eğitim Hizmeti', N'CDH', N'Hizmet', 6), (N'CDH.02.001', N'Calldesk Panorama - Yazılım Bakımı Hizmeti', N'CDH', N'Hizmet', 6), (N'CDH.03.001', N'Calldesk Panorama Destek Hizmeti (Çağrı Merkezi)', N'CDH', N'Hizmet', 6), (N'CDH.03.002', N'Calldesk Panorama - Uzman Destek Hizmeti', N'CDH', N'Hizmet', 6), (N'CDH.03.003', N'Calldesk Panorama - Admin Operatör Hizmeti', N'CDH', N'Hizmet', 6), (N'CDY.01.001', N'CallDesk Panorama- Servis Otomasyonu Çözüm Lisansı', N'CDY', N'Yazılım', 6), (N'CDY.01.008', N'CallDesk Panorama - Merkez ERP Entegrasyonu Web Service Lisansı', N'CDY', N'Yazılım', 6), (N'CDY.01.011', N'CallDesk Panorama - Platform Back Office Kullanıcı Lisansı', N'CDY', N'Yazılım', 6), (N'CDY.01.016', N'CallDesk Panorama - Servis Noktası Lisansı', N'CDY', N'Yazılım', 6), (N'SH.02.001', N'StokBar Panorama - Yazılım Bakımı Hizmeti', N'SH', N'Hizmet', 7), (N'SH.03.001', N'StokBar Panorama - Çağrı Merkezi Hizmeti (Tesis)', N'SH', N'Hizmet', 7)");

                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT 1 FROM TBLSOS_URUN_ESLESTIRME WHERE StokKodu = N'SH.03.002') " +
                    "INSERT INTO TBLSOS_URUN_ESLESTIRME (StokKodu, UrunAdi, Mask, LisansTipi, AnaUrunId) VALUES " +
                    "(N'SH.03.002', N'StokBar Panorama - Uzman Destek Hizmeti', N'SH', N'Hizmet', 7), (N'SH.03.004', N'StokBar Panorama - Çağrı Merkezi Hizmeti (Depo/Lokasyon)', N'SH', N'Hizmet', 7), (N'SH.03.006', N'StokBar Panorama - Çağrı Başı Destek Hizmeti', N'SH', N'Hizmet', 7), (N'SH.05.001', N'StokBar Panorama - Yazılım Geliştirme Hizmeti', N'SH', N'Hizmet', 7), (N'SH.06.001', N'StokBar Panorama - Hosting Hizmeti', N'SH', N'Hizmet', 4), (N'SH.01.001', N'StokBar Panorama - Proje ve Ürün Yönetimi Danışmanlığı Hizmeti', N'SH.01', N'', 7), (N'SH.01.003', N'StokBar Panorama - Kurulum ve Eğitim Hizmeti', N'SH.01', N'', 7), (N'SY.01.004', N'StokBar Panorama - Depo & Sevkiyat Yönetimi \"Standart\" Çözüm Lisansı', N'SY', N'Yazılım', 7), (N'SY.01.005', N'StokBar Panorama - Depo & Üretim Yönetimi Çözüm Lisansı (Business)', N'SY', N'Yazılım', 7), (N'SY.01.007', N'StokBar Panorama - Tesis Lisansı', N'SY', N'Yazılım', 7), (N'SY.01.008', N'StokBar Panorama - Depo / Lokasyon Lisansı', N'SY', N'Yazılım', 7), (N'SY.01.009', N'Stokbar Panorama - Platform Back Office Kullanıcı Lisansı', N'SY', N'Yazılım', 7), (N'SY.02.002', N'StokBar Panorama - Mobil (Android) Kullanıcı Lisansı', N'SY', N'Yazılım', 7), (N'SY.03.016', N'StokBar Panorama - İş Atama Modülü Lisansı', N'SY', N'Yazılım', 7), (N'VH.01.001', N'Varuna SSH Proje Yönetim Hizmeti', N'VH', N'Hizmet', 8), (N'VH.01.002', N'Varuna SSH Yazılım Geliştirme Hizmeti', N'VH', N'Hizmet', 8), (N'VH.01.004', N'Varuna SSH Entegrasyon Hizmeti', N'VH', N'Hizmet', 8), (N'VY.01.005', N'Varuna SSH (Starter) - Teknisyen Aylık Kiralama', N'VY', N'Yazılım', 8), (N'VY.04.006', N'Varuna SSH (Enterprise) - Teknisyen Yıllık  Kiralama', N'VY', N'Yazılım', 8), (N'VY.05.007', N'Varuna CRM (Enterprise) Aylık Kiralama', N'VY', N'Yazılım', 8)");

                // ── TBLSOS_URUN_ESLESTIRME: Duplicate temizliği + UNIQUE INDEX (kalıcı önleyici) ──
                // Geçmişte seed blokları anchor-stok-kodu guard'ıyla idempotent olmaya çalışıyordu;
                // ama anchor silinirse 50'lik blok yeniden basıp duplicate üretiyordu.
                // Bu blok hem mevcut duplicate'leri temizler (StokKodu başına en küçük Id'yi tutar)
                // hem UNIQUE INDEX ile DB-level guard kurar — ikinci basışta INSERT exception verir.
                await ExecuteSqlAsync(
                    "IF EXISTS (SELECT 1 FROM TBLSOS_URUN_ESLESTIRME GROUP BY StokKodu HAVING COUNT(*) > 1) " +
                    "BEGIN " +
                    "  WITH dup AS (SELECT Id, ROW_NUMBER() OVER (PARTITION BY StokKodu ORDER BY Id) AS rn FROM TBLSOS_URUN_ESLESTIRME) " +
                    "  DELETE FROM TBLSOS_URUN_ESLESTIRME WHERE Id IN (SELECT Id FROM dup WHERE rn > 1); " +
                    "END");
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_TBLSOS_URUN_ESLESTIRME_StokKodu') " +
                    "CREATE UNIQUE INDEX UX_TBLSOS_URUN_ESLESTIRME_StokKodu ON TBLSOS_URUN_ESLESTIRME(StokKodu)");

                // ── Seed: 2026 genel hedefler (Excel onaylı, toplam ₺600M) ──
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT 1 FROM TBLSOS_HEDEF_AYLIK WHERE Yil=2026 AND Tip='GENEL') " +
                    "INSERT INTO TBLSOS_HEDEF_AYLIK (Yil, Ay, Tip, AnaUrunId, HedefTutar, Aktif) VALUES " +
                    "(2026, 1, 'GENEL', NULL, 42000000, 1), (2026, 2, 'GENEL', NULL, 42000000, 1), (2026, 3, 'GENEL', NULL, 48000000, 1), (2026, 4, 'GENEL', NULL, 45000000, 1), (2026, 5, 'GENEL', NULL, 50000000, 1), (2026, 6, 'GENEL', NULL, 55000000, 1), (2026, 7, 'GENEL', NULL, 50000000, 1), (2026, 8, 'GENEL', NULL, 55000000, 1), (2026, 9, 'GENEL', NULL, 50000000, 1), (2026, 10, 'GENEL', NULL, 55000000, 1), (2026, 11, 'GENEL', NULL, 53000000, 1), (2026, 12, 'GENEL', NULL, 55000000, 1)");

                // ── TBLSOS_FATURA_TAHAKKUK: Tahakkuk override tablosu ──
                // Bazı faturalar Nisan'da kesilmiş ama muhasebe açısından Mart'a ait kabul edilmeli.
                // Bu tablo Fatura_No için manuel "tahakkuk tarihi" override'ı tutar.
                // Tüm dashboard hesapları, eğer fatura için tahakkuk varsa Fatura_Tarihi yerine TahakkukTarihi'ni kullanır.
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'TBLSOS_FATURA_TAHAKKUK') " +
                    "CREATE TABLE TBLSOS_FATURA_TAHAKKUK (" +
                    "  Id INT IDENTITY(1,1) PRIMARY KEY, " +
                    "  FaturaNo NVARCHAR(64) NOT NULL, " +
                    "  TahakkukTarihi DATETIME NOT NULL, " +
                    "  OrijinalFaturaTarihi DATETIME NULL, " +
                    "  Aciklama NVARCHAR(500) NULL, " +
                    "  OlusturulmaTarihi DATETIME NOT NULL DEFAULT GETDATE(), " +
                    "  OlusturanKullanici NVARCHAR(256) NULL, " +
                    "  GuncellemeTarihi DATETIME NULL, " +
                    "  GuncelleyenKullanici NVARCHAR(256) NULL, " +
                    "  Aktif BIT NOT NULL DEFAULT 1" +
                    ")");

                // ── TBLSOS_FATURA_TAHAKKUK: SapReferansNo kolonu ekleme (SAP bazlı tahakkuk) ──
                // Her adım ayrı çalışmalı — SQL Server tek batch'te henüz eklenmemiş kolonu parse edemez
                await ExecuteSqlAsync(
                    "IF COL_LENGTH('TBLSOS_FATURA_TAHAKKUK', 'SapReferansNo') IS NULL " +
                    "ALTER TABLE TBLSOS_FATURA_TAHAKKUK ADD SapReferansNo NVARCHAR(64) NULL");
                await ExecuteSqlAsync(
                    "UPDATE TBLSOS_FATURA_TAHAKKUK SET SapReferansNo = FaturaNo WHERE SapReferansNo IS NULL");
                await ExecuteSqlAsync(
                    "IF EXISTS (SELECT 1 FROM TBLSOS_FATURA_TAHAKKUK WHERE SapReferansNo IS NULL) " +
                    "UPDATE TBLSOS_FATURA_TAHAKKUK SET SapReferansNo = CAST(Id AS NVARCHAR(64)) WHERE SapReferansNo IS NULL");
                await ExecuteSqlAsync(
                    "ALTER TABLE TBLSOS_FATURA_TAHAKKUK ALTER COLUMN SapReferansNo NVARCHAR(64) NOT NULL");
                await ExecuteSqlAsync(
                    "ALTER TABLE TBLSOS_FATURA_TAHAKKUK ALTER COLUMN FaturaNo NVARCHAR(64) NULL");

                // SapReferansNo bazlı unique index — eski FaturaNo index'i kaldır
                await ExecuteSqlAsync(
                    "IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_TBLSOS_FATURA_TAHAKKUK_FaturaNo_Aktif') " +
                    "DROP INDEX UX_TBLSOS_FATURA_TAHAKKUK_FaturaNo_Aktif ON TBLSOS_FATURA_TAHAKKUK");
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_TBLSOS_FATURA_TAHAKKUK_SapRef_Aktif') " +
                    "CREATE UNIQUE INDEX UX_TBLSOS_FATURA_TAHAKKUK_SapRef_Aktif " +
                    "ON TBLSOS_FATURA_TAHAKKUK (SapReferansNo) WHERE Aktif = 1");

                // ── SP_COCKPIT_FATURA: Fatura kartı hesaplama SP ──
                await ExecuteSqlAsync(@"
CREATE OR ALTER PROCEDURE SP_COCKPIT_FATURA
    @StartDate DATE,
    @EndDate   DATE,
    @Owner     NVARCHAR(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- 1) VIEW faturalari: Fatura_No bazinda dedupe (NULL olanlar dedupe edilmez)
    ;WITH DistinctFatura AS (
        SELECT *, CASE WHEN Fatura_No IS NULL THEN 1
            ELSE ROW_NUMBER() OVER (PARTITION BY Fatura_No ORDER BY (SELECT NULL)) END AS rn
        FROM VIEW_CP_EXCEL_FATURA
    ),
    Faturalar AS (
        SELECT Fatura_No, Fatura_Tarihi, Fatura_Toplam, Durum,
               Fatura_Vade_Tarihi, Ilgili_Kisi, Tahsil_Edilen, Bekleyen_Bakiye
        FROM DistinctFatura WHERE rn = 1
    ),

    -- 2) Iade/Ret blacklist: VIEW'de iade/ret + Varuna eslesen SN'ler
    IadeRetBL AS (
        SELECT DISTINCT f.Fatura_No
        FROM Faturalar f
        INNER JOIN TBL_VARUNA_SIPARIS v ON v.SerialNumber = f.Fatura_No
            AND v.OrderStatus = 'Closed' AND v.TotalNetAmount > 0
            AND v.DeletedOn IS NULL
        WHERE LTRIM(RTRIM(f.Durum)) IN (N'İADE',N'IADE',N'İPTAL',N'IPTAL',N'RET',N'İade Fatura',N'Iade Fatura')
    ),

    -- 3) Varuna Closed (blacklist haric, owner filtreli)
    VarunaClosed AS (
        SELECT SerialNumber, TotalNetAmount, InvoiceDate, AccountTitle, SAPOutReferenceCode, OrderId
        FROM TBL_VARUNA_SIPARIS
        WHERE OrderStatus = 'Closed' AND TotalNetAmount > 0
          AND DeletedOn IS NULL
          AND SerialNumber IS NOT NULL
          AND SerialNumber NOT IN (SELECT Fatura_No FROM IadeRetBL)
          AND (@Owner IS NULL OR ProposalOwnerId = @Owner)
    ),

    -- 4) Tahakkuk
    TH AS (
        SELECT SapReferansNo, FaturaNo, TahakkukTarihi
        FROM TBLSOS_FATURA_TAHAKKUK WHERE Aktif = 1
    ),

    -- 5) VIEW + Varuna INNER JOIN + Tahakkuk + filtre
    --    INNER JOIN: sadece Varuna'da Closed olan faturalar sayilir
    FaturalarJoin AS (
        SELECT
            f.Fatura_No AS FaturaNo,
            COALESCE(t1.TahakkukTarihi, t2.TahakkukTarihi, f.Fatura_Tarihi) AS EfektifTarih,
            vc.TotalNetAmount AS NetTutar,
            vc.AccountTitle AS Firma,
            1 AS VarunaEslesti,
            CASE WHEN COALESCE(t1.TahakkukTarihi, t2.TahakkukTarihi) IS NOT NULL THEN 1 ELSE 0 END AS TahakkukVar,
            0 AS IsSentetik
        FROM Faturalar f
        INNER JOIN VarunaClosed vc ON vc.SerialNumber = f.Fatura_No
        LEFT JOIN TH t1 ON t1.SapReferansNo = LTRIM(RTRIM(vc.SAPOutReferenceCode))
        LEFT JOIN TH t2 ON t2.FaturaNo = f.Fatura_No AND t1.TahakkukTarihi IS NULL
        WHERE LTRIM(RTRIM(ISNULL(f.Durum,''))) NOT IN (N'İADE',N'IADE',N'İPTAL',N'IPTAL',N'RET',N'İade Fatura',N'Iade Fatura')
          AND f.Fatura_No NOT IN (SELECT Fatura_No FROM IadeRetBL)
    ),

    -- 6) Sentetik: Varuna Closed + VIEW'de yok (tahakkuk opsiyonel)
    --    Tarih: tahakkuk varsa o, yoksa InvoiceDate, yoksa ModifiedOn (Closed olduğu tarih)
    ViewFNSet AS (
        SELECT DISTINCT Fatura_No FROM Faturalar WHERE Fatura_No IS NOT NULL
    ),
    Sentetik AS (
        SELECT
            COALESCE(v.SerialNumber, 'SAP:'+LTRIM(RTRIM(v.SAPOutReferenceCode))) AS FaturaNo,
            COALESCE(t1.TahakkukTarihi, t2.TahakkukTarihi, v.InvoiceDate, v.ModifiedOn) AS EfektifTarih,
            v.TotalNetAmount AS NetTutar,
            v.AccountTitle AS Firma,
            1 AS VarunaEslesti,
            CASE WHEN COALESCE(t1.TahakkukTarihi, t2.TahakkukTarihi) IS NOT NULL THEN 1 ELSE 0 END AS TahakkukVar,
            1 AS IsSentetik
        FROM TBL_VARUNA_SIPARIS v
        LEFT JOIN TH t1 ON t1.SapReferansNo = LTRIM(RTRIM(v.SAPOutReferenceCode))
        LEFT JOIN TH t2 ON t2.FaturaNo = v.SerialNumber AND t1.TahakkukTarihi IS NULL
        WHERE v.OrderStatus = 'Closed' AND v.TotalNetAmount > 0
          AND v.DeletedOn IS NULL
          -- FaturaNo NULL üretmesin: SerialNumber veya SAPOutReferenceCode'dan en az biri dolu olmalı
          AND (v.SerialNumber IS NOT NULL OR (v.SAPOutReferenceCode IS NOT NULL AND LTRIM(RTRIM(v.SAPOutReferenceCode)) <> ''))
          AND (v.SerialNumber IS NULL OR v.SerialNumber NOT IN (SELECT Fatura_No FROM ViewFNSet))
          AND (v.SerialNumber IS NULL OR v.SerialNumber NOT IN (SELECT Fatura_No FROM IadeRetBL))
          AND COALESCE(t1.TahakkukTarihi, t2.TahakkukTarihi, v.InvoiceDate, v.ModifiedOn) IS NOT NULL
          AND (@Owner IS NULL OR v.ProposalOwnerId = @Owner)
    )

    -- 7) UNION + tarih filtresi
    SELECT FaturaNo, CAST(EfektifTarih AS DATE) AS EfektifTarih, NetTutar,
           Firma, VarunaEslesti, TahakkukVar, IsSentetik
    FROM (
        SELECT * FROM FaturalarJoin
        UNION ALL
        SELECT * FROM Sentetik
    ) Tum
    WHERE EfektifTarih >= @StartDate AND EfektifTarih < DATEADD(DAY,1,@EndDate)
    ORDER BY EfektifTarih, FaturaNo;
END;
");

                // ── SP_COCKPIT_TAHSILAT: Tahsilat kartı hesaplama SP ──
                // Deduplicate YOK — VIEW'deki tüm satırlar sayılır (Excel'de duplicate olması normal)
                await ExecuteSqlAsync(@"
CREATE OR ALTER PROCEDURE SP_COCKPIT_TAHSILAT
    @StartDate DATE,
    @EndDate   DATE
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        -- PAY: Tahsil_Tarihi donemde
        ISNULL(SUM(CASE WHEN Tahsil_Tarihi >= @StartDate AND Tahsil_Tarihi < DATEADD(DAY,1,@EndDate)
                        THEN ISNULL(Tahsil_Edilen, 0) END), 0) AS TahsilEdilen,

        -- PAY Adet
        ISNULL(SUM(CASE WHEN Tahsil_Tarihi >= @StartDate AND Tahsil_Tarihi < DATEADD(DAY,1,@EndDate)
                        THEN 1 END), 0) AS TahsilAdet,

        -- PAYDA bakiye: Fatura_Vade_Tarihi <= donem sonu, as-of @EndDate snapshot
        -- Bugunku bakiye + donem sonrasi tahsil edilen kisim (geriye donuk donemlerde oran sismesini engeller)
        ISNULL(SUM(CASE WHEN Fatura_Vade_Tarihi <= @EndDate
                        THEN ISNULL(Bekleyen_Bakiye, ISNULL(Fatura_Toplam,0) - ISNULL(Tahsil_Edilen,0))
                           + CASE WHEN Tahsil_Tarihi > @EndDate THEN ISNULL(Tahsil_Edilen, 0) ELSE 0 END
                        END), 0) AS BekleyenBakiyeToplam,

        -- Vade donemde fatura toplam
        ISNULL(SUM(CASE WHEN Fatura_Vade_Tarihi >= @StartDate AND Fatura_Vade_Tarihi < DATEADD(DAY,1,@EndDate)
                        THEN ISNULL(Fatura_Toplam, 0) END), 0) AS VadesiGelenToplam,

        -- Vade donemde fatura adet
        ISNULL(SUM(CASE WHEN Fatura_Vade_Tarihi >= @StartDate AND Fatura_Vade_Tarihi < DATEADD(DAY,1,@EndDate)
                        THEN 1 END), 0) AS VadesiGelenAdet,

        -- O Ay Bekleyen: vade dönemde + hâlâ bakiye var (sadece o ay net açık alacak)
        ISNULL(SUM(CASE WHEN Fatura_Vade_Tarihi >= @StartDate AND Fatura_Vade_Tarihi < DATEADD(DAY,1,@EndDate)
                         AND ISNULL(Bekleyen_Bakiye, ISNULL(Fatura_Toplam,0) - ISNULL(Tahsil_Edilen,0)) > 0
                        THEN ISNULL(Bekleyen_Bakiye, ISNULL(Fatura_Toplam,0) - ISNULL(Tahsil_Edilen,0)) END), 0) AS OAyBekleyenToplam,

        -- O Ay Bekleyen adet
        ISNULL(SUM(CASE WHEN Fatura_Vade_Tarihi >= @StartDate AND Fatura_Vade_Tarihi < DATEADD(DAY,1,@EndDate)
                         AND ISNULL(Bekleyen_Bakiye, ISNULL(Fatura_Toplam,0) - ISNULL(Tahsil_Edilen,0)) > 0
                        THEN 1 END), 0) AS OAyBekleyenAdet

    FROM VIEW_CP_EXCEL_FATURA
    WHERE ISNULL(LTRIM(RTRIM(Hukuki_Durum)), '') = ''
      AND LTRIM(RTRIM(ISNULL(Durum,''))) NOT IN (N'İADE',N'IADE',N'İPTAL',N'IPTAL',N'RET',N'İade Fatura',N'Iade Fatura');
END;
");

                // ── SP_COCKPIT_SOZLESME: Sözleşme kartı hesaplama SP ──
                await ExecuteSqlAsync(@"
CREATE OR ALTER PROCEDURE SP_COCKPIT_SOZLESME
    @StartDate DATE,
    @EndDate   DATE
AS
BEGIN
    SET NOCOUNT ON;

    -- 1) Eski sözleşmeler: FinishDate+1 dönemde olan (yenilenmesi gereken)
    --    Yeni sözleşme: RelatedContractId = eski.Id olan kayıt (ters bağlantı)
    -- 2) Bağsız yeni: StartDate dönemde, RelatedContractId NULL, eski olarak da listede yok
    ;WITH EskiSozlesme AS (
        SELECT s.Id, s.ContractNo, s.ContractName, s.ContractStatus, s.ContractType,
               s.AccountTitle, s.TotalAmount, s.TotalAmountLocal,
               s.FinishDate, s.RenewalDate, s.StartDate,
               DATEADD(DAY, 1, s.FinishDate) AS Yenilemetarihi
        FROM TBL_VARUNA_SOZLESME s
        WHERE s.RenewalDate IS NOT NULL
          AND s.DeletedOn IS NULL
          AND DATEADD(DAY, 1, s.FinishDate) >= @StartDate
          AND DATEADD(DAY, 1, s.FinishDate) <  DATEADD(DAY, 1, @EndDate)
    )
    SELECT
        N'Eski' AS Tipi,
        e.Id,
        e.ContractNo,
        -- Yenilendiyse yeni sozlesmenin adi, degilse eskinin adi
        COALESCE(y.YeniContractName, e.ContractName) AS ContractName,
        e.ContractStatus,
        e.AccountTitle AS Firma,
        e.TotalAmount AS EskiTutar,
        e.TotalAmountLocal AS EskiTutarLocal,
        e.FinishDate AS EskiBitis,
        e.ContractType AS EskiTip,
        e.Yenilemetarihi,
        CASE WHEN y.YeniId IS NOT NULL THEN 1 ELSE 0 END AS Yenilendi,
        y.YeniContractNo,
        y.YeniStatus,
        y.YeniTutar,
        y.YeniTutarLocal,
        y.YeniBaslangic,
        y.YeniBitis,
        y.YeniContractType AS YeniTip,
        y.YeniInvoiceStatusId,
        CASE
            WHEN y.YeniInvoiceStatusId = '588A659C-2766-4872-880B-3BCF772439BA' THEN N'Tamamlandı'
            WHEN y.YeniInvoiceStatusId = '41A14F17-BD82-4927-A29E-592AB37F6BB0' THEN N'Kısmi Faturalandı'
            WHEN y.YeniInvoiceStatusId = '53056965-D3EC-4C71-B968-6493A898A7CC' THEN N'Faturalanacak'
            ELSE N'Belirsiz'
        END AS FaturaStatu
    FROM EskiSozlesme e
    OUTER APPLY (
        SELECT TOP 1
            n.Id AS YeniId,
            n.ContractNo AS YeniContractNo,
            n.ContractName AS YeniContractName,
            n.ContractStatus AS YeniStatus,
            n.TotalAmount AS YeniTutar,
            n.TotalAmountLocal AS YeniTutarLocal,
            n.StartDate AS YeniBaslangic,
            n.FinishDate AS YeniBitis,
            n.ContractType AS YeniContractType,
            CAST(n.InvoiceStatusId AS NVARCHAR(50)) AS YeniInvoiceStatusId
        FROM TBL_VARUNA_SOZLESME n
        WHERE n.RelatedContractId = e.Id
          AND n.DeletedOn IS NULL
        ORDER BY n.StartDate ASC
    ) y

    UNION ALL

    -- Bağsız yeni: dönemde başlayan, RelatedContractId boş olan sözleşmeler.
    -- Eski-yeni bağı kurulmadığı için Eski listesinde de görünmüyor — bu blokla yakalanır.
    SELECT
        N'BagsizYeni' AS Tipi,
        b.Id,
        NULL AS ContractNo,
        b.ContractName,
        NULL AS ContractStatus,
        b.AccountTitle AS Firma,
        NULL AS EskiTutar,
        NULL AS EskiTutarLocal,
        NULL AS EskiBitis,
        NULL AS EskiTip,
        NULL AS Yenilemetarihi,
        0 AS Yenilendi,
        b.ContractNo AS YeniContractNo,
        b.ContractStatus AS YeniStatus,
        b.TotalAmount AS YeniTutar,
        b.TotalAmountLocal AS YeniTutarLocal,
        b.StartDate AS YeniBaslangic,
        b.FinishDate AS YeniBitis,
        b.ContractType AS YeniTip,
        CAST(b.InvoiceStatusId AS NVARCHAR(50)) AS YeniInvoiceStatusId,
        CASE
            WHEN b.InvoiceStatusId = '588A659C-2766-4872-880B-3BCF772439BA' THEN N'Tamamlandı'
            WHEN b.InvoiceStatusId = '41A14F17-BD82-4927-A29E-592AB37F6BB0' THEN N'Kısmi Faturalandı'
            WHEN b.InvoiceStatusId = '53056965-D3EC-4C71-B968-6493A898A7CC' THEN N'Faturalanacak'
            ELSE N'Belirsiz'
        END AS FaturaStatu
    FROM TBL_VARUNA_SOZLESME b
    WHERE b.RelatedContractId IS NULL
      AND b.StartDate >= @StartDate
      AND b.StartDate <  DATEADD(DAY, 1, @EndDate)
      AND b.DeletedOn IS NULL
    ORDER BY Tipi, EskiBitis, Firma;
END;
");

                // ── SP_PIPELINE_FIRSAT: Fırsat pipeline karti (K1 = havuz, K2 = dönem) ──
                // @Start/@End NULL gelirse tüm zamanlar; doluysa CloseDate bazlı filtre.
                await ExecuteSqlAsync(@"
CREATE OR ALTER PROCEDURE SP_PIPELINE_FIRSAT
    @StartDate DATE = NULL,
    @EndDate   DATE = NULL,
    @Owner     NVARCHAR(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        ISNULL(SUM(CASE WHEN OpportunityStageName NOT IN ('Won','Lost')
                         AND (OpportunityStageName IS NULL OR OpportunityStageName NOT LIKE '%Closed%')
                        THEN AmountAmount END), 0) AS TutarAcik,
        ISNULL(SUM(CASE WHEN OpportunityStageName NOT IN ('Won','Lost')
                         AND (OpportunityStageName IS NULL OR OpportunityStageName NOT LIKE '%Closed%')
                        THEN 1 END), 0) AS AdetAcik,
        ISNULL(SUM(CASE WHEN OpportunityStageName = 'Won' THEN AmountAmount END), 0) AS TutarWon,
        ISNULL(SUM(CASE WHEN OpportunityStageName = 'Won' THEN 1 END), 0) AS AdetWon,
        ISNULL(SUM(CASE WHEN OpportunityStageName = 'Lost' THEN AmountAmount END), 0) AS TutarLost,
        ISNULL(SUM(CASE WHEN OpportunityStageName = 'Lost' THEN 1 END), 0) AS AdetLost
    FROM TBL_VARUNA_OPPORTUNITIES
    WHERE DeletedOn IS NULL
      AND (Name IS NULL OR (Name NOT LIKE '%TEST%' AND Name NOT LIKE '%DENEME%'))
      AND (@StartDate IS NULL OR CloseDate >= @StartDate)
      AND (@EndDate   IS NULL OR CloseDate <= @EndDate)
      AND (@Owner     IS NULL OR OwnerId = @Owner);
END;
");

                // ── SP_PIPELINE_TEKLIF: Teklif pipeline karti (K3) ──
                // CreatedOn dönem filtresi, Denied/Reject hariç aktif teklifler.
                await ExecuteSqlAsync(@"
CREATE OR ALTER PROCEDURE SP_PIPELINE_TEKLIF
    @StartDate DATE,
    @EndDate   DATE,
    @Owner     NVARCHAR(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        ISNULL(SUM(CASE WHEN Status NOT IN ('Denied','Reject')
                        THEN TotalNetAmountLocalCurrency_Amount END), 0) AS TutarAktif,
        ISNULL(SUM(CASE WHEN Status NOT IN ('Denied','Reject') THEN 1 END), 0) AS AdetAktif,
        ISNULL(SUM(CASE WHEN Status IN ('Denied','Reject')
                        THEN TotalNetAmountLocalCurrency_Amount END), 0) AS TutarRed,
        ISNULL(SUM(CASE WHEN Status IN ('Denied','Reject') THEN 1 END), 0) AS AdetRed
    FROM TBL_VARUNA_TEKLIF
    WHERE DeletedOn IS NULL
      AND (Account_Title IS NULL OR (Account_Title NOT LIKE '%TEST%' AND Account_Title NOT LIKE '%DENEME%'))
      AND CreatedOn >= @StartDate AND CreatedOn <= @EndDate
      AND (@Owner IS NULL OR ProposalOwnerId = @Owner);
END;
");

                // ── SP_PIPELINE_SIPARIS: Sipariş pipeline karti (K4) ──
                // CreateOrderDate dönem filtresi, Canceled hariç, açık vs kapanmış ayrımı.
                await ExecuteSqlAsync(@"
CREATE OR ALTER PROCEDURE SP_PIPELINE_SIPARIS
    @StartDate DATE,
    @EndDate   DATE,
    @Owner     NVARCHAR(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        ISNULL(SUM(CASE WHEN OrderStatus <> 'Closed' THEN TotalNetAmount END), 0) AS TutarAcik,
        ISNULL(SUM(CASE WHEN OrderStatus <> 'Closed' THEN 1 END), 0) AS AdetAcik,
        ISNULL(SUM(CASE WHEN OrderStatus = 'Closed' THEN TotalNetAmount END), 0) AS TutarKapali,
        ISNULL(SUM(CASE WHEN OrderStatus = 'Closed' THEN 1 END), 0) AS AdetKapali
    FROM TBL_VARUNA_SIPARIS
    WHERE (AccountTitle IS NULL OR (AccountTitle NOT LIKE '%TEST%' AND AccountTitle NOT LIKE '%DENEME%'))
      AND OrderStatus <> 'Canceled'
      AND DeletedOn IS NULL
      AND CreateOrderDate >= @StartDate AND CreateOrderDate <= @EndDate;
END;
");

                // ── SP_FIRSAT_PIPELINE_V2: Tek SP, 5 pipeline kartı (K1-K5) ──
                await ExecuteSqlAsync(@"
CREATE OR ALTER PROCEDURE SP_FIRSAT_PIPELINE_V2
    @StartDate DATE = NULL,
    @EndDate   DATE = NULL,
    @Owner     NVARCHAR(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- K1: Tüm fırsatlar havuzu (dönem filtresi YOK — tüm açık havuz)
    --     Won + Lost hariç, kapalı siparişi olanlar hariç
    DECLARE @TumFirsatAdet INT, @TumFirsatTutar DECIMAL(18,2);

    ;WITH KapaliSiparisliOpp AS (
        SELECT DISTINCT LOWER(CAST(t.OpportunityId AS NVARCHAR(100))) AS OppId
        FROM TBL_VARUNA_TEKLIF t
        INNER JOIN TBL_VARUNA_SIPARIS s ON CAST(t.Id AS NVARCHAR(100)) = s.QuoteId
        WHERE t.DeletedOn IS NULL AND t.OpportunityId IS NOT NULL
          AND s.OrderStatus = 'Closed' AND s.QuoteId IS NOT NULL
          AND s.DeletedOn IS NULL
    )
    SELECT @TumFirsatAdet = COUNT(*),
           @TumFirsatTutar = ISNULL(SUM(o.AmountAmount), 0)
    FROM TBL_VARUNA_OPPORTUNITIES o
    WHERE o.DeletedOn IS NULL
      AND (o.Name IS NULL OR (o.Name NOT LIKE '%TEST%' AND o.Name NOT LIKE '%DENEME%'))
      AND o.OpportunityStageName NOT IN ('Won', 'Lost')
      AND (o.OpportunityStageName IS NULL OR o.OpportunityStageName NOT LIKE '%Closed%')
      AND LOWER(ISNULL(o.Id, '')) NOT IN (SELECT OppId FROM KapaliSiparisliOpp)
      AND (@Owner IS NULL OR o.OwnerId = @Owner);

    -- K2: Dönem fırsatları (CloseDate dönemde, Won+Lost hariç, kapalı siparişli hariç)
    DECLARE @FirsatAdet INT, @FirsatTutar DECIMAL(18,2);

    ;WITH KapaliSiparisliOpp AS (
        SELECT DISTINCT LOWER(CAST(t.OpportunityId AS NVARCHAR(100))) AS OppId
        FROM TBL_VARUNA_TEKLIF t
        INNER JOIN TBL_VARUNA_SIPARIS s ON CAST(t.Id AS NVARCHAR(100)) = s.QuoteId
        WHERE t.DeletedOn IS NULL AND t.OpportunityId IS NOT NULL
          AND s.OrderStatus = 'Closed' AND s.QuoteId IS NOT NULL
          AND s.DeletedOn IS NULL
    )
    SELECT @FirsatAdet = COUNT(*),
           @FirsatTutar = ISNULL(SUM(o.AmountAmount), 0)
    FROM TBL_VARUNA_OPPORTUNITIES o
    WHERE o.DeletedOn IS NULL
      AND (o.Name IS NULL OR (o.Name NOT LIKE '%TEST%' AND o.Name NOT LIKE '%DENEME%'))
      AND o.OpportunityStageName NOT IN ('Won', 'Lost')
      AND (o.OpportunityStageName IS NULL OR o.OpportunityStageName NOT LIKE '%Closed%')
      AND LOWER(ISNULL(o.Id, '')) NOT IN (SELECT OppId FROM KapaliSiparisliOpp)
      AND (@StartDate IS NULL OR o.CloseDate >= @StartDate)
      AND (@EndDate   IS NULL OR o.CloseDate <= @EndDate)
      AND (@Owner     IS NULL OR o.OwnerId = @Owner);

    -- K3: Dönem teklifleri (CreatedOn dönemde, Denied/Reject/Closed hariç)
    DECLARE @TeklifAdet INT, @TeklifTutar DECIMAL(18,2);
    SELECT @TeklifAdet = COUNT(*),
           @TeklifTutar = ISNULL(SUM(TotalNetAmountLocalCurrency_Amount), 0)
    FROM TBL_VARUNA_TEKLIF
    WHERE DeletedOn IS NULL
      AND (Account_Title IS NULL OR (Account_Title NOT LIKE '%TEST%' AND Account_Title NOT LIKE '%DENEME%'))
      AND Status NOT IN ('Denied', 'Reject', 'Closed')
      AND CreatedOn >= @StartDate AND CreatedOn <= @EndDate
      AND (@Owner IS NULL OR ProposalOwnerId = @Owner);

    -- K4: Dönem açık siparişleri (CreateOrderDate dönemde, Canceled hariç, Closed hariç)
    DECLARE @AcikSiparisAdet INT, @AcikSiparisTutar DECIMAL(18,2);
    SELECT @AcikSiparisAdet = COUNT(*),
           @AcikSiparisTutar = ISNULL(SUM(TotalNetAmount), 0)
    FROM TBL_VARUNA_SIPARIS
    WHERE (AccountTitle IS NULL OR (AccountTitle NOT LIKE '%TEST%' AND AccountTitle NOT LIKE '%DENEME%'))
      AND OrderStatus NOT IN ('Canceled', 'Closed')
      AND DeletedOn IS NULL
      AND CreateOrderDate >= @StartDate AND CreateOrderDate <= @EndDate;

    -- K5: Dönem kapalı siparişleri (CreateOrderDate dönemde, Closed)
    DECLARE @KapaliSiparisAdet INT, @KapaliSiparisTutar DECIMAL(18,2);
    SELECT @KapaliSiparisAdet = COUNT(*),
           @KapaliSiparisTutar = ISNULL(SUM(TotalNetAmount), 0)
    FROM TBL_VARUNA_SIPARIS
    WHERE (AccountTitle IS NULL OR (AccountTitle NOT LIKE '%TEST%' AND AccountTitle NOT LIKE '%DENEME%'))
      AND OrderStatus = 'Closed'
      AND DeletedOn IS NULL
      AND CreateOrderDate >= @StartDate AND CreateOrderDate <= @EndDate;

    -- DonemFirsatAdet: exclusive set kontrolü için
    DECLARE @DonemFirsatAdet INT;
    SELECT @DonemFirsatAdet = COUNT(*)
    FROM TBL_VARUNA_OPPORTUNITIES
    WHERE DeletedOn IS NULL
      AND (Name IS NULL OR (Name NOT LIKE '%TEST%' AND Name NOT LIKE '%DENEME%'))
      AND (@StartDate IS NULL OR CloseDate >= @StartDate)
      AND (@EndDate   IS NULL OR CloseDate <= @EndDate)
      AND (@Owner     IS NULL OR OwnerId = @Owner);

    -- Tek satır sonuç
    SELECT
        @TumFirsatAdet      AS TumFirsatAdet,
        @TumFirsatTutar     AS TumFirsatTutar,
        @FirsatAdet         AS FirsatAdet,
        @FirsatTutar        AS FirsatTutar,
        @TeklifAdet         AS TeklifAdet,
        @TeklifTutar        AS TeklifTutar,
        @AcikSiparisAdet    AS AcikSiparisAdet,
        @AcikSiparisTutar   AS AcikSiparisTutar,
        @KapaliSiparisAdet  AS KapaliSiparisAdet,
        @KapaliSiparisTutar AS KapaliSiparisTutar,
        @DonemFirsatAdet    AS DonemFirsatAdet;
END;
");

                // ── SP_HEDEF_URUN_AY_MATRIS: Ürün × Ay × (Toplam, YS, Yen) tek pass ──
                // Cockpit fatura listesi (otorite) + kalem oransal dağıtım + ürün eşleştirme
                // Hedef paneli ürün sekmesi + Şirket Özeti heatmap + UrunDetay aynı sonucu döndürür.
                await ExecuteSqlAsync(@"
CREATE OR ALTER PROCEDURE SP_HEDEF_URUN_AY_MATRIS
    @Yil INT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Start DATE = DATEFROMPARTS(@Yil, 1, 1);
    DECLARE @End   DATE = DATEFROMPARTS(@Yil, 12, 31);

    -- Cockpit fatura listesi (İade/Ret hariç, EfektifTarih, sentetik dahil)
    DECLARE @Fat TABLE (
        FaturaNo NVARCHAR(64),
        EfektifTarih DATE,
        NetTutar DECIMAL(38,4),
        Firma NVARCHAR(500),
        VarunaEslesti INT,
        TahakkukVar INT,
        IsSentetik INT
    );
    INSERT INTO @Fat (FaturaNo, EfektifTarih, NetTutar, Firma, VarunaEslesti, TahakkukVar, IsSentetik)
    EXEC SP_COCKPIT_FATURA @Start, @End, NULL;

    ;WITH
    -- Fatura → OrderId
    -- Sentetik fatura (SerialNumber NULL): FaturaNo='SAP:'+SAPOutReferenceCode formatında üretilir
    -- (SP_COCKPIT_FATURA Sentetik CTE içinde). Bu pattern olmadan BFG/Hakediş gibi sentetik
    -- kalemler kaybolur (örn. Turk Elektronik Para TPV faturaları YTD 891K → 0 görünüyordu).
    FaturaOrder AS (
        SELECT
            f.FaturaNo, f.EfektifTarih, f.NetTutar,
            s.OrderId,
            CASE WHEN dt.Code = 'ZZ08' THEN 1 ELSE 0 END AS IsYen
        FROM @Fat f
        INNER JOIN TBL_VARUNA_SIPARIS s
            ON COALESCE(s.SerialNumber, 'SAP:'+LTRIM(RTRIM(s.SAPOutReferenceCode))) = f.FaturaNo
            AND s.DeletedOn IS NULL
        LEFT JOIN TBL_VARUNA_SALESDOCUMENTTYPESAP dt ON dt.Id = s.SalesDocumentTypeSapId
    ),
    -- Kalem normalize: aynı (CrmOrderId, StockCode) için toplam (deduplicate)
    KalemAgg AS (
        SELECT CrmOrderId, StockCode, SUM(ISNULL(Total, 0)) AS Total
        FROM TBL_VARUNA_SIPARIS_URUNLERI
        WHERE CrmOrderId IS NOT NULL AND StockCode IS NOT NULL
        GROUP BY CrmOrderId, StockCode
    ),
    -- Sipariş bazlı toplam döviz (kalem oransı için)
    OrderDoviz AS (
        SELECT CrmOrderId, SUM(Total) AS ToplamDoviz
        FROM KalemAgg
        GROUP BY CrmOrderId
    ),
    -- TL dağıtım: kalem.Total / ToplamDoviz × NetTutar
    KalemTL AS (
        SELECT
            fo.EfektifTarih, fo.IsYen, k.StockCode,
            CASE WHEN od.ToplamDoviz > 0
                 THEN k.Total / od.ToplamDoviz * fo.NetTutar
                 ELSE 0 END AS KalemTutar
        FROM FaturaOrder fo
        INNER JOIN KalemAgg k ON k.CrmOrderId = fo.OrderId
        INNER JOIN OrderDoviz od ON od.CrmOrderId = fo.OrderId
    ),
    -- Ürün eşleştirme: StokKodu → TBLSOS_ANA_URUN.Ad → TBLSOS_HEDEF_URUN.Id
    -- (Panel TBLSOS_HEDEF_URUN.Id namespace kullanıyor — ANA_URUN.Id'den farklı olabiliyor.
    --  Ad-bazlı bridge ile her iki tablo eşlenir.)
    Eslesme AS (
        SELECT e.StokKodu, MIN(hu.Id) AS UrunId
        FROM TBLSOS_URUN_ESLESTIRME e
        INNER JOIN TBLSOS_ANA_URUN au ON au.Id = e.AnaUrunId
        INNER JOIN TBLSOS_HEDEF_URUN hu ON LTRIM(RTRIM(hu.Ad)) = LTRIM(RTRIM(au.Ad))
        WHERE e.StokKodu IS NOT NULL
        GROUP BY e.StokKodu
    )
    SELECT
        e.UrunId AS UrunId,
        CAST(MONTH(k.EfektifTarih) AS TINYINT) AS Ay,
        CAST(SUM(k.KalemTutar) AS DECIMAL(38,4)) AS Toplam,
        CAST(SUM(CASE WHEN k.IsYen = 0 THEN k.KalemTutar ELSE 0 END) AS DECIMAL(38,4)) AS YeniSatis,
        CAST(SUM(CASE WHEN k.IsYen = 1 THEN k.KalemTutar ELSE 0 END) AS DECIMAL(38,4)) AS Yenileme
    FROM KalemTL k
    INNER JOIN Eslesme e ON e.StokKodu = k.StockCode
    GROUP BY e.UrunId, MONTH(k.EfektifTarih)
    ORDER BY UrunId, Ay;
END;
");

                // ── SP_HEDEF_TEMSILCI_AY_MATRIS: Temsilci × Ay × (Toplam, YS, Yen) tek pass ──
                // 4-kademeli zincir (FA ResolveSalesRepName ile aynı semantik):
                //   1) ACCOUNT_REPRESENTATIVES.AccountOwnerId → CrmPersonId (Pri=0, ID-bazlı)
                //   2) ACCOUNT_REPRESENTATIVES.AccountOwnerId → Person.PersonNameSurname → Ad (Pri=1, ad-bazlı)
                //   3) Fırsat.CustomerRepresentativeId → CrmPersonId (Pri=2, fırsata özel atama — REPS yoksa)
                //   4) Fırsat.OwnerId → CrmPersonId (Pri=3, son çare — fırsat sahibi)
                // Fatura → Fırsat bağı: Sipariş.QuoteId → Teklif.OpportunityId → Opportunity
                await ExecuteSqlAsync(@"
CREATE OR ALTER PROCEDURE SP_HEDEF_TEMSILCI_AY_MATRIS
    @Yil INT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Start DATE = DATEFROMPARTS(@Yil, 1, 1);
    DECLARE @End   DATE = DATEFROMPARTS(@Yil, 12, 31);

    DECLARE @Fat TABLE (
        FaturaNo NVARCHAR(64),
        EfektifTarih DATE,
        NetTutar DECIMAL(38,4),
        Firma NVARCHAR(500),
        VarunaEslesti INT,
        TahakkukVar INT,
        IsSentetik INT
    );
    INSERT INTO @Fat (FaturaNo, EfektifTarih, NetTutar, Firma, VarunaEslesti, TahakkukVar, IsSentetik)
    EXEC SP_COCKPIT_FATURA @Start, @End, NULL;

    ;WITH
    -- Kademe 1+2: AccountId → REPS → temsilci (kanonik müşteri portföy ataması)
    AccTemsRaw AS (
        -- Pri=0: ACCOUNT_REPRESENTATIVES → Person.Id → CrmPersonId (ID-bazlı, yazim farkına dayanıklı)
        SELECT r.AccountId, t.Id AS TemsilciId, 0 AS Pri
        FROM TBL_VARUNA_ACCOUNT_REPRESENTATIVES r
        INNER JOIN TBLSOS_HEDEF_TEMSILCI t
            ON t.CrmPersonId IS NOT NULL
           AND LOWER(t.CrmPersonId) = LOWER(CAST(r.AccountOwnerId AS NVARCHAR(64)))
        WHERE r.AccountId IS NOT NULL
          AND r.State = 'Active'
          AND r.AccountOwnerId IS NOT NULL
        UNION ALL
        -- Pri=1: ACCOUNT_REPRESENTATIVES → Person.PersonNameSurname (ad-bazlı, son çare)
        SELECT r.AccountId, t.Id AS TemsilciId, 1 AS Pri
        FROM TBL_VARUNA_ACCOUNT_REPRESENTATIVES r
        INNER JOIN TBL_VARUNA_PERSON p ON p.Id = r.AccountOwnerId
        INNER JOIN TBLSOS_HEDEF_TEMSILCI t
            ON LTRIM(RTRIM(p.PersonNameSurname)) = LTRIM(RTRIM(t.Ad))
        WHERE r.AccountId IS NOT NULL
          AND r.State = 'Active'
          AND r.AccountOwnerId IS NOT NULL
    ),
    AccTems AS (
        -- Aynı AccountId için tek temsilci seç (CrmPersonId varsa o, yoksa ad-bazlı)
        SELECT AccountId, TemsilciId
        FROM (
            SELECT AccountId, TemsilciId, Pri,
                   RN = ROW_NUMBER() OVER (PARTITION BY AccountId ORDER BY Pri, TemsilciId)
            FROM AccTemsRaw
        ) x
        WHERE x.RN = 1
    ),
    -- Kademe 3+4: Sipariş → Teklif → Fırsat → CustomerRepresentativeId / OwnerId
    -- REPS'te eşleşme yoksa fırsat-bazlı atamalar devreye girer (FA ile uyumlu zincir).
    SipFirsatRaw AS (
        -- Pri=2: Fırsat.CustomerRepresentativeId
        SELECT s.OrderId, t.Id AS TemsilciId, 2 AS Pri
        FROM TBL_VARUNA_SIPARIS s
        INNER JOIN TBL_VARUNA_TEKLIF tk ON CAST(tk.Id AS NVARCHAR(64)) = s.QuoteId
        INNER JOIN TBL_VARUNA_OPPORTUNITIES o
            ON CAST(o.Id AS NVARCHAR(64)) = CAST(tk.OpportunityId AS NVARCHAR(64))
        INNER JOIN TBLSOS_HEDEF_TEMSILCI t
            ON t.CrmPersonId IS NOT NULL
           AND LOWER(t.CrmPersonId) = LOWER(CAST(o.CustomerRepresentativeId AS NVARCHAR(64)))
        WHERE s.OrderId IS NOT NULL AND s.QuoteId IS NOT NULL
          AND s.DeletedOn IS NULL
          AND o.DeletedOn IS NULL AND o.CustomerRepresentativeId IS NOT NULL
        UNION ALL
        -- Pri=3: Fırsat.OwnerId
        SELECT s.OrderId, t.Id AS TemsilciId, 3 AS Pri
        FROM TBL_VARUNA_SIPARIS s
        INNER JOIN TBL_VARUNA_TEKLIF tk ON CAST(tk.Id AS NVARCHAR(64)) = s.QuoteId
        INNER JOIN TBL_VARUNA_OPPORTUNITIES o
            ON CAST(o.Id AS NVARCHAR(64)) = CAST(tk.OpportunityId AS NVARCHAR(64))
        INNER JOIN TBLSOS_HEDEF_TEMSILCI t
            ON t.CrmPersonId IS NOT NULL
           AND LOWER(t.CrmPersonId) = LOWER(CAST(o.OwnerId AS NVARCHAR(64)))
        WHERE s.OrderId IS NOT NULL AND s.QuoteId IS NOT NULL
          AND s.DeletedOn IS NULL
          AND o.DeletedOn IS NULL AND o.OwnerId IS NOT NULL
    ),
    SipFirsatTems AS (
        -- Aynı OrderId için tek temsilci (CustRepId öncelik, OwnerId fallback)
        SELECT OrderId, TemsilciId
        FROM (
            SELECT OrderId, TemsilciId, Pri,
                   RN = ROW_NUMBER() OVER (PARTITION BY OrderId ORDER BY Pri, TemsilciId)
            FROM SipFirsatRaw
        ) x
        WHERE x.RN = 1
    ),
    FaturaTemsilci AS (
        -- INNER JOIN AccTems yerine LEFT + COALESCE: REPS yoksa fırsat-bazlı fallback devreye girer.
        -- WHERE COALESCE IS NOT NULL ile atanmamış faturalar elenmiş olur (eski INNER davranışı korunur).
        SELECT
            f.EfektifTarih, f.NetTutar,
            COALESCE(at.TemsilciId, sft.TemsilciId) AS TemsilciId,
            CASE WHEN dt.Code = 'ZZ08' THEN 1 ELSE 0 END AS IsYen
        FROM @Fat f
        -- Sentetik faturalarda FaturaNo = 'SAP:'+SAPOutReferenceCode formatında üretiliyor.
        INNER JOIN TBL_VARUNA_SIPARIS s
            ON COALESCE(s.SerialNumber, 'SAP:'+LTRIM(RTRIM(s.SAPOutReferenceCode))) = f.FaturaNo
            AND s.DeletedOn IS NULL
        LEFT JOIN AccTems at ON at.AccountId = s.AccountId
        LEFT JOIN SipFirsatTems sft ON sft.OrderId = s.OrderId
        LEFT JOIN TBL_VARUNA_SALESDOCUMENTTYPESAP dt ON dt.Id = s.SalesDocumentTypeSapId
        WHERE COALESCE(at.TemsilciId, sft.TemsilciId) IS NOT NULL
    )
    SELECT
        TemsilciId,
        CAST(MONTH(EfektifTarih) AS TINYINT) AS Ay,
        CAST(SUM(NetTutar) AS DECIMAL(38,4)) AS Toplam,
        CAST(SUM(CASE WHEN IsYen = 0 THEN NetTutar ELSE 0 END) AS DECIMAL(38,4)) AS YeniSatis,
        CAST(SUM(CASE WHEN IsYen = 1 THEN NetTutar ELSE 0 END) AS DECIMAL(38,4)) AS Yenileme
    FROM FaturaTemsilci
    GROUP BY TemsilciId, MONTH(EfektifTarih)
    ORDER BY TemsilciId, Ay;
END;
");

                // ── Raporlama ana ürünü kaldır: StockCode'ları Enroute/Quest'e taşı ──
                await ExecuteSqlAsync(
                    "UPDATE TBLSOS_URUN_ESLESTIRME SET AnaUrunId = 3 WHERE StokKodu = N'EH.05.002' AND AnaUrunId = 9"); // → Enroute
                await ExecuteSqlAsync(
                    "UPDATE TBLSOS_URUN_ESLESTIRME SET AnaUrunId = 5 WHERE StokKodu = N'QH.07.001' AND AnaUrunId = 9"); // → Quest
                await ExecuteSqlAsync(
                    "UPDATE TBLSOS_URUN_ESLESTIRME SET AnaUrunId = 5 WHERE StokKodu = N'QH.07.002' AND AnaUrunId = 9"); // → Quest

                // ╔═══════════════════════════════════════════════════════════════════╗
                // ║  2026 HEDEF SİSTEMİ — 600M Senaryosu                              ║
                // ║  6 yeni tablo, ID-bağlı, TBLSOS_ANA_URUN'dan bağımsız domain.     ║
                // ║  Senaryo → Ürün/Temsilci → Yıllık/Aylık olgu tabloları.           ║
                // ╚═══════════════════════════════════════════════════════════════════╝

                // 1) TBLSOS_HEDEF_SENARYO — boyut
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'TBLSOS_HEDEF_SENARYO') " +
                    "CREATE TABLE TBLSOS_HEDEF_SENARYO (" +
                    "  Id INT NOT NULL PRIMARY KEY, " +
                    "  Kod NVARCHAR(20) NOT NULL UNIQUE, " +
                    "  Ad NVARCHAR(100) NOT NULL, " +
                    "  Yil INT NOT NULL, " +
                    "  YillikToplam DECIMAL(18,2) NOT NULL, " +
                    "  Aktif BIT NOT NULL DEFAULT 1" +
                    ")");

                // 2) TBLSOS_HEDEF_URUN — boyut (hedef-domain ürün listesi)
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'TBLSOS_HEDEF_URUN') " +
                    "CREATE TABLE TBLSOS_HEDEF_URUN (" +
                    "  Id INT NOT NULL PRIMARY KEY, " +
                    "  Ad NVARCHAR(50) NOT NULL UNIQUE, " +
                    "  SiraNo INT NOT NULL, " +
                    "  Aktif BIT NOT NULL DEFAULT 1" +
                    ")");

                // 3) TBLSOS_HEDEF_TEMSILCI — boyut
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'TBLSOS_HEDEF_TEMSILCI') " +
                    "CREATE TABLE TBLSOS_HEDEF_TEMSILCI (" +
                    "  Id INT NOT NULL PRIMARY KEY, " +
                    "  Ad NVARCHAR(100) NOT NULL UNIQUE, " +
                    "  Kanal NVARCHAR(20) NOT NULL, " +
                    "  CrmPersonId NVARCHAR(64) NULL, " +
                    "  SiraNo INT NOT NULL, " +
                    "  Aktif BIT NOT NULL DEFAULT 1" +
                    ")");
                // Eski tablo varsa CrmPersonId kolonunu ekle
                await ExecuteSqlAsync(
                    "IF COL_LENGTH('TBLSOS_HEDEF_TEMSILCI', 'CrmPersonId') IS NULL " +
                    "ALTER TABLE TBLSOS_HEDEF_TEMSILCI ADD CrmPersonId NVARCHAR(64) NULL");

                // 4) TBLSOS_HEDEF_URUN_YILLIK — olgu (Senaryo × Ürün → yıllık YS/Yen/Toplam)
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'TBLSOS_HEDEF_URUN_YILLIK') " +
                    "CREATE TABLE TBLSOS_HEDEF_URUN_YILLIK (" +
                    "  Id INT IDENTITY(1,1) PRIMARY KEY, " +
                    "  SenaryoId INT NOT NULL FOREIGN KEY REFERENCES TBLSOS_HEDEF_SENARYO(Id), " +
                    "  UrunId INT NOT NULL FOREIGN KEY REFERENCES TBLSOS_HEDEF_URUN(Id), " +
                    "  HedefYeniSatis DECIMAL(18,2) NOT NULL, " +
                    "  HedefYenileme DECIMAL(18,2) NOT NULL, " +
                    "  HedefToplam DECIMAL(18,2) NOT NULL, " +
                    "  CONSTRAINT UQ_HedefUrunYillik UNIQUE (SenaryoId, UrunId)" +
                    ")");

                // 5) TBLSOS_HEDEF_URUN_AYLIK — olgu (Senaryo × Ürün × Ay × Tip)
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'TBLSOS_HEDEF_URUN_AYLIK') " +
                    "CREATE TABLE TBLSOS_HEDEF_URUN_AYLIK (" +
                    "  Id INT IDENTITY(1,1) PRIMARY KEY, " +
                    "  SenaryoId INT NOT NULL FOREIGN KEY REFERENCES TBLSOS_HEDEF_SENARYO(Id), " +
                    "  UrunId INT NOT NULL FOREIGN KEY REFERENCES TBLSOS_HEDEF_URUN(Id), " +
                    "  Ay TINYINT NOT NULL, " +
                    "  SatisTipi NVARCHAR(20) NOT NULL, " +
                    "  HedefTutar DECIMAL(18,2) NOT NULL, " +
                    "  CONSTRAINT UQ_HedefUrunAylik UNIQUE (SenaryoId, UrunId, Ay, SatisTipi)" +
                    ")");
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_HedefUrunAylik_Lookup') " +
                    "CREATE INDEX IX_HedefUrunAylik_Lookup ON TBLSOS_HEDEF_URUN_AYLIK (SenaryoId, Ay, UrunId)");

                // 6) TBLSOS_HEDEF_TEMSILCI_AYLIK — olgu (Senaryo × Temsilci × Ürün × Ay × Tip)
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'TBLSOS_HEDEF_TEMSILCI_AYLIK') " +
                    "CREATE TABLE TBLSOS_HEDEF_TEMSILCI_AYLIK (" +
                    "  Id BIGINT IDENTITY(1,1) PRIMARY KEY, " +
                    "  SenaryoId INT NOT NULL FOREIGN KEY REFERENCES TBLSOS_HEDEF_SENARYO(Id), " +
                    "  TemsilciId INT NOT NULL FOREIGN KEY REFERENCES TBLSOS_HEDEF_TEMSILCI(Id), " +
                    "  UrunId INT NOT NULL FOREIGN KEY REFERENCES TBLSOS_HEDEF_URUN(Id), " +
                    "  Ay TINYINT NOT NULL, " +
                    "  SatisTipi NVARCHAR(20) NOT NULL, " +
                    "  HedefTutar DECIMAL(18,2) NOT NULL, " +
                    "  CONSTRAINT UQ_HedefTemsilciAylik UNIQUE (SenaryoId, TemsilciId, UrunId, Ay, SatisTipi)" +
                    ")");
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_HedefTemsilciAylik_Lookup') " +
                    "CREATE INDEX IX_HedefTemsilciAylik_Lookup ON TBLSOS_HEDEF_TEMSILCI_AYLIK (SenaryoId, Ay, UrunId, TemsilciId)");

                // ── TBLSOS_LOGIN_AKTIVITE: Kullanıcı login/logout/aktivite kaydı ──
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'TBLSOS_LOGIN_AKTIVITE') " +
                    "CREATE TABLE TBLSOS_LOGIN_AKTIVITE (" +
                    "  Id BIGINT IDENTITY(1,1) PRIMARY KEY, " +
                    "  KullaniciId INT NOT NULL, " +
                    "  Email NVARCHAR(256) NULL, " +
                    "  AdSoyad NVARCHAR(256) NULL, " +
                    "  GirisZamani DATETIME NOT NULL DEFAULT GETDATE(), " +
                    "  CikisZamani DATETIME NULL, " +
                    "  SonAktiviteZamani DATETIME NOT NULL DEFAULT GETDATE(), " +
                    "  SureSaniye INT NOT NULL DEFAULT 0, " +
                    "  AktifMi BIT NOT NULL DEFAULT 1, " +
                    "  IPAdresi NVARCHAR(64) NULL, " +
                    "  UserAgent NVARCHAR(512) NULL" +
                    ")");
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_LOGIN_AKTIVITE_Kullanici_Aktif') " +
                    "CREATE INDEX IX_LOGIN_AKTIVITE_Kullanici_Aktif ON TBLSOS_LOGIN_AKTIVITE (KullaniciId, AktifMi, GirisZamani DESC)");
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_LOGIN_AKTIVITE_GirisZamani') " +
                    "CREATE INDEX IX_LOGIN_AKTIVITE_GirisZamani ON TBLSOS_LOGIN_AKTIVITE (GirisZamani DESC)");

                // ── Varuna performans index'leri (Fırsat Analizi + Cockpit hot path) ──
                // Tablolar küçük (1.5K-3K satır) ama JOIN/WHERE kombinasyonları çok yoğun.
                // Index'ler GetOpportunitySummary, GetFunnelBreakdown, GetKpiCore, SP_PIPELINE_*'ı hızlandırır.
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_VARUNA_OPP_CloseDate_Stage') " +
                    "CREATE NONCLUSTERED INDEX IX_VARUNA_OPP_CloseDate_Stage " +
                    "ON TBL_VARUNA_OPPORTUNITIES(CloseDate, OpportunityStageName) " +
                    "INCLUDE (Id, OwnerId, AccountId, AccountTitle, AmountAmount, ProductGroupId, DealType, Probability, Name, DeletedOn)");
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_VARUNA_OPP_CreatedOn') " +
                    "CREATE NONCLUSTERED INDEX IX_VARUNA_OPP_CreatedOn " +
                    "ON TBL_VARUNA_OPPORTUNITIES(CreatedOn) " +
                    "INCLUDE (Id, OpportunityStageName, DeletedOn)");
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_VARUNA_OPP_Stage') " +
                    "CREATE NONCLUSTERED INDEX IX_VARUNA_OPP_Stage " +
                    "ON TBL_VARUNA_OPPORTUNITIES(OpportunityStageName) " +
                    "INCLUDE (Id, DeletedOn)");
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_VARUNA_TEKLIF_Opp_Created') " +
                    "CREATE NONCLUSTERED INDEX IX_VARUNA_TEKLIF_Opp_Created " +
                    "ON TBL_VARUNA_TEKLIF(OpportunityId, CreatedOn) " +
                    "INCLUDE (Id, Status, Account_Title, ProposalOwnerId, TotalNetAmountLocalCurrency_Amount, DeletedOn)");
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_VARUNA_TEKLIF_Status') " +
                    "CREATE NONCLUSTERED INDEX IX_VARUNA_TEKLIF_Status " +
                    "ON TBL_VARUNA_TEKLIF(Status) " +
                    "INCLUDE (Id, OpportunityId, DeletedOn)");
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_VARUNA_SIPARIS_Quote_Status') " +
                    "CREATE NONCLUSTERED INDEX IX_VARUNA_SIPARIS_Quote_Status " +
                    "ON TBL_VARUNA_SIPARIS(QuoteId, OrderStatus) " +
                    "INCLUDE (OrderId, SerialNumber, SAPOutReferenceCode, AccountTitle, InvoiceDate, CreateOrderDate, TotalNetAmount, ProposalOwnerId)");
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_VARUNA_SIPARIS_Status_AccountTitle') " +
                    "CREATE NONCLUSTERED INDEX IX_VARUNA_SIPARIS_Status_AccountTitle " +
                    "ON TBL_VARUNA_SIPARIS(OrderStatus, AccountTitle) " +
                    "INCLUDE (SerialNumber, SAPOutReferenceCode, InvoiceDate, QuoteId)");
                await ExecuteSqlAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_VARUNA_SIPARIS_InvoiceDate') " +
                    "CREATE NONCLUSTERED INDEX IX_VARUNA_SIPARIS_InvoiceDate " +
                    "ON TBL_VARUNA_SIPARIS(InvoiceDate) " +
                    "INCLUDE (QuoteId, OrderStatus, SerialNumber, SAPOutReferenceCode)");

                // ── Cross-DB index: VeriOkumaDonusum.TBL_FINANS_FATURA ──
                // SP_COCKPIT_FATURA bu tabloya VIEW_CP_EXCEL_FATURA üzerinden erişiyor.
                // Index'siz cross-DB join 9s sürüyordu; Fatura_No üzerinde NCI ile ~1.2s'ye düşüyor.
                // Yetki yoksa try-catch sessizce atlar (canlı DB Admin'i manuel ekleyebilir).
                await ExecuteSqlAsync(@"
EXEC VeriOkumaDonusum.dbo.sp_executesql N'
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = ''IX_FINANS_FATURA_FaturaNo'')
  CREATE NONCLUSTERED INDEX IX_FINANS_FATURA_FaturaNo
  ON dbo.TBL_FINANS_FATURA(Fatura_No)
  INCLUDE (Fatura_Tarihi, Fatura_Toplam, Durum, Fatura_Vade_Tarihi, Tahsil_Edilen, Bekleyen_Bakiye)';");

                // ── Admin seed: melih.bulut → LNGKULLANICITIPI = 1 ──
                // Eğer AspNetUsers'ta melih.bulut varsa ve TBL_KULLANICI'da varsa, tipini admin (1) yap
                await ExecuteSqlAsync(
                    "UPDATE k SET k.LNGKULLANICITIPI = 1 " +
                    "FROM TBL_KULLANICI k " +
                    "INNER JOIN AspNetUsers u ON u.Id = k.LNGIDENTITYKOD " +
                    "WHERE (u.UserName = N'melih.bulut' OR u.Email LIKE N'melih.bulut%' OR u.NormalizedUserName = N'MELIH.BULUT') " +
                    "AND (k.LNGKULLANICITIPI IS NULL OR k.LNGKULLANICITIPI <> 1)");

                // ── Seed: 600M senaryo verileri (Excel: 2026 Hedefler_V07.xlsx) ──
                // Resource dosyası: Services/SeedData/HedefSeed_600M.sql
                // Idempotent: her INSERT IF NOT EXISTS pattern'iyle korunur
                await ExecuteSeedFileAsync("HedefSeed_600M.sql");

                _logger.LogInformation("SOS database migrations completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SOS database migrations failed - this may be due to permissions or migrations already applied");
                // Don't throw - allow application to start even if migrations fail
            }
        }

        private async Task ExecuteSqlAsync(string sql)
        {
            try
            {
                await _context.Database.ExecuteSqlRawAsync(sql);
                _logger.LogDebug("Executed migration: {Sql}", sql.Substring(0, Math.Min(50, sql.Length)) + "...");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Migration SQL failed: {Sql}", sql.Substring(0, Math.Min(50, sql.Length)));
                // Continue with other migrations even if one fails
            }
        }

        // Resource dosyasındaki seed SQL'i ';' ayırarak parça parça çalıştırır.
        // Tüm statement'lar IF NOT EXISTS ile idempotent kalmalı.
        private async Task ExecuteSeedFileAsync(string fileName)
        {
            try
            {
                var basePath = AppContext.BaseDirectory;
                var path = Path.Combine(basePath, "Services", "SeedData", fileName);
                if (!File.Exists(path))
                {
                    // Geliştirme modunda proje köküne göre fallback
                    var altPath = Path.Combine(Directory.GetCurrentDirectory(), "Services", "SeedData", fileName);
                    if (File.Exists(altPath)) path = altPath;
                    else
                    {
                        _logger.LogWarning("Seed file not found: {File}", fileName);
                        return;
                    }
                }

                var content = await File.ReadAllTextAsync(path);
                // ';' ile statement'lara böl. Her statement'ı ayrı çalıştır.
                var statements = content.Split(';', StringSplitOptions.RemoveEmptyEntries);
                int executed = 0;
                foreach (var raw in statements)
                {
                    var stmt = raw.Trim();
                    if (string.IsNullOrWhiteSpace(stmt)) continue;
                    // Yorum-only satırları atla
                    var noComments = string.Join('\n', stmt.Split('\n')
                        .Where(l => !l.TrimStart().StartsWith("--"))).Trim();
                    if (string.IsNullOrWhiteSpace(noComments)) continue;

                    await ExecuteSqlAsync(stmt);
                    executed++;
                }
                _logger.LogInformation("Seed file {File} executed: {Count} statements", fileName, executed);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Seed file execution failed: {File}", fileName);
            }
        }
    }
}
