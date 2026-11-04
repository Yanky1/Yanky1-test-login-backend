using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;

namespace Solvelt.Api.PostgresTests;

/// <summary>Runs only with SOLVELT_PG_ADMIN=&lt;Npgsql connection string of a superuser, e.g. "Host=localhost;Username=postgres;Password=...;Database=postgres"&gt;. Skipped in THIS project only.</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public static string? Admin => Environment.GetEnvironmentVariable("SOLVELT_PG_ADMIN") is { Length: > 0 } c ? c : null;
    public PgFactAttribute() { if (Admin is null) Skip = "no SOLVELT_PG_ADMIN: PostgreSQL migration tests are opt-in (docs/SHIPPING.md)"; }
}

[Trait("Category", "PostgresMigrations")]
public class PostgresMigrationTests
{
    private const string BeforeUnique = "20261104012837_Shipping";                  // the schema before the live-tracking index
    private const string BeforePerCarrier = "20261104080144_MarketplaceContract";   // the schema right before ShippingLiveTrackingPerCarrier

    private static AppDbContext Context(string connectionString)
    {
        var sp = new ServiceCollection().AddLogging()
            .AddIdentityCore<AppUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AppDbContext>().Services.BuildServiceProvider();
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).UseApplicationServiceProvider(sp).Options);
    }

    /// <summary>Inserts one live carrier shipment with every NOT NULL column filled (foreign keys are not enforced for the seed: the order rows do not matter here).</summary>
    private static async Task SeedShipment(NpgsqlConnection c, string carrier, string number, DateTime created)
    {
        var cols = new List<(string Name, string Type, bool HasDefault)>();
        await using (var q = new NpgsqlCommand("SELECT column_name, data_type, column_default IS NOT NULL FROM information_schema.columns WHERE table_name = 'order_shipments' AND is_nullable = 'NO'", c))
        await using (var r = await q.ExecuteReaderAsync()) while (await r.ReadAsync()) cols.Add((r.GetString(0), r.GetString(1), r.GetBoolean(2)));
        var values = new List<string>(); var names = new List<string>();
        foreach (var (name, type, hasDefault) in cols)
        {
            if (hasDefault) continue;
            names.Add($"\"{name}\"");
            values.Add(name switch
            {
                "Id" => "gen_random_uuid()", "Carrier" => $"'{carrier}'", "TrackingNumber" => $"'{number}'", "Method" => "'carrier'", "CreatedAt" => $"'{created:O}'",
                _ => type switch { "uuid" => "gen_random_uuid()", "text" or "character varying" => "''", "integer" or "bigint" or "smallint" => "0", "boolean" => "false", "jsonb" or "json" => "'{}'", var t when t.StartsWith("timestamp") => $"'{created:O}'", _ => "NULL" }
            });
        }
        await using var cmd = new NpgsqlCommand($"SET session_replication_role = replica; INSERT INTO order_shipments ({string.Join(",", names)}) VALUES ({string.Join(",", values)}); SET session_replication_role = DEFAULT;", c);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<(string Carrier, string Number, bool Stopped, DateTime Created)>> Rows(NpgsqlConnection c)
    {
        var rows = new List<(string, string, bool, DateTime)>();
        await using var q = new NpgsqlCommand("SELECT \"Carrier\", \"TrackingNumber\", \"TrackingStoppedAt\" IS NOT NULL, \"CreatedAt\" FROM order_shipments ORDER BY \"CreatedAt\"", c);
        await using var r = await q.ExecuteReaderAsync(); while (await r.ReadAsync()) rows.Add((r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetDateTime(3)));
        return rows;
    }

    [PgFact]
    public async Task Migrating_up_over_live_duplicates_and_back_down_over_a_shared_string_both_succeed()
    {
        var name = "solvelt_mig_" + Guid.NewGuid().ToString("N")[..10];
        await using var admin = new NpgsqlConnection(PgFactAttribute.Admin); await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            var cs = new NpgsqlConnectionStringBuilder(PgFactAttribute.Admin) { Database = name }.ConnectionString;
            await using var db = Context(cs); var migrator = db.GetService<IMigrator>();
            migrator.Migrate(BeforeUnique);
            await using var c = new NpgsqlConnection(cs); await c.OpenAsync();
            var t0 = DateTime.UtcNow.AddHours(-5);
            await SeedShipment(c, "USPS", "9400111899223000000001", t0); await SeedShipment(c, "USPS", "9400111899223000000001", t0.AddHours(1));   // two LIVE parcels, one number: the pre-index world
            await SeedShipment(c, "USPS", "9400111899223000000002", t0);                                                                           // and an unrelated one

            migrator.Migrate();                                                                                                                     // up to the latest: must not fail with 23505
            var up = await Rows(c);
            var dups = up.Where(x => x.Number.EndsWith('1')).OrderBy(x => x.Created).ToList();
            Assert.True(dups[0].Stopped); Assert.False(dups[1].Stopped); Assert.False(up.Single(x => x.Number.EndsWith('2')).Stopped);                                                 // the older duplicate stopped, the newest and the unrelated one stay live

            await SeedShipment(c, "FedEx", "94001118992230000099", t0.AddHours(2)); await SeedShipment(c, "USPS", "94001118992230000099", t0.AddHours(3));   // legal under the per-carrier index
            migrator.Migrate(BeforePerCarrier);                                                                                                     // back down: recreating the number-only index must not fail with 23505
            var down = await Rows(c);
            Assert.True(down.Single(x => x.Carrier == "FedEx").Stopped); Assert.False(down.Single(x => x.Number == "94001118992230000099" && x.Carrier == "USPS").Stopped);

            migrator.Migrate();                                                                                                                     // and up again
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", admin); await drop.ExecuteNonQueryAsync();
        }
    }
}
