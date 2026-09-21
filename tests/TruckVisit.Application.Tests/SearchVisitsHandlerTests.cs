using TruckVisit.Application.Abstractions;
using TruckVisit.Application.Visits;
using TruckVisit.Domain.Common;
using TruckVisit.Domain.Visits;
using Xunit;

namespace TruckVisit.Application.Tests;

/// <summary>
/// Covers the search endpoint's two jobs before the query runs: bounding it, and narrowing it to
/// what the caller is allowed to see.
/// </summary>
/// <remarks>
/// These assert on the <see cref="VisitSearchCriteria"/> handed to the repository rather than on
/// results. Scoping is a security control, and the thing worth proving is that the restriction is
/// applied at all — not that a fake happened to return the right rows.
/// </remarks>
public sealed class SearchVisitsHandlerTests
{
    private readonly FakeVisitRepository _repository = new();

    [Fact]
    public async Task An_unscoped_request_is_narrowed_to_the_terminals_the_caller_holds()
    {
        var handler = Handler(new FakeCurrentUser("operator-1", "DOVER", "HARWICH"));

        await handler.HandleAsync(Query(), TestContext.Current.CancellationToken);

        Assert.Equal(
            ["DOVER", "HARWICH"],
            Criteria.TerminalIds!.Order().ToArray());
    }

    [Fact]
    public async Task A_caller_with_global_access_is_not_restricted()
    {
        var handler = Handler(new FakeCurrentUser("auditor-1") { HasGlobalTerminalAccess = true });

        await handler.HandleAsync(Query(), TestContext.Current.CancellationToken);

        // null means unrestricted, and is reachable only through the global-access claim.
        Assert.Null(Criteria.TerminalIds);
    }

    [Fact]
    public async Task A_caller_holding_no_terminals_gets_an_empty_scope_not_an_absent_one()
    {
        var handler = Handler(new FakeCurrentUser("nobody"));

        await handler.HandleAsync(Query(), TestContext.Current.CancellationToken);

        // The distinction is the whole authorization model: an empty collection means "no
        // terminals", null means "all of them". Collapsing the two would turn a principal with no
        // access into one with complete access.
        Assert.NotNull(Criteria.TerminalIds);
        Assert.Empty(Criteria.TerminalIds!);
    }

    [Fact]
    public async Task Asking_for_a_terminal_the_caller_does_not_hold_is_refused_not_emptied()
    {
        var handler = Handler(new FakeCurrentUser("operator-1", "DOVER"));

        var exception = await Assert.ThrowsAsync<TerminalAccessDeniedException>(
            () => handler.HandleAsync(
                Query() with { TerminalId = "HARWICH" }, TestContext.Current.CancellationToken));

        Assert.Equal("HARWICH", exception.TerminalId);

        // Returning an empty page would read as "no trucks today" to an operator whose token was
        // misconfigured — a far more dangerous answer than an error.
        Assert.Null(_repository.LastSearchCriteria);
    }

    [Fact]
    public async Task Asking_for_one_held_terminal_narrows_to_exactly_that_one()
    {
        var handler = Handler(new FakeCurrentUser("operator-1", "DOVER", "HARWICH"));

        await handler.HandleAsync(
            Query() with { TerminalId = " dover " }, TestContext.Current.CancellationToken);

        Assert.Equal(["DOVER"], Criteria.TerminalIds!.ToArray());
    }

    [Fact]
    public async Task Location_filters_are_normalised_so_they_match_what_was_stored()
    {
        var handler = Handler(new FakeCurrentUser("operator-1", "DOVER"));

        await handler.HandleAsync(
            Query() with { MovementFrom = "yard 1", MovementTo = "berth-3" },
            TestContext.Current.CancellationToken);

        Assert.Equal("YARD1", Criteria.MovementFrom);
        Assert.Equal("BERTH-3", Criteria.MovementTo);
    }

    [Fact]
    public async Task Paging_defaults_are_applied_when_nothing_is_asked_for()
    {
        var handler = Handler(new FakeCurrentUser("operator-1", "DOVER"));

        await handler.HandleAsync(Query(), TestContext.Current.CancellationToken);

        Assert.Equal(PagingDefaults.FirstPage, Criteria.Page);
        Assert.Equal(PagingDefaults.DefaultPageSize, Criteria.PageSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_page_below_the_first_is_rejected(int page)
    {
        var exception = await Assert.ThrowsAsync<RequestValidationException>(
            () => Handler(new FakeCurrentUser("operator-1", "DOVER"))
                .HandleAsync(Query() with { Page = page }, TestContext.Current.CancellationToken));

        Assert.Equal("page", exception.Field);
    }

    [Fact]
    public async Task A_page_size_above_the_maximum_is_rejected_rather_than_clamped()
    {
        // Clamping silently would hand back a page that does not match the request and make the
        // caller's own paging arithmetic wrong.
        var exception = await Assert.ThrowsAsync<RequestValidationException>(
            () => Handler(new FakeCurrentUser("operator-1", "DOVER")).HandleAsync(
                Query() with { PageSize = PagingDefaults.MaxPageSize + 1 },
                TestContext.Current.CancellationToken));

        Assert.Equal("pageSize", exception.Field);
    }

    [Fact]
    public async Task An_inverted_date_range_is_rejected()
    {
        var exception = await Assert.ThrowsAsync<RequestValidationException>(
            () => Handler(new FakeCurrentUser("operator-1", "DOVER")).HandleAsync(
                Query() with
                {
                    CreatedTimeFrom = Given.Now,
                    CreatedTimeTo = Given.Now.AddDays(-1),
                },
                TestContext.Current.CancellationToken));

        Assert.Equal("createdTimeFrom", exception.Field);
    }

    [Fact]
    public async Task An_undeclared_status_is_rejected()
    {
        var exception = await Assert.ThrowsAsync<RequestValidationException>(
            () => Handler(new FakeCurrentUser("operator-1", "DOVER")).HandleAsync(
                Query() with { CurrentStatus = (VisitStatus)42 },
                TestContext.Current.CancellationToken));

        Assert.Equal("currentStatus", exception.Field);
    }

    [Fact]
    public async Task An_unusable_location_filter_is_a_bad_request_not_a_silent_empty_page()
    {
        await Assert.ThrowsAsync<DomainValidationException>(
            () => Handler(new FakeCurrentUser("operator-1", "DOVER")).HandleAsync(
                Query() with { MovementFrom = "yard;drop" },
                TestContext.Current.CancellationToken));
    }

    private VisitSearchCriteria Criteria =>
        _repository.LastSearchCriteria ?? throw new InvalidOperationException("Search was not called.");

    private static SearchVisitsQuery Query() =>
        new(null, null, null, null, null, null, null, null, null);

    private SearchVisitsHandler Handler(ICurrentUser user) => new(_repository, user);
}
