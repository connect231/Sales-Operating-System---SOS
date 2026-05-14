# SOS2 Agent Rota Haritası

> Bu belge `/sos-yap` orkestratörünün karar mantığını **görsel** olarak gösterir. Hangi soruda hangi katmana, hangi belgeye, hangi subagent'a gidildiğini tek bakışta gör.
>
> Routing'in resmi kaynağı: `AGENTS.md` → "Hangi Agent Ne İçin?" tablosu. Bu belge onun görselleştirilmiş hali.

---

## 1. Üst Seviye Akış (Mermaid)

```mermaid
flowchart TD
    Start([Kullanıcı: /sos-yap "...."]) --> CTX[1. Bağlam yükle<br/>CLAUDE.md + AGENTS.md<br/>+ ilgili Docs/architecture/*]
    CTX --> Intent{2. Niyet analizi}
    Intent -->|Belirsiz| Ask[Batched netleştirme<br/>en fazla 3 soru]
    Intent -->|Net| Plan[3. Plan: 2-5 dk adımlar]
    Ask --> Plan
    Plan --> Route{4. Routing}

    Route -->|UI / kart / Razor| UI[razor-ui-polisher]
    Route -->|Endpoint / controller / service| BE[dotnet-cockpit-engineer]
    Route -->|Hesap tutmuyor / sapma| Audit[finans-hesaplama-auditor]
    Route -->|Yavaş sorgu / migration / SP| SQL[sql-ef-query-pro]
    Route -->|Genel keşif| Explore[Explore agent]

    UI --> Validate
    BE --> Validate
    Audit --> Validate
    SQL --> Validate
    Explore --> Validate

    Validate{5. Doğrulama zinciri} --> V1[dotnet build /warnaserror]
    V1 --> V2[lsp diagnostics]
    V2 --> V3{Finansal değişti?}
    V3 -->|Evet| V3a[finans-hesaplama-auditor]
    V3 -->|Hayır| V4
    V3a --> V4{UI değişti?}
    V4 -->|Evet| V4a[bg_shell + chrome-devtools screenshot]
    V4 -->|Hayır| V5
    V4a --> V5{Yüksek risk?}
    V5 -->|Evet| V5a[/security-review skill]
    V5 -->|Hayır| V6
    V5a --> V6[/review skill]
    V6 --> Report[6. Konsolide rapor:<br/>Yapıldı + Değişen + Doğrulama<br/>+ Karar Gerekenler]
```

---

## 2. Soru → Agent → Belge — Hızlı Tablo

