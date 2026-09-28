namespace Inkshelf;

public sealed class UpdateCheck
{
    // Null unless the remote tag parses and is strictly newer than ours. Both
    // sides drop a leading "v" and anything from the first "+": the Docker build
    // stamps non-release images "<version>+pr-34.a1b2c3d", and such a build of
    // 1.0.0 should still be told about 1.0.1.
    internal static string? NewerThan(string local, string remote)
    {
        static Version? Parse(string s)
        {
            s = s.TrimStart('v', 'V');
            var plus = s.IndexOf('+');
            if (plus >= 0) s = s[..plus];
            return Version.TryParse(s, out var v) ? v : null;
        }

        var mine = Parse(local);
        var theirs = Parse(remote);
        return mine is not null && theirs is not null && theirs > mine ? theirs.ToString() : null;
    }
}
