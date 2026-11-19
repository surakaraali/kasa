using System.Linq.Expressions;
using Kasa.Api.Common;
using Kasa.Api.Domain;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Kasa.Api.Data;

/// <summary>
/// Identity's user tables without roles (a user has one role, stored on the user) plus the company data.
/// Every <see cref="ITenantEntity"/> gets a query filter on the current company, and saving refuses rows of
/// another company, so forgetting a "where CompanyId = ..." can neither show nor change another company's data.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options, TenantContext tenant)
    : IdentityUserContext<AppUser, Guid>(options), IDataProtectionKeyContext
{
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<BankConnection> BankConnections => Set<BankConnection>();
    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();
    public DbSet<BankTransaction> BankTransactions => Set<BankTransaction>();

    /// <summary>The ASP.NET Core Data Protection key ring, shared by every API instance.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

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

        model.Entity<BankConnection>(e =>
        {
            e.Property(c => c.Bank).HasConversion<string>().HasMaxLength(20);
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(c => c.LastSyncOutcome).HasConversion<string>().HasMaxLength(20);
            e.Property(c => c.LastSyncError).HasMaxLength(BankConnection.ErrorMaxLength);
            e.Property(c => c.ConsentId).HasMaxLength(100);
            e.Property(c => c.StateHash).HasMaxLength(64);
            e.Property(c => c.AuthorizationUrl).HasMaxLength(2000);
            foreach (var secret in new[] { nameof(BankConnection.CodeVerifierProtected), nameof(BankConnection.AccessTokenProtected),
                         nameof(BankConnection.RefreshTokenProtected), nameof(BankConnection.UsernameProtected), nameof(BankConnection.PasswordProtected) })
            {
                e.Property(secret).HasMaxLength(2000);
            }

            // One live connection per bank per company, enforced by the database so two clicks at once cannot make two.
            e.HasIndex(c => new { c.CompanyId, c.Bank }).IsUnique().HasFilter("[Status] <> 'Removed'");
            e.HasIndex(c => c.StateHash).IsUnique().HasFilter("[StateHash] IS NOT NULL");
            e.HasOne<Company>().WithMany().HasForeignKey(c => c.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<BankAccount>(e =>
        {
            e.Property(a => a.ExternalId).HasMaxLength(64);
            e.Property(a => a.Iban).HasMaxLength(34);
            e.Property(a => a.Name).HasMaxLength(200);
            e.Property(a => a.Currency).HasMaxLength(3);
            e.Property(a => a.Balance).HasPrecision(18, 2);
            e.HasIndex(a => new { a.BankConnectionId, a.ExternalId }).IsUnique();
            e.HasOne(a => a.Connection).WithMany(c => c.Accounts).HasForeignKey(a => a.BankConnectionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Company>().WithMany().HasForeignKey(a => a.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<BankTransaction>(e =>
        {
            // Binary collation: bank references are compared byte for byte, "abc" and "ABC" are two movements.
            e.Property(t => t.BankReference).HasMaxLength(BankTransaction.BankReferenceMaxLength).UseCollation("Latin1_General_100_BIN2");
            e.Property(t => t.Description).HasMaxLength(BankTransaction.DescriptionMaxLength);
            e.Property(t => t.CounterpartyName).HasMaxLength(BankTransaction.CounterpartyNameMaxLength);
            e.Property(t => t.CounterpartyIban).HasMaxLength(34);
            e.Property(t => t.CounterpartyTaxNumber).HasMaxLength(11);
            e.Property(t => t.Amount).HasPrecision(18, 2);
            e.Property(t => t.RowVersion).IsRowVersion();
            // The guarantee behind "each movement once", whatever the application does.
            e.HasIndex(t => new { t.BankAccountId, t.BankReference }).IsUnique();
            // The movements list: one company, newest first, keyset on (BookedAt, Id).
            e.HasIndex(t => new { t.CompanyId, t.BookedAt, t.Id });
            e.HasOne(t => t.Account).WithMany().HasForeignKey(t => t.BankAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Company>().WithMany().HasForeignKey(t => t.CompanyId).OnDelete(DeleteBehavior.Restrict);
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
