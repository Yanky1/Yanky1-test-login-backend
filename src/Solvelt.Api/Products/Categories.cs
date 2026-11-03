using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvelt.Api.Data;
using Solvelt.Api.Domain;
using Solvelt.Api.Teams;

namespace Solvelt.Api.Products;

public static class CategorySeed
{
    private record A(string Key, string Label, string Type = "text", string[]? Options = null, bool Required = false, string? Group = null, string? Help = null, string? Unit = null);
    private record C(string Name, string[]? Kids = null, A[]? Attrs = null);

    private static readonly string[] Allergens = ["Milk", "Eggs", "Fish", "Shellfish", "Tree nuts", "Peanuts", "Wheat", "Soy", "Sesame", "None"];
    private static readonly string[] FoodCerts = ["Organic", "Kosher", "Halal", "Vegan", "Gluten-free", "Non-GMO", "Fair trade"];

    private static readonly C[] Tree =
    [
        new("Electronics", ["Phones & Accessories", "Computers & Tablets", "Audio & Headphones", "TV & Home Theater", "Cameras & Photo", "Smart Home", "Wearable Tech"],
            [new("model", "Model name / number", Required: true, Group: "Basics", Help: "As printed on the product or box."),
             new("warranty_months", "Manufacturer warranty", "number", Group: "Basics", Unit: "months"),
             new("power", "Power / battery", Group: "Technical", Help: "e.g. 5000 mAh, 65 W adapter, 120 V"),
             new("connectivity", "Connectivity", "multiselect", ["Bluetooth", "Wi-Fi", "USB-C", "USB-A", "HDMI", "NFC", "Ethernet", "None"], Group: "Technical"),
             new("color", "Colour", Group: "Basics")]),
        new("Home & Kitchen", ["Kitchen & Dining", "Furniture", "Bedding & Bath", "Home Décor", "Storage & Organization", "Appliances", "Lighting"],
            [new("material", "Material", Group: "Basics"), new("color", "Colour", Group: "Basics"),
             new("dimensions_note", "Size / dimensions", Group: "Basics", Help: "e.g. 30 × 20 × 10 cm"),
             new("care", "Care instructions", "longtext", Group: "Basics")]),
        new("Clothing, Shoes & Accessories", ["Women", "Men", "Kids & Baby Clothing", "Shoes", "Bags & Luggage", "Jewelry & Watches"],
            [new("material", "Material / fabric", Required: true, Group: "Basics", Help: "e.g. 100% cotton"),
             new("fit", "Fit", "select", ["Regular", "Slim", "Relaxed", "Oversized", "Petite", "Plus"], Group: "Basics"),
             new("size_chart", "Size chart / sizing notes", "longtext", Group: "Sizing", Help: "Measurements per size help reduce returns."),
             new("care", "Care instructions", "longtext", Group: "Basics"),
             new("gender", "Department", "select", ["Women", "Men", "Girls", "Boys", "Unisex"], Group: "Basics")]),
        new("Beauty & Personal Care", ["Skin Care", "Hair Care", "Makeup", "Fragrance", "Personal Care"],
            [new("ingredients", "Ingredients", "longtext", Required: true, Group: "Basics"), new("skin_type", "Skin / hair type", Group: "Basics"),
             new("volume", "Volume / weight", Group: "Basics"), new("expiry_months", "Shelf life after opening", "number", Unit: "months")]),
        new("Toys & Games", ["Building Toys", "Games & Puzzles", "Dolls & Plush", "Outdoor Play", "Learning & Education"],
            [new("age_range", "Recommended age", Required: true, Group: "Safety", Help: "e.g. 3-6 years"),
             new("pieces", "Number of pieces", "number"), new("battery_required", "Batteries required", "bool", Group: "Safety"),
             new("choking_hazard", "Contains small parts", "bool", Group: "Safety")]),
        new("Books", ["Fiction", "Non-fiction", "Children's Books", "Textbooks", "Comics & Graphic Novels"],
            [new("author", "Author", Required: true, Group: "Basics"), new("publisher", "Publisher", Group: "Basics"),
             new("pages", "Number of pages", "number", Group: "Basics"), new("language", "Language", Group: "Basics"),
             new("format", "Format", "select", ["Hardcover", "Paperback", "Board book", "Spiral-bound"], Group: "Basics"),
             new("publication_year", "Publication year", "number", Group: "Basics")]),
        new("Grocery & Food", ["Snacks & Sweets", "Beverages", "Bakery", "Dairy & Eggs", "Meat & Poultry", "Fresh Produce", "Pantry Staples", "Frozen Food"],
            [new("ingredients", "Ingredients", "longtext", Required: true, Group: "Food information"),
             new("allergens", "Allergens", "multiselect", Allergens, Required: true, Group: "Food information", Help: "Select every allergen present or may be present."),
             new("nutrition", "Nutrition facts (per serving)", "longtext", Group: "Food information"),
             new("certifications", "Certifications", "multiselect", FoodCerts, Group: "Food information", Help: "Organic, kosher, halal ... Only claim what you can document."),
             new("net_weight", "Net weight / volume", Required: true, Group: "Food information"),
             new("shelf_life_days", "Shelf life", "number", Group: "Storage", Unit: "days"),
             new("storage", "Storage", "select", ["Room temperature", "Refrigerated", "Frozen"], Group: "Storage")]),
        new("Sports & Outdoors", ["Fitness & Exercise", "Camping & Hiking", "Cycling", "Team Sports", "Water Sports"],
            [new("material", "Material", Group: "Basics"), new("color", "Colour", Group: "Basics"), new("sport", "Sport / activity", Group: "Basics")]),
        new("Health & Household", ["Vitamins & Supplements", "Cleaning Supplies", "Medical Supplies", "Paper & Plastic"],
            [new("ingredients", "Ingredients / active ingredients", "longtext", Group: "Basics"), new("count", "Count / size", Group: "Basics")]),
        new("Pet Supplies", ["Dogs", "Cats", "Fish & Aquatic", "Small Animals", "Birds"], [new("pet_type", "Pet type", Group: "Basics"), new("material", "Material", Group: "Basics")]),
        new("Automotive", ["Parts & Accessories", "Car Care", "Tools & Equipment"], [new("fitment", "Compatible makes / models", "longtext", Group: "Basics"), new("part_number", "Part number", Group: "Basics")]),
        new("Office & School Supplies", ["Writing Supplies", "Paper & Notebooks", "Desk Accessories", "Art Supplies"], [new("color", "Colour", Group: "Basics"), new("count", "Count / size", Group: "Basics")]),
        new("Garden & Tools", ["Power Tools", "Hand Tools", "Garden & Outdoor Living", "Plants & Seeds"], [new("material", "Material", Group: "Basics"), new("power", "Power source", Group: "Basics")]),
        new("Baby", ["Feeding", "Diapering", "Strollers & Car Seats", "Nursery"], [new("age_range", "Recommended age", Group: "Safety"), new("material", "Material", Group: "Basics")]),
        new("Arts, Crafts & Sewing", ["Crafting", "Sewing", "Painting & Drawing"], [new("material", "Material", Group: "Basics"), new("color", "Colour", Group: "Basics")]),
    ];

