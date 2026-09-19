using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Core.Users;

/// <summary>
/// What the per-request session check needs to know about an account, in one read.
/// </summary>
/// <param name="Permissions">The union of the account's roles right now.</param>
/// <param name="VRChatLinked">Whether the person has linked their VRChat account (design §4.3).</param>
public readonly record struct AccountState(
    bool IsDisabled,
    DateTimeOffset? SessionsValidAfter,
    ModbotPermissions Permissions,
    bool VRChatLinked);

/// <summary>
/// Creates staff accounts and checks passwords.
/// </summary>
/// <remarks>
/// Shared kernel rather than a feature slice: the login slice, the onboarding wizard's
/// create-administrator step, the invite flow and the session check all need it, and spec 2.8
/// says shared logic moves into <c>Modbot.Core</c> rather than being reached into from another
/// slice. It writes no facts — the fact writer lives a layer up — so every caller that changes an
/// account is responsible for recording that it did.
/// </remarks>
public sealed class UserAccountService
{
    /// <summary>
    /// A real hash of a throwaway password, verified against when no account matches, so an
    /// unknown username costs the same time as a known one. Without it, response latency is a
    /// user-enumeration oracle on the login endpoint.
    /// </summary>
    private static readonly string DecoyHash =
        new PasswordHasher<ModbotUser>().HashPassword(new ModbotUser(), "no account matched");

    private readonly ModbotContext _db;
    private readonly IPasswordHasher<ModbotUser> _hasher;
    private readonly IModbotClock _clock;

    public UserAccountService(ModbotContext db, IPasswordHasher<ModbotUser> hasher, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _hasher = hasher;
        _clock = clock;
    }

    /// <summary>
    /// True when no staff account exists yet. Spec 7.1: that is the condition for first run.
    /// </summary>
    public Task<bool> AnyUsersAsync(CancellationToken ct = default)
        => _db.Users.AnyAsync(ct);

    /// <summary>Accounts with their roles, for anything that needs to show or compute permissions.</summary>
    public IQueryable<ModbotUser> UsersWithRoles()
        => _db.Users.Include(u => u.Roles).ThenInclude(ur => ur.Role);

    public Task<ModbotUser?> FindAsync(Guid id, CancellationToken ct = default)
        => UsersWithRoles().FirstOrDefaultAsync(u => u.Id == id, ct);

    /// <summary>
    /// Creates an account holding exactly these roles. Every id must name an existing role.
    /// </summary>
    /// <remarks>
    /// Does not save on its own when the caller has opened a transaction — the account and the
    /// fact that records its creation are meant to commit together (design §6).
    /// </remarks>
    public async Task<ModbotUser> CreateAsync(
        string username,
        string password,
        string email,
        IReadOnlyCollection<Guid> roleIds,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentNullException.ThrowIfNull(roleIds);

        // Every slice that makes an account checks the username first and hands the person a
        // sentence. This is the backstop for a fifth caller that forgets, and it throws rather
        // than returning a message because by here there is no form left to put one on.
        if (!UsernameRules.LooksLike(username))
            throw new ArgumentException(UsernameRules.WrongCharacters, nameof(username));

        var roles = await RolesAsync(roleIds, ct);

        var user = new ModbotUser
        {
            Username = username.Trim(),
            UsernameNormalized = Normalize(username),

            // A parameter rather than something the caller sets afterwards: every account needs
            // one (design §4.5), and a required parameter is the only version of that rule the
            // compiler checks at each of the four places an account is made.
            Email = EmailAddress.Normalize(email),
            CreatedAt = _clock.UtcNow,
        };

        user.PasswordHash = _hasher.HashPassword(user, password);

        foreach (var role in roles)
            user.Roles.Add(new ModbotUserRole { User = user, UserId = user.Id, Role = role, RoleId = role.Id });

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        return user;
    }

