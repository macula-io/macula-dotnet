namespace Macula.Connection;

/// <summary>
/// Whether a subscription's topic matches an event's topic, by the station's
/// own rule (macula_topic_pattern.erl): both split on "/", the segment counts
/// must be equal, and each segment matches exactly or is "*", which matches
/// exactly one whole segment. There is no "**" and no partial-segment glob:
/// "foo*bar" is literal.
/// </summary>
internal static class TopicPattern
{
    internal static bool Matches(string pattern, string topic)
    {
        var wanted = pattern.Split('/');
        var actual = topic.Split('/');
        if (wanted.Length != actual.Length)
        {
            return false;
        }
        for (var i = 0; i < wanted.Length; i++)
        {
            if (wanted[i] != "*" && !string.Equals(wanted[i], actual[i], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }
}
