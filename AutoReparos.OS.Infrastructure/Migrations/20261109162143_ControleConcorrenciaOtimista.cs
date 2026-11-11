using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoReparos.OS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ControleConcorrenciaOtimista : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // xmin e uma coluna de sistema do PostgreSQL: o token de concorrencia nao exige DDL.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