    /// <summary>
    /// Deletes an account by emptying it: everything that says who it belonged to is replaced,
    /// and everything it did is left alone. Returns the name it is left under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row, the id and the roles-history stay, because facts, case files, notes, alerts and
    /// moderation actions all point at the id and would lose their author if the row went (spec
    /// 5.9.1). What goes is the username, the email address, the password, the Discord id and the
    /// whole VRChat link — the fields that make the row a person rather than a number.
    /// </para>
    /// <para>
    /// <strong>Only this row.</strong> The tables that copied the username down at the time of an
    /// event keep what they copied: those rows say what was true then, and rewriting them would
    /// make Modbot's own history disagree with VRChat's and with everybody's memory of it.
    /// </para>
    /// <para>
    /// Roles are cleared, so a deleted account holds no permissions whatever anybody does to the
    /// row afterwards. Sessions end on the spot and the password becomes one nobody typed, so
    /// there is no way back in.
    /// </para>
    /// <para>
    /// Writes no fact — the fact writer lives a layer up, as everywhere else here — so the caller
    /// records the deletion in the same transaction.
    /// </para>
    /// </remarks>
    public async Task<string> DeleteAsync(ModbotUser user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var name = DeletedAccount.NameFor(user.Id);

        // The short name is a hash of an id that is already unique, so this only ever fires on a
        // collision nobody will see. The long form carries the whole id and so cannot collide.
        var normalized = Normalize(name);
        if (await _db.Users.AnyAsync(u => u.UsernameNormalized == normalized && u.Id != user.Id, ct))
        {
            name = DeletedAccount.LongNameFor(user.Id);
            normalized = Normalize(name);
        }

        user.Username = name;
        user.UsernameNormalized = normalized;
        user.Email = null;
        user.DiscordUserId = null;
        user.PasswordHash = _hasher.HashPassword(user, DeletedAccount.UnguessablePassword());

        user.VRChatUserId = null;
        user.VRChatDisplayName = null;
        user.VRChatLinkedAt = null;
        user.VRChatLinkCode = null;
        user.VRChatLinkCodeExpiresAt = null;
        user.VRChatLinkPendingUserId = null;
        user.VRChatLinkChecks = 0;
        user.VRChatLinkLastCheckAt = null;

        user.Roles.Clear();

        user.IsDisabled = true;
        user.DeletedAt = _clock.UtcNow;
        user.SessionsValidAfter = _clock.UtcNow;

        await _db.SaveChangesAsync(ct);

        return name;
    }

    /// <summary>
    /// Replaces the account's roles with exactly these. Returns the names before and after, for
    /// the fact that records the change.
    /// </summary>
    public async Task<(IReadOnlyList<string> Before, IReadOnlyList<string> After)> SetRolesAsync(
        ModbotUser user,
        IReadOnlyCollection<Guid> roleIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(roleIds);

        var roles = await RolesAsync(roleIds, ct);

        var before = user.Roles.Select(r => r.Role.Name).Order().ToList();

        user.Roles.Clear();
        foreach (var role in roles)
            user.Roles.Add(new ModbotUserRole { User = user, UserId = user.Id, Role = role, RoleId = role.Id });

        await _db.SaveChangesAsync(ct);

        var after = roles.Select(r => r.Name).Order().ToList();
        return (before, after);
    }

    /// <summary>
    /// Loads the named roles, refusing an id that names nothing. Silently dropping an unknown id
    /// would create an account with fewer permissions than the administrator chose, and nothing
    /// would say so.
    /// </summary>
    public async Task<IReadOnlyList<ModbotRole>> RolesAsync(
        IReadOnlyCollection<Guid> roleIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(roleIds);

        var distinct = roleIds.Distinct().ToList();
        var roles = await _db.Roles.Where(r => distinct.Contains(r.Id)).ToListAsync(ct);

        if (roles.Count != distinct.Count)
            throw new UnknownRoleException();

        return roles;
    }

    /// <summary>
    /// Returns the account when the password is right, and null otherwise -- never a reason.
    /// The caller cannot tell "no such user" from "wrong password" from "disabled", because a
    /// login form that distinguishes them is a membership oracle for anyone who finds it.
    /// </summary>
    public async Task<ModbotUser?> VerifyCredentialsAsync(
        string usernameOrEmail,
        string password,
        CancellationToken ct = default)
    {
        var user = await FindBySignInAsync(usernameOrEmail, ct);

        if (user is null || user.IsDisabled)
        {
            _hasher.VerifyHashedPassword(new ModbotUser(), DecoyHash, password ?? string.Empty);
            return null;
        }

        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? string.Empty);
        if (result == PasswordVerificationResult.Failed)
            return null;

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = _hasher.HashPassword(user, password!);

