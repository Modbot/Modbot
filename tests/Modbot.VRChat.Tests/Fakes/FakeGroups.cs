using System.Net;
using NSubstitute;
using VRChat.API.Api;
using VRChat.API.Client;
using VRChat.API.Model;

namespace Modbot.VRChat.Tests.Fakes;

/// <summary>
/// A group's audit log and metadata, served the way VRChat serves them.
/// </summary>
/// <remarks>
/// <para>
/// It pages, rather than handing back whatever the test put in. Paging is where the producer's
/// interesting failures live -- a cursor advanced past a page that was never read, an entry that
/// arrives while a walk is in progress -- and a fake that ignored <c>n</c> and <c>offset</c> would
/// make every one of those tests pass without testing anything.
/// </para>
/// <para>
/// Newest first, which is the order VRChat returns, and filtered by <c>startDate</c> inclusively.
/// Both are assumptions about someone else's API; they are written down here so that if either
/// turns out to be wrong, the place to change it is obvious.
/// </para>
/// </remarks>
public sealed class FakeGroups
{
    private readonly List<GroupAuditLogEntry> _entries = [];

    /// <summary>The group <c>GetGroup</c> answers with. Null means "no body", as a 404 would.</summary>
    public Group? Group { get; set; }

    /// <summary>Force a status other than 200 on the audit log -- 403 for no access, 429 for a limit.</summary>
    public HttpStatusCode AuditLogStatus { get; set; } = HttpStatusCode.OK;

    public HttpStatusCode GroupStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>Every audit-log query, so a test can assert on the window that was asked for.</summary>
    public List<AuditLogQuery> AuditLogQueries { get; } = [];

    public int GroupRequests { get; private set; }

    public int AuditLogRequests => AuditLogQueries.Count;

    public IReadOnlyList<GroupAuditLogEntry> Entries => _entries;

    public FakeGroups Add(params GroupAuditLogEntry[] entries)
    {
        _entries.AddRange(entries);
        return this;
    }

    public IGroupsApi Build()
    {
        var groups = Substitute.For<IGroupsApi>();

        groups
            .GetGroupAuditLogsWithHttpInfoAsync(
                Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>(),
                Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(AuditLogs(
                call.ArgAt<int?>(1) ?? 60,
                call.ArgAt<int?>(2) ?? 0,
                call.ArgAt<DateTime?>(3))));

        groups
            .GetGroupWithHttpInfoAsync(Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                GroupRequests++;

                return Task.FromResult(GroupStatus == HttpStatusCode.OK
                    ? new ApiResponse<Group>(HttpStatusCode.OK, new Multimap<string, string>(), Group!, "{}")
                    : new ApiResponse<Group>(GroupStatus, new Multimap<string, string>(), null!, "{}"));
            });

        return groups;
    }

    private ApiResponse<PaginatedGroupAuditLogEntryList> AuditLogs(int n, int offset, DateTime? startDate)
    {
        AuditLogQueries.Add(new AuditLogQuery(n, offset, startDate));

        if (AuditLogStatus != HttpStatusCode.OK)
        {
            return new ApiResponse<PaginatedGroupAuditLogEntryList>(
                AuditLogStatus, new Multimap<string, string>(), null!, "{}");
        }

        var matching = _entries
            .Where(e => startDate is null || e.CreatedAt >= startDate)
            .OrderByDescending(e => e.CreatedAt)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();

        var page = matching.Skip(offset).Take(n).ToList();
        var body = new PaginatedGroupAuditLogEntryList(
            hasNext: matching.Count > offset + page.Count,
            results: page,
            totalCount: matching.Count);

        return new ApiResponse<PaginatedGroupAuditLogEntryList>(
            HttpStatusCode.OK, new Multimap<string, string>(), body, "{}");
    }

    public sealed record AuditLogQuery(int PageSize, int Offset, DateTime? StartDate);
}
