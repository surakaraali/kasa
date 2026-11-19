using Kasa.Api.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Kasa.Api.Data;

/// <summary>Lets `dotnet ef` build the model without starting the app. The connection is never opened for adding a migration.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlServer("Server=localhost,14335;Database=Kasa").Options,
        new TenantContext(new HttpContextAccessor()));
}
