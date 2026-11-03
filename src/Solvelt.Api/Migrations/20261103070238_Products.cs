using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    public partial class Products : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_categories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "category_attributes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OptionsJson = table.Column<string>(type: "text", nullable: false),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    Group = table.Column<string>(type: "text", nullable: true),
                    Help = table.Column<string>(type: "text", nullable: true),
                    Unit = table.Column<string>(type: "text", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_category_attributes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_category_attributes_categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BrandId = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Handle = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Condition = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ConditionNote = table.Column<string>(type: "text", nullable: true),
                    Sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PriceCents = table.Column<long>(type: "bigint", nullable: true),
                    CompareAtCents = table.Column<long>(type: "bigint", nullable: true),
                    SaleCents = table.Column<long>(type: "bigint", nullable: true),
                    SaleStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SaleEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CostCents = table.Column<long>(type: "bigint", nullable: true),
                    Stock = table.Column<int>(type: "integer", nullable: false),
                    Reserved = table.Column<int>(type: "integer", nullable: false),
                    LowStockAlert = table.Column<int>(type: "integer", nullable: true),
                    AllowBackorder = table.Column<bool>(type: "boolean", nullable: false),
                    MaxPerOrder = table.Column<int>(type: "integer", nullable: true),
                    Barcode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    NoBarcode = table.Column<bool>(type: "boolean", nullable: false),
                    BulletsJson = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    WeightGrams = table.Column<int>(type: "integer", nullable: true),
                    LengthMm = table.Column<int>(type: "integer", nullable: true),
                    WidthMm = table.Column<int>(type: "integer", nullable: true),
                    HeightMm = table.Column<int>(type: "integer", nullable: true),
                    HandlingDays = table.Column<int>(type: "integer", nullable: true),
                    ShippingJson = table.Column<string>(type: "text", nullable: false),
                    LocalPickup = table.Column<bool>(type: "boolean", nullable: false),
                    ReturnsDays = table.Column<int>(type: "integer", nullable: true),
                    ReturnsCondition = table.Column<string>(type: "text", nullable: true),
                    WarrantyMonths = table.Column<int>(type: "integer", nullable: true),
                    WarrantyNote = table.Column<string>(type: "text", nullable: true),
                    SearchTerms = table.Column<string>(type: "text", nullable: true),
                    MetaTitle = table.Column<string>(type: "text", nullable: true),
                    MetaDescription = table.Column<string>(type: "text", nullable: true),
                    HasVariants = table.Column<bool>(type: "boolean", nullable: false),
                    QualityScore = table.Column<int>(type: "integer", nullable: false),
                    IsReady = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_products", x => x.Id);
                    table.ForeignKey(
                        name: "FK_products_brands_BrandId",
                        column: x => x.BrandId,
                        principalTable: "brands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_products_categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_products_seller_accounts_SellerId",
                        column: x => x.SellerId,
                        principalTable: "seller_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_attribute_values",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ValueJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_attribute_values", x => x.Id);
                    table.ForeignKey(
                        name: "FK_product_attribute_values_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_faqs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Question = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Answer = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_faqs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_product_faqs_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_images",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    VariantId = table.Column<Guid>(type: "uuid", nullable: true),
                    StorageKey = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    Alt = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsMain = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_images", x => x.Id);
                    table.ForeignKey(
                        name: "FK_product_images_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_options",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ValuesJson = table.Column<string>(type: "text", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_options", x => x.Id);
                    table.ForeignKey(
                        name: "FK_product_options_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_questions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AskerName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Answer = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AskedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AnsweredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AnsweredBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_questions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_product_questions_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_variants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ValuesJson = table.Column<string>(type: "text", nullable: false),
                    ComboKey = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PriceCents = table.Column<long>(type: "bigint", nullable: true),
                    Stock = table.Column<int>(type: "integer", nullable: false),
                    Reserved = table.Column<int>(type: "integer", nullable: false),
                    Barcode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_variants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_product_variants_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_categories_ParentId",
                table: "categories",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_categories_Slug",
                table: "categories",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_category_attributes_CategoryId_Key",
                table: "category_attributes",
                columns: new[] { "CategoryId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_product_attribute_values_ProductId_Key",
                table: "product_attribute_values",
                columns: new[] { "ProductId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_product_faqs_ProductId",
                table: "product_faqs",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_product_images_ProductId_VariantId",
                table: "product_images",
                columns: new[] { "ProductId", "VariantId" });

            migrationBuilder.CreateIndex(
                name: "IX_product_options_ProductId",
                table: "product_options",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_product_questions_ProductId",
                table: "product_questions",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_product_questions_SellerId_State",
                table: "product_questions",
                columns: new[] { "SellerId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_product_variants_ProductId_ComboKey",
                table: "product_variants",
                columns: new[] { "ProductId", "ComboKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_product_variants_SellerId_Sku",
                table: "product_variants",
                columns: new[] { "SellerId", "Sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_products_BrandId",
                table: "products",
                column: "BrandId");

            migrationBuilder.CreateIndex(
                name: "IX_products_CategoryId",
                table: "products",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_products_SellerId_BrandId",
                table: "products",
                columns: new[] { "SellerId", "BrandId" });

            migrationBuilder.CreateIndex(
                name: "IX_products_SellerId_Handle",
                table: "products",
                columns: new[] { "SellerId", "Handle" });

            migrationBuilder.CreateIndex(
                name: "IX_products_SellerId_Sku",
                table: "products",
                columns: new[] { "SellerId", "Sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_products_SellerId_Status",
                table: "products",
                columns: new[] { "SellerId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "category_attributes");

            migrationBuilder.DropTable(
                name: "product_attribute_values");

            migrationBuilder.DropTable(
                name: "product_faqs");

            migrationBuilder.DropTable(
                name: "product_images");

            migrationBuilder.DropTable(
                name: "product_options");

            migrationBuilder.DropTable(
                name: "product_questions");

            migrationBuilder.DropTable(
                name: "product_variants");

            migrationBuilder.DropTable(
                name: "products");

            migrationBuilder.DropTable(
                name: "categories");
        }
    }
}
