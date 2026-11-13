namespace Macula.Identity;

/// <summary>
/// Petnames: a deterministic, human-readable label for a mesh node id —
/// Docker's adjective_color_animal convention with a four-digit suffix
/// (e.g. "happy_green_rabbit_4831"). A pure function of the node id
/// itself: the same identity gets the same petname across restarts,
/// across every tool that shows it, and on every other agent's roster.
///
/// The suffix exists because the mesh is expected to host THOUSANDS of
/// agents: the word trio alone (64,000 combinations) collides visibly
/// under the birthday problem at a few hundred identities; the trio
/// plus a 4-digit hash group (640,000,000 combinations) stays
/// effectively collision-free at fleet scale while remaining scannable.
///
/// The word lists and derivation are shared verbatim with macula-mcp's
/// petname.ts, macula-rust's petname() and macula-go's identity.Petname:
/// same SHA-256 of the lowercased hex id, same 16-bit reads modulo the
/// list lengths, plus the suffix group.
/// </summary>
public static class Petname
{
    private static readonly string[] AdjectivesA =
    {
        "bold", "bouncy", "brave", "breezy", "calm", "cheerful", "clever", "curious",
        "daring", "eager", "elegant", "fierce", "gentle", "graceful", "humble", "jolly",
        "jovial", "keen", "kind", "lively", "lucky", "mellow", "merry", "nimble",
        "noble", "plucky", "proud", "quiet", "quirky", "radiant", "silly", "sleepy",
        "spry", "sturdy", "tranquil", "upbeat", "vivid", "wise", "witty", "zealous",
    };

    private static readonly string[] AdjectivesB =
    {
        "amber", "azure", "bronze", "coral", "crimson", "cyan", "emerald", "golden",
        "green", "indigo", "ivory", "jade", "lavender", "lilac", "magenta", "maroon",
        "mauve", "navy", "olive", "orange", "peach", "pink", "plum", "purple",
        "red", "rust", "ruby", "sage", "salmon", "scarlet", "sienna", "silver",
        "slate", "tan", "teal", "turquoise", "violet", "yellow", "blue", "copper",
    };

    private static readonly string[] Nouns =
    {
        "antelope", "badger", "beetle", "bison", "cricket", "dolphin", "eagle", "elk",
        "falcon", "ferret", "flamingo", "fox", "gazelle", "gecko", "hare", "heron",
        "ibex", "iguana", "lynx", "marten", "mongoose", "moose", "narwhal", "orca",
        "otter", "owl", "panther", "pelican", "penguin", "rabbit", "raven", "salamander",
        "seal", "sparrow", "tiger", "toucan", "walrus", "weasel", "wolf", "wombat",
    };

    /// <summary>The stable "adjective_color_animal_0000" label for a node
    /// id: same input, same output, on every tool and every machine.</summary>
    public static string For(string nodeId)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(nodeId.ToLowerInvariant()));
        var a = AdjectivesA[ReadU16(digest, 0) % AdjectivesA.Length];
        var b = AdjectivesB[ReadU16(digest, 2) % AdjectivesB.Length];
        var n = Nouns[ReadU16(digest, 4) % Nouns.Length];
        var suffix = ReadU16(digest, 6) % 10_000;
        return $"{a}_{b}_{n}_{suffix:D4}";
    }

    private static ushort ReadU16(byte[] bytes, int offset) =>
        (ushort)((bytes[offset] << 8) | bytes[offset + 1]);
}