    public static string Slugify(string s)
    {
        var chars = s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Idempotent: adds missing categories/attributes by slug/key; never deletes or edits what an operator changed.</summary>
    public static void Run(AppDbContext db)
    {
        if (db.Categories.Any()) return;
        void AddAttrs(Guid catId, A[]? attrs)
        {
            var i = 0;
            foreach (var a in attrs ?? [])
                db.CategoryAttributes.Add(new CategoryAttribute
                {
                    CategoryId = catId, Key = a.Key, Label = a.Label, Type = a.Type, OptionsJson = JsonSerializer.Serialize(a.Options ?? []), Required = a.Required,
                    Group = a.Group, Help = a.Help, Unit = a.Unit, SortOrder = i++
                });
        }
        var order = 0;
        foreach (var root in Tree)
        {
            var r = new Category { Name = root.Name, Slug = Slugify(root.Name), SortOrder = order++ };
            db.Categories.Add(r);
            AddAttrs(r.Id, root.Attrs);
            var k = 0;
            foreach (var kid in root.Kids ?? [])
                db.Categories.Add(new Category { ParentId = r.Id, Name = kid, Slug = Slugify(kid), SortOrder = k++ });
        }
        db.SaveChanges();
    }
}

/// <summary>Resolves a category's path and its (inherited) attribute configuration.</summary>
public class CategoryService(AppDbContext db)
{
    public record Node(Guid Id, Guid? ParentId, string Name, string Slug, string Path, bool Leaf);

