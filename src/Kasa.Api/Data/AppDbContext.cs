using System.Linq.Expressions;
using Kasa.Api.Common;
using Kasa.Api.Domain;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Kasa.Api.Data;

/// <summary>
/// Identity's user tables without roles (a user has one role, stored on the user) plus the company data.
/// Every <see cref="ITenantEntity"/> gets a query filter on the current company, and saving refuses rows of
/// another company, so forgetting a "where CompanyId = ..." can neither show nor change another company's data.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options, TenantContext tenant)
    : IdentityUserContext<AppUser, Guid>(options)
{
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Invitation> Invitations => Set<Invitation>();

    /// <summary>Read by the query filters each time a query runs, so one model serves every company.</summary>
    internal Guid? CurrentCompanyId => tenant.CompanyId;

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);

        model.Entity<Company>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(Company.NameMaxLength);
            e.Property(c => c.TaxNumber).HasMaxLength(11);
            e.Property(c => c.ErpFirmCode).HasMaxLength(10);
        });

        model.Entity<AppUser>(e =>
        {
            e.Property(u => u.FullName).HasMaxLength(AppUser.FullNameMaxLength);
            e.Property(u => u.Role).HasConversion<string>().HasMaxLength(20);
            e.HasOne(u => u.Company).WithMany().HasForeignKey(u => u.CompanyId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(u => u.CompanyId);
        });

        model.Entity<RefreshToken>(e =>
        {
            e.Property(t => t.TokenHash).HasMaxLength(64);
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.FamilyId);
            e.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Invitation>(e =>
        {
            e.Property(i => i.Email).HasMaxLength(256);
            e.Property(i => i.Role).HasConversion<string>().HasMaxLength(20);
            e.Property(i => i.TokenHash).HasMaxLength(64);
            e.HasIndex(i => i.TokenHash).IsUnique();
            e.HasOne<Company>().WithMany().HasForeignKey(i => i.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        // One filter per tenant entity, on whatever entities exist now and are added later.
        foreach (var type in model.Model.GetEntityTypes().Where(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType)))
        {
            var row = Expression.Parameter(type.ClrType, "row");
            var companyId = Expression.Convert(Expression.Property(row, nameof(ITenantEntity.CompanyId)), typeof(Guid?));
            var current = Expression.Property(Expression.Constant(this), nameof(CurrentCompanyId));
            type.SetQueryFilter(Expression.Lambda(Expression.Equal(companyId, current), row));
        }
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardTenant();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardTenant();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <summary>New rows take the current company; a row of another company cannot be added, changed or deleted.</summary>
    private void GuardTenant()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantEntity>())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached)
            {
                continue;
            }

            var current = tenant.CompanyId
                ?? throw new InvalidOperationException($"Cannot save {entry.Entity.GetType().Name}: no company is set for this work.");

            if (entry.State == EntityState.Added && entry.Entity.CompanyId == Guid.Empty)
            {
                entry.Entity.CompanyId = current;
            }

            if (entry.Entity.CompanyId != current)
            {
                throw new InvalidOperationException(
                    $"Cannot save {entry.Entity.GetType().Name} of company {entry.Entity.CompanyId} while working for company {current}.");
            }
        }
    }
}
