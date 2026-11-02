using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Commonage.Web.Pages;

/// <summary>
/// Shown for errors and for empty error responses such as "not found". It says what happened in plain words and
/// never shows technical details; those go to the log, where the reference number finds them.
/// </summary>
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public class ErrorModel : PageModel
{
    public int Status { get; private set; }
    public string? RequestId { get; private set; }

    public void OnGet(int? code) => Show(code);

    // Re-executed error responses keep the original method, so a failed form must find a handler too.
    public void OnPost(int? code) => Show(code);

    private void Show(int? code)
    {
        Status = code ?? StatusCodes.Status500InternalServerError;
        if (Status >= 500)
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
    }
}
