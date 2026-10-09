using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class Enrollments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_passes_user_id",
                table: "passes");

            migrationBuilder.DropIndex(
                name: "IX_enrollments_class_id",
                table: "enrollments");

            migrationBuilder.DropIndex(
                name: "IX_classes_room_id",
                table: "classes");

            migrationBuilder.AddColumn<int>(
                name: "refunded_visits",
                table: "enrollments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                """
                UPDATE enrollments e
                SET refunded_visits = e.seats
                FROM classes c
                WHERE c.id = e.class_id
                    AND e.status = 'cancelled'
                    AND e.cancelled_at IS NOT NULL
                    AND (c.is_cancelled OR e.cancelled_at <= c.starts_at - interval '2 hours')
                """);

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false),
                    key_hash = table.Column<string>(type: "text", nullable: false),
                    request_hash = table.Column<string>(type: "text", nullable: false),
                    response_body = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_records", x => x.id);
                    table.ForeignKey(
                        name: "FK_idempotency_records_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "waitlist_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    class_id = table.Column<long>(type: "bigint", nullable: false),
                    seats = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    cancelled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    promoted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    enrollment_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_waitlist_entries", x => x.id);
                    table.CheckConstraint("ck_waitlist_entries_seats", "seats > 0");
                    table.ForeignKey(
                        name: "FK_waitlist_entries_classes_class_id",
                        column: x => x.class_id,
                        principalTable: "classes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_waitlist_entries_enrollments_enrollment_id",
                        column: x => x.enrollment_id,
                        principalTable: "enrollments",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_waitlist_entries_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_passes_user_id",
                table: "passes",
                column: "user_id",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_passes_remaining_visits",
                table: "passes",
                sql: "remaining_visits >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_enrollments_active_class_user",
                table: "enrollments",
                columns: new[] { "class_id", "user_id" },
                filter: "status = 'active'")
                .Annotation("Npgsql:IndexInclude", new[] { "seats" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_enrollments_refunded_visits",
                table: "enrollments",
                sql: "refunded_visits >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_enrollments_seats",
                table: "enrollments",
                sql: "seats > 0");

            migrationBuilder.CreateIndex(
                name: "ix_classes_scheduled_room_starts_at",
                table: "classes",
                columns: new[] { "room_id", "starts_at", "id" },
                filter: "is_cancelled = false");

            migrationBuilder.CreateIndex(
                name: "ix_classes_scheduled_starts_at",
                table: "classes",
                columns: new[] { "starts_at", "id" },
                filter: "is_cancelled = false");

            migrationBuilder.CreateIndex(
                name: "ux_idempotency_records_user_scope_key",
                table: "idempotency_records",
                columns: new[] { "user_id", "scope", "key_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_waitlist_entries_enrollment_id",
                table: "waitlist_entries",
                column: "enrollment_id");

            migrationBuilder.CreateIndex(
                name: "ix_waitlist_entries_waiting_queue",
                table: "waitlist_entries",
                columns: new[] { "class_id", "created_at", "id" },
                filter: "status = 'waiting'");

            migrationBuilder.CreateIndex(
                name: "ux_waitlist_entries_waiting_user_class",
                table: "waitlist_entries",
                columns: new[] { "user_id", "class_id" },
                unique: true,
                filter: "status = 'waiting'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_records");

            migrationBuilder.DropTable(
                name: "waitlist_entries");

            migrationBuilder.DropIndex(
                name: "IX_passes_user_id",
                table: "passes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_passes_remaining_visits",
                table: "passes");

            migrationBuilder.DropIndex(
                name: "ix_enrollments_active_class_user",
                table: "enrollments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_enrollments_refunded_visits",
                table: "enrollments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_enrollments_seats",
                table: "enrollments");

            migrationBuilder.DropIndex(
                name: "ix_classes_scheduled_room_starts_at",
                table: "classes");

            migrationBuilder.DropIndex(
                name: "ix_classes_scheduled_starts_at",
                table: "classes");

            migrationBuilder.DropColumn(
                name: "refunded_visits",
                table: "enrollments");

            migrationBuilder.CreateIndex(
                name: "IX_passes_user_id",
                table: "passes",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_enrollments_class_id",
                table: "enrollments",
                column: "class_id");

            migrationBuilder.CreateIndex(
                name: "IX_classes_room_id",
                table: "classes",
                column: "room_id");
        }
    }
}
