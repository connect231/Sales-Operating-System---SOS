using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using SOS.DbData;
using SOS.Models.MsK;

namespace SOS.Controllers
{
    [Authorize]
    public class ParametreController : Controller
    {
        private readonly IDbContextFactory<MskDbContext> _contextFactory;

        public ParametreController(IDbContextFactory<MskDbContext> contextFactory)
        {
            _contextFactory = contextFactory;
        }

        public async Task<IActionResult> Index(int? yil)
        {
            using var db = _contextFactory.CreateDbContext();
            var now = DateTime.Now;
            var selectedYil = yil ?? now.Year;

            var hedefler = await db.TBLSOS_HEDEF_AYLIKs
                .AsNoTracking()
                .Where(h => h.Yil == selectedYil && h.Tip == "GENEL")
                .OrderBy(h => h.Ay)
                .ToListAsync();

            var anaUrunler = await db.TBLSOS_ANA_URUNs
                .AsNoTracking()
                .Where(u => u.Aktif)
                .OrderBy(u => u.Sira)
                .ToListAsync();

            ViewBag.SelectedYil = selectedYil;
            ViewBag.AnaUrunler = anaUrunler;
            ViewBag.Hedefler = hedefler;
            ViewBag.ToplamHedef = hedefler.Sum(h => h.HedefTutar);

            return View();
        }

        [HttpPost]
        public async Task<IActionResult> SaveHedefler([FromBody] HedefSaveModel model)
        {
            if (model?.Hedefler == null || model.Hedefler.Count == 0)
                return BadRequest(new { error = "Veri bulunamadı" });

            using var db = _contextFactory.CreateDbContext();

            foreach (var h in model.Hedefler)
            {
                var existing = await db.TBLSOS_HEDEF_AYLIKs
                    .FirstOrDefaultAsync(x => x.Yil == h.Yil && x.Ay == h.Ay && x.Tip == "GENEL" && x.AnaUrunId == null);

                if (existing != null)
                {
                    existing.HedefTutar = h.HedefTutar;
                }
                else
                {
                    db.TBLSOS_HEDEF_AYLIKs.Add(new TBLSOS_HEDEF_AYLIK
                    {
                        Yil = h.Yil,
                        Ay = h.Ay,
                        Tip = "GENEL",
                        AnaUrunId = null,
                        HedefTutar = h.HedefTutar,
                        Aktif = true
                    });
                }
            }

            await db.SaveChangesAsync();
            return Json(new { success = true, toplam = model.Hedefler.Sum(h => h.HedefTutar) });
        }

        public class HedefSaveModel
        {
            public List<HedefItem> Hedefler { get; set; } = new();
        }

        public class HedefItem
        {
            public int Yil { get; set; }
            public int Ay { get; set; }
            public decimal HedefTutar { get; set; }
        }
    }
}
