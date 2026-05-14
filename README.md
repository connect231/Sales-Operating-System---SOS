# SOS — Sales Operating System

Finansal cockpit dashboard uygulaması. ASP.NET Core MVC (.NET 10), Razor Views, Tailwind CSS, SQL Server.

## Geliştirme

```bash
# Bağımlılıklar (ilk kurulum)
npm install
dotnet restore

# Secrets (connection string, SMTP vs.)
dotnet user-secrets set "ConnectionStrings:MsKConnection" "<cs>"
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "<cs>"
dotnet user-secrets set "Email:Host" "smtp.gmail.com"
dotnet user-secrets set "Email:Username" "<email>"
dotnet user-secrets set "Email:Password" "<password>"

# Çalıştır
dotnet run --project SOS.csproj          # http://localhost:5165
npm run build:css                        # Tailwind watch (ayrı terminal)
```

## Proje Yapısı

- **Backend**: `Controllers/`, `Services/`, `DbData/`, `Models/`
- **Frontend**: `Views/Cockpit/Index.cshtml` (ana dashboard), `Views/Shared/_Layout.cshtml`
- **Configuration**: `appsettings.json` + `appsettings.Development.json` (placeholder) + **user-secrets**

## Dokümantasyon

- **`CLAUDE.md`** — proje özeti, çekirdek mimari invariant'lar, **konu → belge yönlendirme tablosu** (kısa kök).
- **`AGENTS.md`** — operasyonel kurallar, yasaklar, agent kullanım rehberi.
- **`TODO.md`** — teknik borç / gelecekteki iyileştirmeler.
- **`Docs/architecture/`** — modül başına detaylı mimari ve iş mantığı belgeleri.
  - `01-veri-akisi.md` — fatura, ürün dağılımı, Varuna eşleşmesi.
  - `02-stored-procedures.md` — SP'ler, cache, `ICockpitDataService`.
  - `03-tahakkuk.md` — SAP bazlı tarih override sistemi.
  - `04-tahsilat-cei.md` — tahsilat, vade, CEI hesabı.
  - `05-hedef-sistemi.md` — hedef tabloları, ana ürünler, ürün eşleşme.
  - `06-firsat-analiz.md` — fırsat hunisi ve Cockpit tutarlılığı.
- **`Docs/conventions/`** — projeye özel sabit kurallar.
  - `ui-locale-tr.md` — Türkçe UI, locale, format kuralları.
  - `data-rules.md` — iade/ret, hukuki takip, deduplikasyon.
- **`Docs/FirsatAnaliz.md`**, **`Docs/FirsatAnaliz_KodReferans.md`** — Fırsat Analiz ekran detayları ve kod referansı.
- **`.claude/agents/`** — proje-özel subagent tanımları (dotnet-cockpit-engineer, finans-hesaplama-auditor, sql-ef-query-pro, razor-ui-polisher).
