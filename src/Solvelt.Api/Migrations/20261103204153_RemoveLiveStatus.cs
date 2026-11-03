using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLiveStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "Live" was removed as a seller status: Approved is the final selling state. Data only, the schema is unchanged.
            // (Audit rows keep their original text: the audit log is append-only history.)
            migrationBuilder.Sql(Solvelt.Api.Domain.LegacyStatus.ConvertLiveSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // not reversible: which accounts used to be "Live" is not kept
        }
    }
}