| Sizin yazdığınız (örnek) | Routed agent | Yüklenen belge | Çağrılacak skill (varsa) |
|---|---|---|---|
| "CockpitController'a yeni AJAX endpoint ekle" | `dotnet-cockpit-engineer` | `02-stored-procedures.md` | `/review` |
| "Tahsilat kartı tutmuyor, neden farklı?" | `finans-hesaplama-auditor` (önce) → `dotnet-cockpit-engineer` (düzeltme) | `04-tahsilat-cei.md` + `data-rules.md` | `/review` |
| "Fırsat analizinde funnel dip toplamı sapması var" | `finans-hesaplama-auditor` | `06-firsat-analiz.md` | — (sadece denetim) |
| "FirsatAnaliz GetFunnelBreakdown çok yavaş" | `sql-ef-query-pro` | `02-stored-procedures.md` | `/review` |
| "Yeni `TBLSOS_KOTA` tablosu eklensin" | `sql-ef-query-pro` (DatabaseMigrationService'e raw SQL) | `02-stored-procedures.md` + `05-hedef-sistemi.md` | `/review` + `/security-review` (yeni şema → injection riski) |
| "Hedef kartına 'kalan iş günü' badge'i koy" | `razor-ui-polisher` | `ui-locale-tr.md` | `/review` |
| "Sayı sayma animasyonu kekliyor, 60fps olsun" | `razor-ui-polisher` | `ui-locale-tr.md` | `/review` |
| "Tahakkuk import butonu eklensin" | `dotnet-cockpit-engineer` (backend) → `razor-ui-polisher` (UI) | `03-tahakkuk.md` + `ui-locale-tr.md` | `/review` + `/security-review` (dosya upload) |
| "İade faturalar tahsilatta gözüküyor sanırım" | `finans-hesaplama-auditor` | `04-tahsilat-cei.md` + `data-rules.md` | — |
| "Login ekranına 2FA ekle" | `dotnet-cockpit-engineer` | (yeni Doc gerekir) | `/security-review` zorunlu + `/review` |
| "Hangi controller LoadAllCachedDataAsync çağırıyor?" | `Explore` | — | — |
| "Yeni metrik kartı: 'Hukuki Takipteki Tutar'" | Zincir: `sql-ef-query-pro` → `dotnet-cockpit-engineer` → `razor-ui-polisher` | `01-veri-akisi.md` + `04-tahsilat-cei.md` + `ui-locale-tr.md` | `/review` |

---

## 3. ASCII — Tek Bakışta Karar Matrisi

```
┌─────────────────────────────────────────────────────────────────────┐
│                       İSTEKTEKİ ANAHTAR KELİME                      │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  [tutmuyor] [sapma] [hesap]            ──→  finans-hesaplama-       │
│  [neden farklı] [denetim]                   auditor (sadece okur)   │
│                                                                     │
│  [yavaş] [N+1] [sorgu]                 ──→  sql-ef-query-pro        │
│  [yeni tablo] [migration] [SP] [view]                               │
│                                                                     │
│  [endpoint] [controller] [service]     ──→  dotnet-cockpit-         │
│  [tahakkuk] [hedef] [auth]                  engineer                │
│                                                                     │
│  [UI] [kart] [badge] [animasyon]       ──→  razor-ui-polisher       │
│  [Razor] [Tailwind] [filtre nav]                                    │
│                                                                     │
│  [nerede] [hangi dosya] [kim çağırıyor] ──→ Explore agent           │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────┐
│                  ZORUNLU DOĞRULAMA ZİNCİRİ                          │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│   1. dotnet build SOS.csproj /warnaserror      ──→  zero error      │
│   2. lsp diagnostics                            ──→  clean          │
│   3. Finansal? ─→ finans-hesaplama-auditor      ──→  ₺1 sapma yok   │
│   4. UI?       ─→ chrome-devtools screenshot    ──→  görsel kanıt   │
│   5. Riskli?   ─→ /security-review (Anthropic)  ──→  bulgu yok      │
│   6. Her zaman ─→ /review (Anthropic)           ──→  öneriler       │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 4. Bağlam Yükleme Mekanizması (Hafıza)

Orkestratör **her oturumda otomatik yükler**:

| Belge | Ne zaman | Neden |
|---|---|---|
| `CLAUDE.md` | Her zaman (Claude Code otomatik) | Proje invariant'ları |
| `AGENTS.md` | Her zaman (`/sos-yap` ilk adımda okur) | Yasaklar + routing tablosu |
| `Docs/architecture/01-06` | İstekteki anahtar kelimeye göre koşullu | Modül detayı |
| `Docs/conventions/*` | UI veya veri kuralı içeren isteklerde | Türkçe locale + dedupe kuralları |
| Subagent dosyaları (`.claude/agents/*.md`) | Subagent çağrıldığında otomatik | Domain mandate |

**Yaşayan hafıza:**
- Yeni mimari belgesi açıldığında → `CLAUDE.md` yönlendirme tablosuna **mutlaka** eklenmeli
- Yeni subagent eklendiğinde → `AGENTS.md` "Hangi Agent" tablosuna eklenmeli + bu haritada bir satır

Bu kural sayesinde hafıza **kendiliğinden güncel kalır** — eklediğin her belge otomatik routing'e girer.

---

## 5. Anti-Pattern'ler (Yapma!)

| Yanlış davranış | Doğrusu |
|---|---|
| Kullanıcıya her adımda tek soru sormak | Batched, en fazla 3 soru tek mesajda |
| "Yapıldı" deyip doğrulama atlamak | Build + auditor + screenshot zorunlu |
| Kendi başına tüm katmanlara dalmak | Subagent'a iş ver, koordine et |
| Birden fazla belge yüklemekten kaçınmak | Şüpheliyse 2 belge yükle (context bütçesi sonra düşünülür) |
| `/security-review` sadece "auth değişti" denince çağırmak | Yeni dış girdi, yeni şema, yeni dosya upload da risk |
| `/review`'i unutmak | Her teslimde son adım |
| AGENTS.md'deki 13 yasağı hatırlamamak | Plan adımında her birine karşı checklist |

---

## 6. Hızlı Komut Referansı

```bash
# Orkestratörü çağır
/sos-yap <istek>

# Örnekler
/sos-yap Tahsilat kartına vade aşımı badge'i ekle
/sos-yap Fırsat analizinde Q2 dip toplamı tutmuyor, neden?
/sos-yap GetFunnelBreakdown 8 saniye sürüyor, optimize et
/sos-yap Yeni "Hukuki Takipteki Tutar" kartı tasarla ve veriyi bağla

# Manuel subagent çağırma (orkestratörü atlamak istersen)
@dotnet-cockpit-engineer <iş>
@finans-hesaplama-auditor <iş>
@sql-ef-query-pro <iş>
@razor-ui-polisher <iş>

# Anthropic resmi skill'ler (orkestratör otomatik çağırır, manuel de mümkün)
/review              # PR/değişiklik review
/security-review     # güvenlik denetimi
```

---

## 7. Akışın Mantığı — Tek Cümle

> **Sen niyet ver, orkestratör doğru subagent'ı + doğru belgeyi + doğru doğrulamayı bağlar, tek konsolide raporla geri döner.**

Kafanda tutman gereken tek şey: `/sos-yap "...."`. Geri kalan haritada.
