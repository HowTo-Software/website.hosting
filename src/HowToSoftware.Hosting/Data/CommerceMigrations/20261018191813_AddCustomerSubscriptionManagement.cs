using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HowToSoftware.Hosting.Data.CommerceMigrations
{
    /// <inheritdoc />
    public partial class AddCustomerSubscriptionManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_access",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    normalized_email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    challenge_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    session_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                    expires_at = table.Column<long>(type: "bigint", nullable: false),
                    used_at = table.Column<long>(type: "bigint", nullable: true),
                    session_expires_at = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_access", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "customer_support",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    normalized_email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    subject = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    message = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    locale = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                    notified_at = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_support", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "subscription_plan_changes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    hosting_service_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    target_plan_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    stripe_subscription_id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    stripe_customer_id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    subscription_item_id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    source_price_id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    target_price_id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    baseline_invoice_id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "Latin1_General_100_BIN2"),
                    stripe_invoice_id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "Latin1_General_100_BIN2"),
                    stripe_schedule_id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "Latin1_General_100_BIN2"),
                    currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    billing_period = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    due_now_minor = table.Column<long>(type: "bigint", nullable: false),
                    next_invoice_minor = table.Column<long>(type: "bigint", nullable: false),
                    memory_mb = table.Column<int>(type: "int", nullable: false),
                    cpu_percent = table.Column<int>(type: "int", nullable: false),
                    disk_mb = table.Column<int>(type: "int", nullable: false),
                    backup_limit = table.Column<int>(type: "int", nullable: false),
                    database_limit = table.Column<int>(type: "int", nullable: false),
                    allocation_limit = table.Column<int>(type: "int", nullable: false),
                    direction = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    state = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    active = table.Column<bool>(type: "bit", nullable: false),
                    cancellation_requested = table.Column<bool>(type: "bit", nullable: false),
                    proration_at = table.Column<long>(type: "bigint", nullable: false),
                    effective_at = table.Column<long>(type: "bigint", nullable: false),
                    quote_expires_at = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<long>(type: "bigint", nullable: false),
                    lease_token = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    lease_expires_at = table.Column<long>(type: "bigint", nullable: true),
                    failure_code = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subscription_plan_changes", x => x.id);
                    table.ForeignKey(
                        name: "FK_subscription_plan_changes_hosting_plans_target_plan_id",
                        column: x => x.target_plan_id,
                        principalTable: "hosting_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_subscription_plan_changes_hosting_services_hosting_service_id",
                        column: x => x.hosting_service_id,
                        principalTable: "hosting_services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_access_challenge_hash",
                table: "customer_access",
                column: "challenge_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_access_normalized_email",
                table: "customer_access",
                column: "normalized_email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_access_normalized_email_created_at",
                table: "customer_access",
                columns: new[] { "normalized_email", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_access_session_hash",
                table: "customer_access",
                column: "session_hash",
                unique: true,
                filter: "[session_hash] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_customer_support_normalized_email_created_at",
                table: "customer_support",
                columns: new[] { "normalized_email", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_support_notified_at",
                table: "customer_support",
                column: "notified_at");

            migrationBuilder.CreateIndex(
                name: "IX_subscription_plan_changes_hosting_service_id",
                table: "subscription_plan_changes",
                column: "hosting_service_id",
                unique: true,
                filter: "[active] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_subscription_plan_changes_state_active",
                table: "subscription_plan_changes",
                columns: new[] { "state", "active" });

            migrationBuilder.CreateIndex(
                name: "IX_subscription_plan_changes_stripe_subscription_id",
                table: "subscription_plan_changes",
                column: "stripe_subscription_id");

            migrationBuilder.CreateIndex(
                name: "IX_subscription_plan_changes_target_plan_id",
                table: "subscription_plan_changes",
                column: "target_plan_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_access");

            migrationBuilder.DropTable(
                name: "customer_support");

            migrationBuilder.DropTable(
                name: "subscription_plan_changes");
        }
    }
}
