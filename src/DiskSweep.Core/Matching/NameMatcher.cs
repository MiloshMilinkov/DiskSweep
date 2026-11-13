using System.Text;
using DiskSweep.Core.SystemInfo;

namespace DiskSweep.Core.Matching;

/// <summary><paramref name="Owner"/> describes what matched, e.g. "Contoso Editor (publisher)".</summary>
public readonly record struct NameMatch(double Score, string? Owner);

/// <summary>
/// Fuzzy comparison of a folder name against installed programs (name, publisher, install folder, exe names).
/// It is generous on purpose: a false match only means a folder is NOT flagged, which is the safe outcome.
/// </summary>
public sealed class NameMatcher
{
    public const double MatchThreshold = 0.75;

    private static readonly HashSet<string> NoiseTokens = new(StringComparer.Ordinal)
    {
        "inc", "llc", "ltd", "limited", "corp", "corporation", "co", "company", "gmbh", "ag", "sa", "srl", "bv", "sro",
        "software", "technologies", "technology", "the", "x64", "x86", "64bit", "32bit", "bit", "edition", "version",
        "for", "and", "of", "by", "windows", "win", "app", "application", "desktop", "setup", "installer",
    };

    private readonly List<(string Name, string Owner, string Compact, HashSet<string> Tokens)> _candidates = [];
    private readonly HashSet<string> _seenNames = new(StringComparer.OrdinalIgnoreCase);

    public NameMatcher(IEnumerable<InstalledProgram> programs, IEnumerable<StorePackage> packages)
    {
        foreach (var p in programs)
        {
            Add(p.DisplayName, p.DisplayName);
            if (p.Publisher is { } pub) Add(pub, $"{p.DisplayName} (publisher)");
            if (p.InstallLocation is { } loc) Add(Path.GetFileName(loc), $"{p.DisplayName} (install folder)");
            foreach (string f in p.RegisteredFiles)
            {
                string exe = Path.GetFileNameWithoutExtension(f);
                if (!exe.StartsWith("unins", StringComparison.OrdinalIgnoreCase)) Add(exe, $"{p.DisplayName} (program file)");
                string? parent = Path.GetFileName(Path.GetDirectoryName(f));
                if (!string.IsNullOrEmpty(parent)) Add(parent, $"{p.DisplayName} (program folder)");
            }
        }
        foreach (var pkg in packages)
        {
            Add(pkg.Name, $"Store app {pkg.Name}");
            int dot = pkg.Name.IndexOf('.');
            if (dot > 0)
            {
                Add(pkg.Name[..dot], $"Store app {pkg.Name} (publisher)");
                Add(pkg.Name[(dot + 1)..], $"Store app {pkg.Name}");
            }
            if (pkg.DisplayName is { } d) Add(d, $"Store app {d}");
        }
    }

    private void Add(string? name, string owner)
    {
        // The same name often appears many times (one publisher, many programs); the score depends only on the name.
        if (string.IsNullOrWhiteSpace(name) || !_seenNames.Add(name)) return;
        var tokens = Tokens(name);
        string compact = Compact(name);
        if (compact.Length >= 2) _candidates.Add((name, owner, compact, tokens));
    }

    public NameMatch BestMatch(string folderName)
    {
        string compact = Compact(folderName);
        var tokens = Tokens(folderName);
        var best = new NameMatch(0, null);
        foreach (var c in _candidates)
        {
            double s = Score(compact, tokens, c.Compact, c.Tokens);
            if (s > best.Score) best = new NameMatch(s, c.Owner);
            if (s >= 1.0) break;
        }
        return best;
    }

    public static double Score(string a, string b) => Score(Compact(a), Tokens(a), Compact(b), Tokens(b));

    private static double Score(string fa, HashSet<string> ta, string fb, HashSet<string> tb)
    {
        if (fa.Length < 2 || fb.Length < 2) return 0;
        if (fa == fb) return 1.0;

        double score = 0;
        if (fa.Length >= 4 && fb.Contains(fa, StringComparison.Ordinal)) score = Math.Max(score, 0.9);
        if (fb.Length >= 4 && fa.Contains(fb, StringComparison.Ordinal)) score = Math.Max(score, 0.9);

        if (ta.Count > 0 && tb.Count > 0)
        {
            int common = ta.Count(tb.Contains);
            if (common == ta.Count && ta.Sum(t => t.Length) >= 4) score = Math.Max(score, 0.85);
            else if (common == tb.Count && tb.Sum(t => t.Length) >= 4) score = Math.Max(score, 0.85);
            double jaccard = (double)common / (ta.Count + tb.Count - common);
            score = Math.Max(score, jaccard * 0.8);
        }

        int max = Math.Max(fa.Length, fb.Length);
        if (max <= 64)
        {
            double similarity = 1.0 - (double)Levenshtein(fa, fb) / max;
            score = Math.Max(score, similarity);
        }
        return score;
    }

    /// <summary>Lower-case letters and digits only, noise words removed: "Contoso, Inc." → "contoso".</summary>
    internal static string Compact(string s)
    {
        var tokens = SplitWords(s).Where(t => !NoiseTokens.Contains(t)).ToList();
        if (tokens.Count == 0) tokens = SplitWords(s).ToList();
        return string.Concat(tokens);
    }

    internal static HashSet<string> Tokens(string s) =>
        SplitWords(s).Where(t => t.Length >= 2 && !NoiseTokens.Contains(t) && !t.All(char.IsDigit)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Splits on non-alphanumerics and camelCase: "GitHubDesktop_v2" → github, desktop, v2.</summary>
    private static IEnumerable<string> SplitWords(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (!char.IsLetterOrDigit(c))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                continue;
            }
            bool boundary = sb.Length > 0 && char.IsUpper(c) && i > 0 && char.IsLower(s[i - 1]);
            if (boundary) { yield return sb.ToString(); sb.Clear(); }
            sb.Append(char.ToLowerInvariant(c));
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
