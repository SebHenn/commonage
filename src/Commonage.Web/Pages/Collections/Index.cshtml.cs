using Commonage.Web.Data;
using Commonage.Web.Search;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Commonage.Web.Pages.Collections;

public class IndexModel(CommonageDbContext db) : PageModel
{
    public IReadOnlyList<CollectionCard> Collections { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Collections = await db.LoadCollectionCardsAsync(cancellationToken);
}