        user.LastLoginAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);

        return user;
    }

    /// <summary>Whether this password is the account's current one. For "change my password".</summary>
    public bool PasswordMatches(ModbotUser user, string password)
    {
        ArgumentNullException.ThrowIfNull(user);

        return _hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? string.Empty)
            != PasswordVerificationResult.Failed;
    }

    /// <summary>Sets a new password. Does not, by itself, end any session — see <see cref="EndSessionsAsync"/>.</summary>
    public async Task SetPasswordAsync(ModbotUser user, string password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrEmpty(password);

        user.PasswordHash = _hasher.HashPassword(user, password);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Ends every session this account has: anything signed in before now is refused on its next
    /// request (design §5). Returns the instant, so a caller that wants to keep its own session
    /// can re-issue it stamped with the same time.
    /// </summary>
    public async Task<DateTimeOffset> EndSessionsAsync(ModbotUser user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var now = _clock.UtcNow;
        user.SessionsValidAfter = now;
        await _db.SaveChangesAsync(ct);

        return now;
    }

    /// <summary>
    /// The one read the session check makes on every request. Null when the account no longer
    /// exists.
    /// </summary>
    public async Task<AccountState?> StateAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => new
            {
                u.IsDisabled,
                u.SessionsValidAfter,
                Permissions = u.Roles.Select(r => r.Role.Permissions).ToList(),
                Linked = u.VRChatUserId != null && u.VRChatUserId != "",
            })
            .FirstOrDefaultAsync(ct);

        return row is null
            ? null
            : new AccountState(
                row.IsDisabled, row.SessionsValidAfter, ModbotRole.Union(row.Permissions), row.Linked);
    }

    /// <summary>
    /// The account this sign-in names, matched against the username <em>or</em> the email address,
    /// both case-insensitively. Null when neither matches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One field on the form and one query here (design §4.6). Two queries -- username first, then
    /// email -- would take measurably longer for an address than for a username, and the time a
    /// sign-in takes is visible to whoever is trying names.
    /// </para>
    /// <para>
    /// <strong><see cref="UsernameRules"/> is not applied here and must never be.</strong> This
    /// matches what was typed against what is stored; an account made before that rule existed may
    /// hold a space, a dot or an accent in its name, and it signs in the same as any other.
    /// </para>
    /// </remarks>
    public Task<ModbotUser?> FindBySignInAsync(string usernameOrEmail, CancellationToken ct = default)
    {
        var (username, email) = SignInKeys(usernameOrEmail);

        return UsersWithRoles()
            .FirstOrDefaultAsync(u => u.UsernameNormalized == username || u.Email == email, ct);
    }

    /// <summary>The id of the account this sign-in names, if there is one. For attributing a failed login.</summary>
    public async Task<Guid?> FindIdAsync(string usernameOrEmail, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(usernameOrEmail))
            return null;

        var (username, email) = SignInKeys(usernameOrEmail);

        return await _db.Users
            .AsNoTracking()
            .Where(u => u.UsernameNormalized == username || u.Email == email)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// What was typed, in both stored forms. The email key is a string that cannot be an address
    /// when nothing was typed, so a blank sign-in cannot match an account whose email is null --
    /// null never equals anything in SQL, but an empty string would equal an empty column.
    /// </summary>
    private static (string Username, string Email) SignInKeys(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
            return (string.Empty, string.Empty);

        return (Normalize(typed), EmailAddress.Normalize(typed) ?? string.Empty);
    }

    /// <summary>
    /// Whether another account already holds this address. Checked before saving so the person
    /// gets a sentence rather than a unique-index violation.
    /// </summary>
    /// <param name="exceptUserId">The account being changed, which does not count against itself.</param>
    public Task<bool> EmailTakenAsync(string email, Guid? exceptUserId = null, CancellationToken ct = default)
    {
        var normalized = EmailAddress.Normalize(email);

        if (normalized is null)
            return Task.FromResult(false);

        return _db.Users
            .AsNoTracking()
            .AnyAsync(u => u.Email == normalized && (exceptUserId == null || u.Id != exceptUserId), ct);
    }

    public Task<ModbotUser?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        var normalized = string.IsNullOrWhiteSpace(username) ? string.Empty : Normalize(username);
        return UsersWithRoles().FirstOrDefaultAsync(u => u.UsernameNormalized == normalized, ct);
    }

    /// <summary>
    /// Whether, after <paramref name="change"/> is applied, at least one enabled account would still
    /// hold the Administrator permission (design §3.4).
    /// </summary>
    /// <param name="change">
    /// Applied to the account named by its key before counting: its new enabled state and its new
    /// permissions. Null means count things as they are.
    /// </param>
    public async Task<bool> AnAdministratorWouldRemainAsync(
        (Guid UserId, bool Enabled, ModbotPermissions Permissions)? change,
        CancellationToken ct = default)
    {
        var accounts = await _db.Users
            .AsNoTracking()
            .Select(u => new
            {
                u.Id,
                u.IsDisabled,
                Permissions = u.Roles.Select(r => r.Role.Permissions).ToList(),
            })
            .ToListAsync(ct);

        foreach (var account in accounts)
        {
            var enabled = !account.IsDisabled;
            var permissions = ModbotRole.Union(account.Permissions);

            if (change is { } c && c.UserId == account.Id)
            {
                enabled = c.Enabled;
                permissions = c.Permissions;
            }

            if (enabled && permissions.HasFlag(ModbotPermissions.Administrator))
                return true;
        }

        return false;
    }

    public static string Normalize(string username) => username.Trim().ToUpperInvariant();
}

/// <summary>A role id that names no role. Callers turn it into a 400.</summary>
public sealed class UnknownRoleException : Exception
{
    public UnknownRoleException() : base("One of the roles does not exist.") { }
}
