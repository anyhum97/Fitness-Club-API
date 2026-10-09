using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataAccessLayer.Configurations;

public class WaitlistEntryConfiguration : IEntityTypeConfiguration<WaitlistEntry>
{
    public void Configure(EntityTypeBuilder<WaitlistEntry> builder)
    {
        builder.ToTable("waitlist_entries", table =>
            table.HasCheckConstraint("ck_waitlist_entries_seats", "seats > 0"));

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.ClassId).HasColumnName("class_id");
        builder.Property(x => x.Seats).HasColumnName("seats");
        builder.Property(x => x.Status).HasColumnName("status").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.CancelledAt).HasColumnName("cancelled_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.PromotedAt).HasColumnName("promoted_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.EnrollmentId).HasColumnName("enrollment_id");

        builder
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId);

        builder
            .HasOne<Class>()
            .WithMany()
            .HasForeignKey(x => x.ClassId);

        builder
            .HasOne<Enrollment>()
            .WithMany()
            .HasForeignKey(x => x.EnrollmentId);

        builder
            .HasIndex(x => new { x.UserId, x.ClassId })
            .HasDatabaseName("ux_waitlist_entries_waiting_user_class")
            .IsUnique()
            .HasFilter("status = 'waiting'");

        builder
            .HasIndex(x => new { x.ClassId, x.CreatedAt, x.Id })
            .HasDatabaseName("ix_waitlist_entries_waiting_queue")
            .HasFilter("status = 'waiting'");
    }
}
