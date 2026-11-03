using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Products;

namespace Solvelt.Api.Bookings;

/// <summary>Adds the "Local Services" department to existing databases too (the product seeder only runs on an empty table). Idempotent, never edits operator changes.</summary>
public static class ServiceCategories
{
    private static readonly string[] Kids = ["Home Services", "Repairs & Maintenance", "Lessons & Classes", "Consulting & Coaching", "Health & Wellness", "Events & Photography"];

    public static void Ensure(AppDbContext db)
    {
        const string rootSlug = "local-services";
        var root = db.Categories.FirstOrDefault(c => c.Slug == rootSlug);
        if (root is null)
        {
            root = new Category { Name = "Local Services", Slug = rootSlug, SortOrder = 100 };
            db.Categories.Add(root);
        }
        var k = 0;
        foreach (var kid in Kids)
        {
            var slug = CategorySeed.Slugify(kid);
            if (!db.Categories.Any(c => c.Slug == slug)) db.Categories.Add(new Category { ParentId = root.Id, Name = kid, Slug = slug, SortOrder = k });
            k++;
        }
        db.SaveChanges();
    }
}
