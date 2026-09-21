using TruckVisit.Application.Abstractions;
using TruckVisit.Application.Visits;
using TruckVisit.Domain.Common;
using TruckVisit.Domain.Visits;
using Xunit;

namespace TruckVisit.Application.Tests;

/// <summary>
/// Covers what the use case adds on top of the domain: authorization, attribution and idempotency.
/// </summary>
public sealed class RegisterVisitHandlerTests
{
    private readonly FakeVisitRepository _repository = new();
    private readonly FakeIdempotencyStore _idempotency = new();

    [Fact]
    public async Task A_visit_is_registered_at_a_terminal_the_caller_holds()
    {
        var handler = Handler(new FakeCurrentUser("operator-1", "DOVER"));

        var result = await handler.HandleAsync(
            Given.Command(), idempotencyKey: null, TestContext.Current.CancellationToken);

        Assert.False(result.WasReplayed);
        Assert.Equal("DOVER", result.Visit.TerminalId);
        Assert.Single(_repository.Stored);
        Assert.Equal(1, _repository.SaveCount);
    }

    [Fact]
    public async Task Registering_at_a_terminal_the_caller_does_not_hold_is_refused()
    {
        var handler = Handler(new FakeCurrentUser("operator-1", "HARWICH"));

        var exception = await Assert.ThrowsAsync<TerminalAccessDeniedException>(
            () => handler.HandleAsync(
                Given.Command("DOVER"), idempotencyKey: null, TestContext.Current.CancellationToken));

        Assert.Equal("DOVER", exception.TerminalId);

        // Refused before anything is written, not rolled back afterwards.
        Assert.Empty(_repository.Stored);
        Assert.Equal(0, _repository.SaveCount);
    }

    [Fact]
    public async Task Authorization_compares_the_normalised_terminal_not_the_raw_text()
    {
        // The token holds "DOVER"; the request says " dover ". Without normalising both sides
        // through the same value object, a legitimate operator would be locked out of their own
        // terminal — or, with the comparison the other way round, someone else let in.
        var handler = Handler(new FakeCurrentUser("operator-1", "DOVER"));

        var result = await handler.HandleAsync(
            Given.Command(" dover "), idempotencyKey: null, TestContext.Current.CancellationToken);

        Assert.Equal("DOVER", result.Visit.TerminalId);
    }

    [Fact]
    public async Task The_audit_trail_names_the_authenticated_caller()
    {
        var handler = Handler(new FakeCurrentUser("gate-operator-7", "DOVER"));

        var result = await handler.HandleAsync(
            Given.Command(), idempotencyKey: null, TestContext.Current.CancellationToken);

        // There is no way for a client to influence this: the command carries no createdBy field
        // at all, which is the point.
        Assert.Equal("gate-operator-7", result.Visit.CreatedBy);
        Assert.Equal("gate-operator-7", result.Visit.StatusHistory[0].ChangedBy);
    }

    [Fact]
    public async Task The_visit_is_timestamped_by_the_server_clock()
    {
        var handler = Handler(new FakeCurrentUser("operator-1", "DOVER"));

        var result = await handler.HandleAsync(
            Given.Command(), idempotencyKey: null, TestContext.Current.CancellationToken);

        Assert.Equal(Given.Now, result.Visit.CreatedTime);
    }

    [Fact]
    public async Task Repeating_a_request_with_the_same_idempotency_key_returns_the_first_visit()
    {
        var handler = Handler(new FakeCurrentUser("operator-1", "DOVER"));
        const string key = "gate-3-20260921-0001";

        var first = await handler.HandleAsync(
            Given.Command(), key, TestContext.Current.CancellationToken);

        var retry = await handler.HandleAsync(
            Given.Command(), key, TestContext.Current.CancellationToken);

        // One arrival, one record — the failure mode this exists to prevent is one lorry becoming
        // three rows in an audit trail, which no later correction can fully undo.
        Assert.False(first.WasReplayed);
        Assert.True(retry.WasReplayed);
        Assert.Equal(first.Visit.Id, retry.Visit.Id);
        Assert.Single(_repository.Stored);
    }

    [Fact]
    public async Task Idempotency_keys_are_scoped_to_the_caller()
    {
        const string key = "shared-key";

        await Handler(new FakeCurrentUser("operator-1", "DOVER"))
            .HandleAsync(Given.Command(), key, TestContext.Current.CancellationToken);

        var other = await Handler(new FakeCurrentUser("operator-2", "DOVER"))
            .HandleAsync(Given.Command(), key, TestContext.Current.CancellationToken);

        // Two clients choosing the same string must not collide, and must not be able to read
        // each other's results by guessing one.
        Assert.False(other.WasReplayed);
        Assert.Equal(2, _repository.Stored.Count);
    }

    [Fact]
    public async Task Without_a_key_every_request_creates_a_visit()
    {
        var handler = Handler(new FakeCurrentUser("operator-1", "DOVER"));

        await handler.HandleAsync(Given.Command(), null, TestContext.Current.CancellationToken);
        await handler.HandleAsync(Given.Command(), "   ", TestContext.Current.CancellationToken);

        // Blank is treated as absent, not as a key of its own.
        Assert.Equal(2, _repository.Stored.Count);
    }

    [Fact]
    public async Task An_oversized_idempotency_key_is_rejected()
    {
        var handler = Handler(new FakeCurrentUser("operator-1", "DOVER"));

        var exception = await Assert.ThrowsAsync<RequestValidationException>(
            () => handler.HandleAsync(
                Given.Command(),
                new string('k', RegisterVisitHandler.MaxIdempotencyKeyLength + 1),
                TestContext.Current.CancellationToken));

        Assert.Equal("Idempotency-Key", exception.Field);
    }

    [Fact]
    public async Task Domain_rules_still_apply_and_nothing_is_written_when_they_fail()
    {
        var handler = Handler(new FakeCurrentUser("operator-1", "DOVER"));

        var command = Given.Command() with { Movements = [] };

        var exception = await Assert.ThrowsAsync<DomainValidationException>(
            () => handler.HandleAsync(command, null, TestContext.Current.CancellationToken));

        Assert.Equal("movements", exception.Field);
        Assert.Empty(_repository.Stored);
    }

    private RegisterVisitHandler Handler(ICurrentUser user) =>
        new(_repository, _idempotency, user, new FixedTimeProvider(Given.Now));
}
