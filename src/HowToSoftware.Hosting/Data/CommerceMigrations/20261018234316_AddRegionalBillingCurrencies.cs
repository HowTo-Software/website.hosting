using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HowToSoftware.Hosting.Data.CommerceMigrations
{
    /// <inheritdoc />
    public partial class AddRegionalBillingCurrencies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_plan_billing_prices_plan_id_billing_period",
                table: "plan_billing_prices");

            migrationBuilder.CreateIndex(
                name: "IX_plan_billing_prices_plan_id_billing_period_currency",
                table: "plan_billing_prices",
                columns: new[] { "plan_id", "billing_period", "currency" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_plan_billing_prices_plan_id_billing_period_currency",
                table: "plan_billing_prices");

            migrationBuilder.CreateIndex(
                name: "IX_plan_billing_prices_plan_id_billing_period",
                table: "plan_billing_prices",
                columns: new[] { "plan_id", "billing_period" },
                unique: true);
        }
    }
}
