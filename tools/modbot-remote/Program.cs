using Modbot.Companion.TestRemote;

// modbot-remote <data-folder> <command> [args]
//
// Sends one command to the test remote of the companion test copy that keeps its things in
// <data-folder> (its MODBOT_DATA_FOLDER), and prints the one line of JSON it answers. Exit code 0
// when the answer says ok, 1 when it says why not, 2 when nothing answered.

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: modbot-remote <data-folder> <command> [args]");
    Console.Error.WriteLine("Commands: " + RemoteCommands.Known);
    return 2;
}

var (pipe, refusal) = TestRemoteSwitch.PipeForFolder(
    args[0],
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

if (pipe is null)
{
    Console.Error.WriteLine(refusal ?? "That folder has no test remote.");
    return 2;
}

// The words go back together as one line, quoted where they hold a space, so a name like
// "Test person" arrives as one word.
var line = string.Join(' ', args.Skip(1).Select(word => word.Contains(' ', StringComparison.Ordinal) ? $"\"{word}\"" : word));

// Long enough for the longest wait and a long scroll.
var answer = await TestRemoteClient.AskAsync(
    pipe,
    line,
    connectTimeout: TimeSpan.FromSeconds(3),
    answerTimeout: TimeSpan.FromMilliseconds(RemoteCommands.LongestWait + 30_000));

if (answer is null)
{
    Console.Error.WriteLine(
        "Nothing answered. Is the test copy running for that folder, with MODBOT_DEBUG_MODE=1, as this Windows account?");
    return 2;
}

Console.WriteLine(answer);
return answer.StartsWith("{\"ok\":true", StringComparison.Ordinal) ? 0 : 1;
