using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Commonage.Web.Pages.People;

/// <summary>
/// A person as visitors read them. A key that is not 12 characters of the alphabet gets the answer of every other key nobody may
/// use, without a question to the database; an unknown key, a hidden person, one in the bin and one whose items are all hidden
/// give that same <c>NotFound()</c>. No date of change, ETag or Last-Modified is sent: none may follow what a hidden person or item does.
/// </summary>
public class DetailsModel(CommonageDbContext db) : PageModel
{
    public PersonView View { get; private set; } = null!;

    [BindProperty(SupportsGet = true, Name = QueryUrls.PageKey)]
    public int PageNumber { get; set; } = 1;

    public async Task<IActionResult> OnGetAsync(string key, CancellationToken cancellationToken)
    {
        if (!Person.IsPublicId(key))
            return NotFound();

        var person = await VisiblePeople.PageAsync(db, key, PageNumber, cancellationToken);
        if (person is null)
            return NotFound();

        View = person;
        // Someone who may be living stays out of search engines, by the header as well as by the page (_Layout reads NoIndex).
        if (!person.HasDied)
        {
            Response.Headers["X-Robots-Tag"] = "noindex";
            ViewData["NoIndex"] = true;
        }
        return Page();
    }
}
