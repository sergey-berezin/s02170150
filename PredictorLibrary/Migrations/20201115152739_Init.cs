using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PredictorLibrary.Migrations
{
    public partial class Init : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Images",
                columns: table => new
                {
                    ImageDataId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Images", x => x.ImageDataId);
                });

            migrationBuilder.CreateTable(
                name: "SavedResults",
                columns: table => new
                {
                    ItemId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Class = table.Column<string>(type: "TEXT", nullable: true),
                    Confidence = table.Column<float>(type: "REAL", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: true),
                    BlobImageDataId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedResults", x => x.ItemId);
                    table.ForeignKey(
                        name: "FK_SavedResults_Images_BlobImageDataId",
                        column: x => x.BlobImageDataId,
                        principalTable: "Images",
                        principalColumn: "ImageDataId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SavedResults_BlobImageDataId",
                table: "SavedResults",
                column: "BlobImageDataId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SavedResults");

            migrationBuilder.DropTable(
                name: "Images");
        }
    }
}
