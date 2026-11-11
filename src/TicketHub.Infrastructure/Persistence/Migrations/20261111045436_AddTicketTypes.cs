using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ticket_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    total_quantity = table.Column<int>(type: "integer", nullable: false),
                    available_quantity = table.Column<int>(type: "integer", nullable: false),
                    max_quantity_per_order = table.Column<int>(type: "integer", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<string>(type: "text", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_types", x => x.id);
                    table.CheckConstraint("ck_ticket_types_max_quantity_per_order", "max_quantity_per_order > 0 AND max_quantity_per_order <= total_quantity");
                    table.CheckConstraint("ck_ticket_types_price", "price >= 0");
                    table.CheckConstraint("ck_ticket_types_total_quantity", "total_quantity > 0 AND available_quantity >= 0 AND available_quantity <= total_quantity");
                    table.ForeignKey(
                        name: "fk_ticket_types_events_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_types_event_id_name",
                table: "ticket_types",
                columns: new[] { "event_id", "name" },
                unique: true,
                filter: "is_deleted = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_types");
        }
    }
}
