using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledgerline.Accounts.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProtectEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE UPDATE, DELETE, TRUNCATE ON events FROM CURRENT_USER;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("GRANT UPDATE, DELETE, TRUNCATE ON events TO CURRENT_USER;");
        }
    }
}
