using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TruckVisit.Application.Visits;

namespace TruckVisit.Infrastructure.Persistence.Configurations;

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("idempotency_records");

        // Composite key: the uniqueness guarantee has to come from the database, not from a
        // read-then-write in application code. Two simultaneous retries of the same request would
        // both find nothing and both insert; here the second one fails instead.
        builder.HasKey(record => new { record.Key, record.UserId });

        builder.Property(record => record.Key)
            .HasMaxLength(RegisterVisitHandler.MaxIdempotencyKeyLength)
            .IsRequired();

        builder.Property(record => record.UserId)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(record => record.VisitId).IsRequired();
        builder.Property(record => record.CreatedAt).IsRequired();

        // Supports the retention job that prunes keys once a retry is no longer plausible.
        builder.HasIndex(record => record.CreatedAt)
            .HasDatabaseName("ix_idempotency_created_at");
    }
}
