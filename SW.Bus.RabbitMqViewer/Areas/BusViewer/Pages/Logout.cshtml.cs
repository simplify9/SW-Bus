using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SW.Bus.RabbitMqViewer.Areas.BusViewer.Pages;

/// <summary>Signs the user out of the Bus Viewer session and redirects to the login page.</summary>
[AllowAnonymous]
public class LogoutModel : PageModel
{
    public async Task<IActionResult> OnGetAsync()
    {
        await HttpContext.SignOutAsync(BusViewerExtensions.CookieSchemeName);
        return Redirect("/bus-viewer/login");
    }
}
