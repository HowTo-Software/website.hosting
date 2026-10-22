using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HowToSoftware.Hosting.Data.CommerceMigrations
{
    /// <inheritdoc />
    public partial class AddExternalCustomerIdentities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_external_identities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    customer_access_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    issuer = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    subject = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    display_name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    linked_at = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_external_identities", x => x.id);
                    table.ForeignKey(
                        name: "FK_customer_external_identities_customer_access_customer_access_id",
                        column: x => x.customer_access_id,
                        principalTable: "customer_access",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "external_identity_flows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    purpose = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    customer_access_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    owner_session_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    browser_secret_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                    expires_at = table.Column<long>(type: "bigint", nullable: false),
                    verified_at = table.Column<long>(type: "bigint", nullable: true),
                    consumed_at = table.Column<long>(type: "bigint", nullable: true),
                    issuer = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                    subject = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "Latin1_General_100_BIN2"),
                    display_name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_identity_flows", x => x.id);
                    table.ForeignKey(
                        name: "FK_external_identity_flows_customer_access_customer_access_id",
                        column: x => x.customer_access_id,
                        principalTable: "customer_access",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_external_identities_customer_access_id_provider",
                table: "customer_external_identities",
                columns: new[] { "customer_access_id", "provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_external_identities_provider_issuer_subject",
                table: "customer_external_identities",
                columns: new[] { "provider", "issuer", "subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_external_identity_flows_customer_access_id",
                table: "external_identity_flows",
                column: "customer_access_id");

            migrationBuilder.CreateIndex(
                name: "IX_external_identity_flows_expires_at",
                table: "external_identity_flows",
                column: "expires_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_external_identities");

            migrationBuilder.DropTable(
                name: "external_identity_flows");
        }
    }
}
