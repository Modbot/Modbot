using System.Text.Json;
using Modbot.Explore;
using Serilog;
using VRChat.API.Client;

// ─────────────────────────────────────────────────────────────────────────────
// Modbot API explorer.
//
// A scratch tool for answering empirical questions about VRChat's API that the
// specs currently record as open. It prints RAW response bodies, because the
// point is to see what VRChat actually returns -- not what the SDK's types say
// it might.
//
// Usage:  dotnet run -- <command> [args]
//         dotnet run -- help
// ─────────────────────────────────────────────────────────────────────────────

var env = DotEnv.Load(Path.Combine(AppContext.BaseDirectory, ".env"))
       ?? DotEnv.Load(".env")
       ?? new Dictionary<string, string>();

Log.Logger = Logging.Create(env.GetValueOrDefault("SEQ_URL"));

var email = env.GetValueOrDefault("VRCHAT_EMAIL");
var password = env.GetValueOrDefault("VRCHAT_PASSWORD");
var totp = env.GetValueOrDefault("VRCHAT_TWO_FACTOR_SECRET");
var groupId = env.GetValueOrDefault("VRCHAT_GROUP_ID");

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var arg1 = args.Length > 1 ? args[1] : null;

if (command is "help" or "-h" or "--help")
{
    PrintHelp();
    return 0;
}

if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
{
    Log.Error("VRCHAT_EMAIL and VRCHAT_PASSWORD must be set in explore/.env (copy .env.example).");
    return 1;
}

Log.Information("Authenticating to VRChat as {Email}", Redact(email));

IVRChat vrchat = new VRChatClientBuilder()
    .WithUsername(email)
    .WithPassword(password)
    .WithTwoFactorSecret(totp ?? string.Empty)
    // WithApplication is required -- VRChat rejects requests without a descriptive User-Agent.
    .WithApplication("ModbotExplore", "2026.9.0", "https://github.com/Modbot/Modbot")
    .Build();

try
{
    switch (command)
    {
        case "whoami":
            Dump("GetCurrentUser", await vrchat.Authentication.GetCurrentUserWithHttpInfoAsync());
            break;

        case "group":
            Require(groupId, "VRCHAT_GROUP_ID");
            Dump("GetGroup", await vrchat.Groups.GetGroupWithHttpInfoAsync(groupId!));
            break;

        case "roles":
            Require(groupId, "VRCHAT_GROUP_ID");
            Dump("GetGroupRoles", await vrchat.Groups.GetGroupRolesWithHttpInfoAsync(groupId!));
            break;

        case "members":
            Require(groupId, "VRCHAT_GROUP_ID");
            // OPEN QUESTION: confirm GroupMember carries no bio/profile data.
            Dump("GetGroupMembers", await vrchat.Groups.GetGroupMembersWithHttpInfoAsync(groupId!, n: 5));
            break;

        case "bans":
            Require(groupId, "VRCHAT_GROUP_ID");
            Dump("GetGroupBans", await vrchat.Groups.GetGroupBansWithHttpInfoAsync(groupId!, n: 5));
            break;

        case "auditlog":
            Require(groupId, "VRCHAT_GROUP_ID");
            Dump("GetGroupAuditLogs", await vrchat.Groups.GetGroupAuditLogsWithHttpInfoAsync(groupId!, n: 10));
            break;

        case "instances":
            Require(groupId, "VRCHAT_GROUP_ID");
            Dump("GetGroupInstances", await vrchat.Groups.GetGroupInstancesWithHttpInfoAsync(groupId!));
            break;

        case "instance":
            // THE BLOCKING QUESTION (M3 open #15):
            // Is Instance.Users populated for a group instance we are not physically in?
            // Pass a full location string, e.g.
            //   wrld_xxx:12345~group(grp_xxx)~groupAccessType(members)~region(use)
            Require(arg1, "<location>");
            Dump("GetInstance", await vrchat.Instances.GetInstanceWithHttpInfoAsync(
                arg1!.Split(':')[0], arg1.Split(':', 2)[1]));
            break;

        case "user":
            // Confirms currentAvatarThumbnailImageUrl and bio are present per-user.
            Require(arg1, "<usr_id>");
            Dump("GetUser", await vrchat.Users.GetUserWithHttpInfoAsync(arg1!));
            break;

        case "search":
            // Heavy rate limits -- 1 req / 3.5 s. Never used on automatic syncs.
            Require(arg1, "<query>");
            Dump("SearchUsers", await vrchat.Users.SearchUsersWithHttpInfoAsync(arg1!, n: 10));
            break;

        default:
            Log.Error("Unknown command {Command}", command);
            PrintHelp();
            return 1;
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "Command {Command} threw", command);
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;

// ── helpers ──────────────────────────────────────────────────────────────────

// Prints the raw body rather than the deserialised object. The SDK's types describe what the spec
// SAYS is returned; this shows what VRChat actually sent, which is the entire point of this tool.
void Dump<T>(string label, ApiResponse<T> response)
{
    var status = (int)response.StatusCode;
    var ok = status is >= 200 and < 300;

    Log.Write(
        ok ? Serilog.Events.LogEventLevel.Information : Serilog.Events.LogEventLevel.Error,
        "{Label} -> {Status} ({Bytes} bytes)",
        label, status, response.RawContent?.Length ?? 0);

    if (!ok)
    {
        Log.Error("Body: {Body}", response.RawContent ?? response.ErrorText);
        return;
    }

    Console.WriteLine();
    Console.WriteLine(Pretty(response.RawContent));
    Console.WriteLine();

    var outFile = Path.Combine("responses", $"{label}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    Directory.CreateDirectory("responses");
    File.WriteAllText(outFile, Pretty(response.RawContent));
    Log.Information("Saved raw response to {File}", outFile);
}

static string Pretty(string? json)
{
    if (string.IsNullOrWhiteSpace(json)) return "(empty body)";
    try
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }
    catch
    {
        return json; // not JSON -- show it as-is rather than swallowing it
    }
}

static string Redact(string value) =>
    value.Length <= 3 ? "***" : value[..2] + new string('*', Math.Min(value.Length - 3, 8)) + value[^1];

static void Require(string? value, string name)
{
    if (string.IsNullOrWhiteSpace(value))
        throw new InvalidOperationException($"{name} is required for this command.");
}

static void PrintHelp() => Console.WriteLine("""
    Modbot API explorer

      dotnet run -- <command> [arg]

    Commands
      whoami                  authenticated user
      group                   group info                      (VRCHAT_GROUP_ID)
      roles                   group roles                      (VRCHAT_GROUP_ID)
      members                 first 5 group members            (VRCHAT_GROUP_ID)
      bans                    first 5 group bans               (VRCHAT_GROUP_ID)
      auditlog                last 10 audit log entries        (VRCHAT_GROUP_ID)
      instances               live group instances             (VRCHAT_GROUP_ID)
      instance <location>     instance detail  -- does it include Users?
      user <usr_id>           full user object -- avatar thumbnail, bio
      search <query>          user search      -- HEAVY RATE LIMIT, 1 req / 3.5s

    Raw bodies print to stdout and are saved under explore/responses/.
    Config comes from explore/.env -- copy .env.example and fill it in.
    """);
