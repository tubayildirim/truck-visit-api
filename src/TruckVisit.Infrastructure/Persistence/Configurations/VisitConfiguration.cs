using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TruckVisit.Domain.Visits;

namespace TruckVisit.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the visit aggregate onto three tables: the visit row, its movements and its audit trail.
/// </summary>
/// <remarks>
/// <para>
/// Movements and history are mapped as owned collections rather than independent entities. That is
/// not a storage detail — it encodes the aggregate boundary in the model, so nothing outside
/// <see cref="Visit"/> can load or modify them on their own.
/// </para>
/// <para>
/// Enums and value objects are stored as text. A seven-year retention period means these rows
/// outlive the code that wrote them, and "AtGate" is still legible to an auditor in 2033 while a
/// bare 2 is only legible next to a copy of this enum (ADR-004).
/// </para>
/// </remarks>
internal sealed class VisitConfiguration : IEntityTypeConfiguration<Visit>
{
    private static readonly ValueConverter<TerminalCode, string> TerminalConverter =
        new(code => code.Value, value => TerminalCode.Create(value));

    private static readonly ValueConverter<UnitNumber, string> UnitNumberConverter =
        new(code => code.Value, value => UnitNumber.Create(value));

    private static readonly ValueConverter<LicensePlate, string> LicensePlateConverter =
        new(code => code.Value, value => LicensePlate.Create(value));

    private static readonly ValueConverter<LocationCode, string> LocationConverter =
        new(code => code.Value, value => LocationCode.Create(value, "location"));

    public void Configure(EntityTypeBuilder<Visit> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("visits");

        builder.HasKey(visit => visit.Id);

        builder.Property(visit => visit.Id).ValueGeneratedNever();

        builder.Property(visit => visit.TerminalId)
            .HasConversion(TerminalConverter)
            .HasMaxLength(TerminalCode.MaxLength)
            .IsRequired();

        builder.Property(visit => visit.CurrentStatus)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(visit => visit.CreatedTime).IsRequired();
        builder.Property(visit => visit.LastStatusChangedAt).IsRequired();

        builder.Property(visit => visit.CreatedBy)
            .HasMaxLength(Visit.MaxActorLength)
            .IsRequired();

        // PostgreSQL's system column: a free optimistic-concurrency token that costs no extra
        // storage and no application code to maintain (ADR-010).
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .IsRowVersion();

        ConfigureTruck(builder);
        ConfigureDriver(builder);
        ConfigureMovements(builder);
        ConfigureStatusHistory(builder);
        ConfigureIndexes(builder);
    }

    private static void ConfigureTruck(EntityTypeBuilder<Visit> builder)
    {
        builder.OwnsOne(visit => visit.Truck, truck =>
        {
            truck.Property(value => value.UnitNumber)
                .HasConversion(UnitNumberConverter)
                .HasColumnName("truck_unit_number")
                .HasMaxLength(UnitNumber.MaxLength)
                .IsRequired();

            truck.Property(value => value.LicensePlate)
                .HasConversion(LicensePlateConverter)
                .HasColumnName("truck_license_plate")
                .HasMaxLength(LicensePlate.MaxLength)
                .IsRequired();

            // Gate staff routinely look a truck up by the plate in front of them.
            truck.HasIndex(value => value.LicensePlate);
        });

        builder.Navigation(visit => visit.Truck).IsRequired();
    }

    private static void ConfigureDriver(EntityTypeBuilder<Visit> builder)
    {
        builder.OwnsOne(visit => visit.Driver, driver =>
        {
            driver.Property(value => value.FullName)
                .HasColumnName("driver_full_name")
                .HasMaxLength(Driver.MaxFullNameLength)
                .IsRequired();

            driver.Property(value => value.DocumentId)
                .HasColumnName("driver_document_id")
                .HasMaxLength(Driver.MaxDocumentIdLength)
                .IsRequired();

            driver.Property(value => value.CompanyName)
                .HasColumnName("driver_company_name")
                .HasMaxLength(Driver.MaxCompanyNameLength)
                .IsRequired();

            driver.Property(value => value.PhoneNumber)
                .HasColumnName("driver_phone_number")
                .HasMaxLength(Driver.MaxPhoneNumberLength);

            // Operations routinely ask "what is this haulier doing at our terminals today" —
            // for billing queries, and when a carrier is suspended.
            driver.HasIndex(value => value.CompanyName);
        });

        builder.Navigation(visit => visit.Driver).IsRequired();
    }

