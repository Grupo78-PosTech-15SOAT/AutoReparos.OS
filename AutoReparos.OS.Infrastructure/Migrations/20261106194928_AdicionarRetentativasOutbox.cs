using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoReparos.Os.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AdicionarRetentativasOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EncaminhadoParaDlq",
                table: "mensagens_pendentes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProximaTentativaEm",
                table: "mensagens_pendentes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Tentativas",
                table: "mensagens_pendentes",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EncaminhadoParaDlq",
                table: "mensagens_pendentes");

            migrationBuilder.DropColumn(
                name: "ProximaTentativaEm",
                table: "mensagens_pendentes");

            migrationBuilder.DropColumn(
                name: "Tentativas",
                table: "mensagens_pendentes");
        }
    }
}
