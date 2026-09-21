using TruckVisit.Domain.Common;
using TruckVisit.Domain.Visits;
using Xunit;

namespace TruckVisit.Domain.Tests;

/// <summary>Covers what it takes to bring a visit into existence.</summary>
public sealed class VisitRegistrationTests
{
    [Fact]
    public void A_registered_visit_starts_pre_registered_with_its_audit_trail_already_open()
    {
        var visit = TestData.RegisteredVisit(createdBy: "gate-operator-7", createdTime: TestData.Now);

        Assert.Equal(VisitStatus.PreRegistered, visit.CurrentStatus);
        Assert.Equal("DOVER", visit.TerminalId.Value);
        Assert.Equal("gate-operator-7", visit.CreatedBy);
        Assert.Equal(TestData.Now, visit.CreatedTime);

        // Registration is itself an auditable event, so the trail is never empty.
        var entry = Assert.Single(visit.StatusHistory);
        Assert.Null(entry.From);
        Assert.Equal(VisitStatus.PreRegistered, entry.To);
        Assert.Equal(1, entry.Sequence);
        Assert.Equal("gate-operator-7", entry.ChangedBy);
        Assert.Equal(visit.Id, entry.VisitId);
    }

    [Fact]
    public void Identifiers_are_time_ordered_so_they_do_not_fragment_the_index()
    {
        // Version 7 GUIDs are generated from the timestamp, so a later visit sorts after an
        // earlier one. Version 4 would scatter inserts across a 51-million-row index.
        var earlier = TestData.RegisteredVisit(createdTime: TestData.Now);
        var later = TestData.RegisteredVisit(createdTime: TestData.Now.AddHours(1));

        Assert.Equal(7, earlier.Id.Version);
        Assert.True(later.Id.CompareTo(earlier.Id) > 0);
    }

    [Fact]
    public void The_terminal_identifier_is_normalised_on_the_way_in()
    {
        var visit = TestData.RegisteredVisit(terminalId: " dover ");

        Assert.Equal("DOVER", visit.TerminalId.Value);
    }

    [Fact]
    public void A_visit_must_declare_at_least_one_movement()
    {
        // A truck admitted with no recorded purpose is a hole in the gate record.
        var exception = Assert.Throws<DomainValidationException>(
            () => TestData.RegisteredVisit(movements: []));

        Assert.Equal("movements", exception.Field);
    }

    [Fact]
    public void A_visit_cannot_declare_more_movements_than_a_truck_could_carry()
    {
        var tooMany = Enumerable
            .Range(0, Visit.MaxMovements + 1)
            .Select(index => TestData.ValidMovement(unitNumber: $"UNIT{index:D5}"))
            .ToArray();

        // Unbounded input on the create endpoint is a denial-of-service vector, not just untidy.
        Assert.Throws<DomainValidationException>(() => TestData.RegisteredVisit(movements: tooMany));
    }

    [Fact]
    public void The_same_unit_cannot_be_listed_twice_for_the_same_operation()
    {
        var duplicated = new[]
        {
            TestData.ValidMovement(MovementType.Delivery, "MSCU1234567"),
            TestData.ValidMovement(MovementType.Delivery, "mscu 123 4567"),
        };

        // The duplicate is only visible after normalisation, which is exactly why normalisation
        // happens in the constructor rather than at the edge.
        var exception = Assert.Throws<DomainValidationException>(
            () => TestData.RegisteredVisit(movements: duplicated));

        Assert.Equal("movements", exception.Field);
    }

    [Fact]
    public void The_same_unit_may_be_both_delivered_and_collected()
    {
        // A legitimate swap: drop a full unit, take an empty one of the same number away.
        var movements = new[]
        {
            TestData.ValidMovement(MovementType.Delivery, "MSCU1234567"),
            TestData.ValidMovement(MovementType.Collection, "MSCU1234567"),
        };

        var visit = TestData.RegisteredVisit(movements: movements);

        Assert.Equal(2, visit.Movements.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void The_acting_principal_must_be_recorded(string? createdBy)
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => TestData.RegisteredVisit(createdBy: createdBy!));

        Assert.Equal("createdBy", exception.Field);
    }

    [Fact]
    public void A_null_truck_or_driver_is_a_programming_error_not_a_validation_failure()
    {
        Assert.Throws<ArgumentNullException>(() => Visit.Register(
            "DOVER", null!, TestData.ValidDriver(), [TestData.ValidMovement()], "op", TestData.Now));

        Assert.Throws<ArgumentNullException>(() => Visit.Register(
            "DOVER", TestData.ValidTruck(), null!, [TestData.ValidMovement()], "op", TestData.Now));
    }

    [Fact]
    public void An_undeclared_movement_type_is_rejected() =>
        Assert.Throws<DomainValidationException>(
            () => Movement.Create((MovementType)99, "MSCU1234567", "YARD1", "BERTH3"));
}
