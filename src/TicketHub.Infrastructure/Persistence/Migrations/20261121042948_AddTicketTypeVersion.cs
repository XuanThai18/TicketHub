using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketTypeVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // xmin là cột hệ thống có sẵn của Postgres: Npgsql bỏ qua lệnh AddColumn này khi sinh SQL, không có cột mới nào được tạo.
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "ticket_types",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                table: "ticket_types");
        }
    }
}
