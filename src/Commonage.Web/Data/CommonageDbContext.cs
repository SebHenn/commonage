using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Data;

public class CommonageDbContext(DbContextOptions<CommonageDbContext> options) : DbContext(options)
{
}
