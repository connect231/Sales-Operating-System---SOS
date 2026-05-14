using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SOS.Controllers;

[Authorize]
public class RaporController : Controller
{
    [HttpGet]
    public IActionResult Index() => View();
}
