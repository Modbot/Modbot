using Modbot.Core.Users;

namespace Modbot.Core.Tests.Users;

/// <summary>
/// The name a deleted account is left under (username rules and deleting accounts design §3.2).
/// Pure: no database, no host.
/// </summary>
public class DeletedAccountTests
{
    [Fact]
    public void TheNameIsTheSameEveryTimeForTheSameAccount()
    {
        var id = Guid.Parse("0199f0f4-8b2c-7a11-9c3d-4e5f60718293");

        Assert.Equal(DeletedAccount.NameFor(id), DeletedAccount.NameFor(id));
    }

    [Fact]
    public void ItLooksLikeTheNameTheDesignAsksFor()
    {
        var name = DeletedAccount.NameFor(Guid.CreateVersion7());

        Assert.StartsWith("deleted_user_", name, StringComparison.Ordinal);
        Assert.Equal(name.ToLowerInvariant(), name);
        Assert.Equal("deleted_user_".Length + 8, name.Length);
    }

    /// <summary>
    /// The whole point of the shape: it is a name the ordinary rule already accepts, so the stored
    /// username and the name on the screen are one string.
    /// </summary>
    [Fact]
    public void BothFormsSatisfyTheOrdinaryUsernameRule()
    {
        var id = Guid.CreateVersion7();

        Assert.True(UsernameRules.LooksLike(DeletedAccount.NameFor(id)));
        Assert.True(UsernameRules.LooksLike(DeletedAccount.LongNameFor(id)));
    }

    /// <summary>
    /// Version 7 ids made moments apart share their leading characters. The suffix comes from a
    /// hash for exactly that reason, so accounts made in one session do not all look alike.
    /// </summary>
    [Fact]
    public void TwoAccountsMadeTogetherGetDifferentNames()
    {
        var names = Enumerable.Range(0, 200)
            .Select(_ => DeletedAccount.NameFor(Guid.CreateVersion7()))
            .ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheLongFormCarriesTheWholeIdAndStillFitsTheColumn()
    {
        var id = Guid.CreateVersion7();
        var name = DeletedAccount.LongNameFor(id);

        Assert.Contains(id.ToString("N"), name, StringComparison.Ordinal);
        Assert.True(name.Length <= UsernameRules.MaximumLength);
    }

    [Theory]
    [InlineData("deleted_user_0fba8a12", true)]
    [InlineData("DELETED_USER_0fba8a12", true)]
    [InlineData("alice", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ItCanTellOneOfItsOwnNames(string? value, bool expected)
        => Assert.Equal(expected, DeletedAccount.IsOne(value));

    [Fact]
    public void ThePasswordItLeavesBehindIsNeverTheSameTwice()
        => Assert.NotEqual(DeletedAccount.UnguessablePassword(), DeletedAccount.UnguessablePassword());
}