    private static void ConfigureMovements(EntityTypeBuilder<Visit> builder)
    {
        builder.OwnsMany(visit => visit.Movements, movement =>
        {
            movement.ToTable("visit_movements");
            movement.WithOwner().HasForeignKey("VisitId");

            movement.HasKey(value => value.Id);
            movement.Property(value => value.Id).ValueGeneratedNever();

            movement.Property(value => value.Type)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();

            movement.Property(value => value.UnitNumber)
                .HasConversion(UnitNumberConverter)
                .HasMaxLength(UnitNumber.MaxLength)
                .IsRequired();

            movement.Property(value => value.From)
                .HasConversion(LocationConverter)
                .HasMaxLength(LocationCode.MaxLength)
                .IsRequired();

            movement.Property(value => value.To)
                .HasConversion(LocationConverter)
                .HasMaxLength(LocationCode.MaxLength)
                .IsRequired();

            movement.Property(value => value.CompletedAt);

            movement.Property(value => value.CompletedBy)
                .HasMaxLength(Visit.MaxActorLength);

            // The movementFrom / movementTo search filters resolve through these.
            movement.HasIndex(value => value.From);
            movement.HasIndex(value => value.To);
            movement.HasIndex(value => value.UnitNumber);

            // Serves both the movementCompleted* range filter and the outstanding-work query.
            // Nullable by design: a null here *is* the "not done yet" state.
            movement.HasIndex(value => value.CompletedAt);
        });
    }

    private static void ConfigureStatusHistory(EntityTypeBuilder<Visit> builder)
    {
        builder.OwnsMany(visit => visit.StatusHistory, history =>
        {
            history.ToTable("visit_status_history");
            history.WithOwner().HasForeignKey(entry => entry.VisitId);

            history.HasKey(entry => entry.Id);
            history.Property(entry => entry.Id).ValueGeneratedNever();

            history.Property(entry => entry.Sequence).IsRequired();

            // Nullable on purpose: the first entry records registration and has no prior status.
            history.Property(entry => entry.From)
                .HasConversion<string>()
                .HasMaxLength(32);

            history.Property(entry => entry.To)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();

            history.Property(entry => entry.ChangedAt).IsRequired();

            history.Property(entry => entry.ChangedBy)
                .HasMaxLength(Visit.MaxActorLength)
                .IsRequired();

            history.Property(entry => entry.Reason)
                .HasMaxLength(StatusChange.MaxReasonLength);

            // Fixed-length by definition: a SHA-256 digest in lowercase hex is always 64
            // characters. Declaring the exact length lets the database reject a truncated or
            // padded value outright rather than storing something that can never verify.
            history.Property(entry => entry.EntryHash)
                .HasMaxLength(StatusChange.HashLength)
                .IsFixedLength()
                .IsRequired();

            // Null only for the first entry in a visit's chain.
            history.Property(entry => entry.PreviousHash)
                .HasMaxLength(StatusChange.HashLength)
                .IsFixedLength();

            // Unique, so a duplicated append is refused by the database rather than silently
            // producing two "step 3"s in an audit trail someone will one day have to defend.
            history.HasIndex(entry => new { entry.VisitId, entry.Sequence }).IsUnique();
        });
    }

    private static void ConfigureIndexes(EntityTypeBuilder<Visit> builder)
    {
        // The search endpoint's dominant shape: scoped to a terminal, usually narrowed by status,
        // and ordered by recency. Leading with TerminalId also means every query is physically
        // confined to the tenant it is allowed to see.
        builder.HasIndex(visit => new { visit.TerminalId, visit.CurrentStatus, visit.CreatedTime })
            .HasDatabaseName("ix_visits_terminal_status_created");

        // Same scope without a status filter — the "everything at this terminal today" view.
        builder.HasIndex(visit => new { visit.TerminalId, visit.CreatedTime })
            .HasDatabaseName("ix_visits_terminal_created");

        builder.HasIndex(visit => visit.CreatedBy)
            .HasDatabaseName("ix_visits_created_by");
    }
}
