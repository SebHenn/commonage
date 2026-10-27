using Commonage.Web.Domain;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Data;

public class CommonageDbContext(DbContextOptions<CommonageDbContext> options)
    : IdentityDbContext<IdentityUser>(options), IDataProtectionKeyContext
{
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<ArchiveItem> ArchiveItems => Set<ArchiveItem>();

    // Keys that encrypt login cookies. Stored in the database so logins survive container rebuilds.
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    /// <summary>Items the public may see. Every public page must start from this query.</summary>
    public IQueryable<ArchiveItem> PublishedItems => ArchiveItems.Where(i => i.Status == ItemStatus.Published);

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Collection>(collection =>
        {
            collection.Property(c => c.Title).HasMaxLength(200);
            collection.Property(c => c.Description).HasMaxLength(4000);
        });

        builder.Entity<ArchiveItem>(item =>
        {
            item.Property(i => i.Title).HasMaxLength(300);
            item.Property(i => i.Description).HasMaxLength(20000);
            item.HasIndex(i => i.Status);
            item.HasOne(i => i.Collection)
                .WithMany(c => c.Items)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
