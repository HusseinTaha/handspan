namespace Handspan.Adb;

/// <summary>
/// The single route from untrusted text to a device command line (spec §71).
/// </summary>
/// <remarks>
/// <para>
/// Android filenames legitimately contain spaces, quotes, semicolons, dollar signs, backticks,
/// newlines and emoji (spec §74). Concatenating one into a shell command is a command-injection bug
/// on the user's own phone, so nothing in this codebase builds a command line without this helper.
/// </para>
/// <para>
/// Single quotes protect everything in POSIX shells except a single quote itself, which is closed,
/// escaped and reopened — the standard <c>'\''</c> dance.
/// </para>
/// </remarks>
public static class ShellQuote
{
    /// <summary>Quotes one argument for safe use in a device shell command.</summary>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        return "'" + argument.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    /// <summary>Quotes a device path for safe use in a device shell command.</summary>
    public static string Quote(Core.Models.DevicePath path) => Quote(path.Value);

    /// <summary>Builds a command from a verb and already-safe flags plus arguments to be quoted.</summary>
    public static string Command(string verb, params string[] argumentsToQuote)
        => argumentsToQuote.Length == 0
            ? verb
            : verb + " " + string.Join(' ', argumentsToQuote.Select(Quote));
}