    public async Task<List<Node>> TreeAsync()
    {
        var cats = await db.Categories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();
        var byId = cats.ToDictionary(c => c.Id);
        var parents = cats.Where(c => c.ParentId != null).Select(c => c.ParentId!.Value).ToHashSet();
        string PathOf(Category c) => c.ParentId is { } p && byId.TryGetValue(p, out var pc) ? PathOf(pc) + " › " + c.Name : c.Name;
        var res = new List<Node>();
        void Walk(Guid? parent)
        {
            foreach (var c in cats.Where(x => x.ParentId == parent)) { res.Add(new(c.Id, c.ParentId, c.Name, c.Slug, PathOf(c), !parents.Contains(c.Id))); Walk(c.Id); }
        }
        Walk(null);
        return res;
    }

    /// <summary>Attributes of the category and all its ancestors (a child key overrides the parent's).</summary>
    public async Task<List<CategoryAttribute>> AttributesAsync(Guid? categoryId)
    {
        if (categoryId is null) return [];
        var chain = new List<Guid>();
        var cur = categoryId;
        while (cur is { } id && chain.Count < 8 && !chain.Contains(id))
        {
            chain.Add(id);
            cur = await db.Categories.AsNoTracking().Where(c => c.Id == id).Select(c => c.ParentId).FirstOrDefaultAsync();
        }
        var all = await db.CategoryAttributes.AsNoTracking().Where(a => chain.Contains(a.CategoryId)).ToListAsync();
        return all.GroupBy(a => a.Key).Select(g => g.OrderBy(a => chain.IndexOf(a.CategoryId)).First())
            .OrderBy(a => chain.IndexOf(a.CategoryId) == chain.Count - 1 ? 0 : 1).ThenBy(a => a.SortOrder).ToList();
    }

    public static object AttrDto(CategoryAttribute a) => new
    {
        key = a.Key, label = a.Label, type = a.Type, options = JsonSerializer.Deserialize<string[]>(a.OptionsJson) ?? [], required = a.Required,
        group = a.Group, help = a.Help, unit = a.Unit
    };
}

public static class CategoryEndpoints
{
    public static void MapCategoryEndpoints(this RouteGroupBuilder g)
    {
        var c = g.MapGroup("/categories").RequireAuthorization("Seller").RequireAuthorization("VerifiedEmail").RequirePermission(Permission.BrandWorkRead);
        c.MapGet("", async (CategoryService svc) => Results.Ok((await svc.TreeAsync()).Select(n => new { id = n.Id, parentId = n.ParentId, name = n.Name, slug = n.Slug, path = n.Path, leaf = n.Leaf })));
        c.MapGet("/{id:guid}/attributes", async (Guid id, AppDbContext db, CategoryService svc) =>
            await db.Categories.AnyAsync(x => x.Id == id) ? Results.Ok((await svc.AttributesAsync(id)).Select(CategoryService.AttrDto)) : Results.NotFound());
    }
}
