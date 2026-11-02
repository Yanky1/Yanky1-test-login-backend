using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Api.Domain;

namespace Solvelt.Api.Data;

/// <summary>Used by `dotnet ef` only. Migrations target PostgreSQL; SQLite (dev/tests) uses EnsureCreated.</summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // Identity's model depends on IdentityOptions.Stores.SchemaVersion (v3 adds the passkeys table).
        var sp = new ServiceCollection()
            .AddLogging()
            .AddIdentityCore<AppUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AppDbContext>().Services
            .BuildServiceProvider();
        var o = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=solvelt;Username=solvelt;Password=design-time-only")
            .UseApplicationServiceProvider(sp).Options;
        return new AppDbContext(o);
    }
}
