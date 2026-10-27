using Microsoft.AspNetCore.Identity;

namespace Commonage.Web.Identity;

/// <summary>
/// Creates the first administrator from configuration (InitialAdmin:Email / InitialAdmin:Password)
/// when the archive has no users yet. After that, the settings are ignored.
/// </summary>
public static class InitialAdminSeeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration, ILogger logger)
    {
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { Roles.Admin, Roles.Curator })
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));
        }

        var userManager = services.GetRequiredService<UserManager<IdentityUser>>();
        if (userManager.Users.Any())
            return;

        var email = configuration["InitialAdmin:Email"];
        var password = configuration["InitialAdmin:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning(
                "No users exist yet. Set InitialAdmin:Email and InitialAdmin:Password to create the first administrator.");
            return;
        }

        var admin = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
        var result = await userManager.CreateAsync(admin, password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                "Could not create the initial administrator: " + string.Join(" ", result.Errors.Select(e => e.Description)));
        }

        await userManager.AddToRoleAsync(admin, Roles.Admin);
        logger.LogInformation("Created the initial administrator account.");
    }
}
