using System.Net;
using System.Runtime.CompilerServices;
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

    /// <summary>
    /// The largest audit-log <c>offset</c> answered with a page. Anything above it gets the 400
    /// VRChat really returns, body and all. Lowered by tests that want the refusal to arrive
    /// early rather than after seven and a half thousand entries.
    /// </summary>
    public int OffsetCap { get; set; } = 7_500;

    /// <summary>Every audit-log query, so a test can assert on the window that was asked for.</summary>
    public List<AuditLogQuery> AuditLogQueries { get; } = [];

    public int GroupRequests { get; private set; }

    public int AuditLogRequests => AuditLogQueries.Count;

    public IReadOnlyList<GroupAuditLogEntry> Entries => _entries;

    /// <summary>
    /// The member list, in the order VRChat would page it. Tests add and remove between sweeps;
    /// the fake pages whatever is here at the moment of each request, which is exactly the
    /// instability the sweep's page overlap exists for.
    /// </summary>
    public List<GroupMember> Members { get; } = [];

    /// <summary>The ban list. The same objects VRChat uses for members, with <c>bannedAt</c> set.</summary>
    public List<GroupMember> Bans { get; } = [];

    public HttpStatusCode MembersStatus { get; set; } = HttpStatusCode.OK;

    public HttpStatusCode BansStatus { get; set; } = HttpStatusCode.OK;

    public List<PageQuery> MemberQueries { get; } = [];

    public List<PageQuery> BanQueries { get; } = [];

    public int MemberRequests => MemberQueries.Count;

    public int BanRequests => BanQueries.Count;

    /// <summary>The group's open instances, as <c>/groups/{groupId}/instances</c> lists them.</summary>
    public List<GroupInstance> Instances { get; } = [];

    public HttpStatusCode InstancesStatus { get; set; } = HttpStatusCode.OK;

    public int InstancesRequests { get; private set; }

    /// <summary>One entry in the group's instance list, with no world attached.</summary>
    public static GroupInstance Listed(string location, int memberCount)
    {
        // Built without its constructor, which insists on a whole world object.
        var instance = (GroupInstance)RuntimeHelpers.GetUninitializedObject(typeof(GroupInstance));
        instance.Location = location;
        instance.InstanceId = location[(location.IndexOf(':') + 1)..];
        instance.MemberCount = memberCount;
        return instance;
    }

    public FakeGroups Add(params GroupAuditLogEntry[] entries)
    {
        _entries.AddRange(entries);
        return this;
    }

    public IGroupsApi Build()
    {
        var groups = Substitute.For<IGroupsApi>();

        groups
            .GetGroupMembersWithHttpInfoAsync(
                Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>(),
                Arg.Any<GroupSearchSort?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(Page(
                Members, MemberQueries, MembersStatus,
                call.ArgAt<int?>(1) ?? 60,
                call.ArgAt<int?>(2) ?? 0)));

        groups
            .GetGroupBansWithHttpInfoAsync(
                Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(Page(
                Bans, BanQueries, BansStatus,
                call.ArgAt<int?>(1) ?? 60,
                call.ArgAt<int?>(2) ?? 0)));

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
            .GetGroupInstancesWithHttpInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                InstancesRequests++;

                return Task.FromResult(InstancesStatus == HttpStatusCode.OK
                    ? new ApiResponse<List<GroupInstance>>(HttpStatusCode.OK, new Multimap<string, string>(), [.. Instances], "[]")
                    : new ApiResponse<List<GroupInstance>>(InstancesStatus, new Multimap<string, string>(), null!, "{}"));
            });

        groups
            .GetGroupWithHttpInfoAsync(Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
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

        if (offset > OffsetCap)
        {
            // Verbatim, fullwidth punctuation included: the producer must not be string-matching
            // this, and a fake that tidied it up would let it.
            var refusal = "{\"error\":{\"message\":\"offset＝" + offset
                + " is above the limit․ if you believe this is too low‚ please contact support＠vrchat․com with details․\","
                + "\"status_code\":400}}";

            return new ApiResponse<PaginatedGroupAuditLogEntryList>(
                HttpStatusCode.BadRequest, new Multimap<string, string>(), null!, refusal);
        }

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

    /// <summary>
    /// One page of a list by offset, the way the members and bans endpoints serve it: 200 with an
    /// empty list past the end, never a 400 (audit-log research §7.1), and the JSON body beside
    /// the typed objects because the sweep keeps the entry as VRChat sent it.
    /// </summary>
    private static ApiResponse<List<GroupMember>> Page(
        List<GroupMember> list,
        List<PageQuery> queries,
        HttpStatusCode status,
        int n,
        int offset)
    {
        queries.Add(new PageQuery(n, offset));

        if (status != HttpStatusCode.OK)
            return new ApiResponse<List<GroupMember>>(status, new Multimap<string, string>(), null!, "{}");

        var page = list.Skip(offset).Take(n).ToList();
        var body = "[" + string.Join(",", page.Select(m => m.ToJson())) + "]";

        return new ApiResponse<List<GroupMember>>(HttpStatusCode.OK, new Multimap<string, string>(), page, body);
    }

    public sealed record AuditLogQuery(int PageSize, int Offset, DateTime? StartDate);

    public sealed record PageQuery(int PageSize, int Offset);
}
