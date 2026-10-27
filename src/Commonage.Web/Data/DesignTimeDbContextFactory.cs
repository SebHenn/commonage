using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Commonage.Web.Data;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> create the context without running the app.
/// Creating a migration never connects to the database, so no credentials are needed here.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CommonageDbContext>
{
    public CommonageDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CommonageDbContext>().UseNpgsql("Host=localhost;Database=commonage").Options);
}
