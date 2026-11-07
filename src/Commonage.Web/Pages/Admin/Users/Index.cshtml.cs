using Commonage.Web.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages.Admin.Users;

public class IndexModel(UserManager<IdentityUser> users) : PageModel
{
    public record Account(string Id, string Name, string? Role, AccountState State, bool TwoFactor);

    public IReadOnlyList<Account> Accounts { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var list = new List<Account>();
        foreach (var user in await users.Users.OrderBy(u => u.NormalizedEmail).ToListAsync())
        {
            var roles = await users.GetRolesAsync(user);
            list.Add(new Account(user.Id, user.Email ?? user.UserName ?? user.Id,
                roles.Contains(Roles.Admin) ? Roles.Admin : roles.FirstOrDefault(),
                AccountStates.Of(user), user.TwoFactorEnabled));
        }
        Accounts = list;
    }
}

public enum AccountState
{
    Active,

    /// <summary>The person has not used their setup link yet.</summary>
    WaitingForSetup,

    Disabled,
}

public static class AccountStates
{
    public static AccountState Of(IdentityUser user) =>
        Identity.Accounts.IsDisabled(user) ? AccountState.Disabled
        : user.PasswordHash is null ? AccountState.WaitingForSetup
        : AccountState.Active;
}
