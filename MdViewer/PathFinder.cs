using System.Text.RegularExpressions;

namespace MdViewer;

// Works out which markdown or image file a pasted or copied piece of text refers to: an absolute path,
// a path relative to the root, a file:// URL, a path with spaces inside a sentence, or just a file name /
// path tail matched against the files under the root and the folders opened before. Free of UI and
// settings so the rules can be tested on their own (MdViewer.Tests).
sealed class PathFinder(string root, IReadOnlyList<string> knownFiles, IReadOnlyList<string> recentDirs)
{
    // Path-looking tokens ending in a listed extension, e.g. E:\docs\b.md, docs/x.png, ./y.md:12.
    // The extension ends at anything but an ASCII word character, not at \b: CJK counts as a word
    // character, so "setup.md這份" would not end there.
    static readonly Regex PathToken = new(
        @"(?:[A-Za-z]:[\\/]|\\\\)?[^\s""'<>|*?()\[\]`]*?\.(?:md|markdown|png|jpe?g|gif|svg|webp|bmp)(?![A-Za-z0-9_])",
        RegexOptions.IgnoreCase);
    // A listed extension at the end of a word; paths with spaces are found by trying starts to its left.
    static readonly Regex ExtensionEnd = new(@"\.(?:md|markdown|png|jpe?g|gif|svg|webp|bmp)(?![A-Za-z0-9_])", RegexOptions.IgnoreCase);
    // "E:\docs\a.md#some-heading", anywhere in the text: a file plus the heading to jump to (what the
    // heading link button copies). The heading ends at whitespace, quotes, brackets or CJK punctuation.
    static readonly Regex WithFragment = new(@"\.(?:md|markdown)(?<frag>#[^\s#""'<>()\[\]。，、；：！？」』）]+)", RegexOptions.IgnoreCase);

    // Absolute folders seen by the last Find (folders themselves, or the folders of files), for the
    // caller to remember so a later copy of just a file name can be matched against them.
    public List<string> SeenDirs { get; } = [];

    public static bool IsMarkdown(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown";

    public static bool IsImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".webp" or ".bmp";

    // What the tree lists and the watcher cares about.
    public static bool IsListed(string path) => IsMarkdown(path) || IsImage(path);

    // The text without the "#heading" part (so the path can be found as usual), and that part.
    public static (string Text, string? Hash) SplitFragment(string text) =>
        WithFragment.Match(text) is { Success: true } m
            ? (text.Remove(m.Groups["frag"].Index, m.Groups["frag"].Length), m.Groups["frag"].Value)
            : (text, null);

    // Accepts a pasted absolute path (file or folder, quoted or not, / or \), a file:// URL,
    // or a file path relative to the root.
    public string? Resolve(string text)
    {
        var s = text.Trim().Trim('"', '\'');
        if (s.Length == 0) return null;
        if (s.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(s, UriKind.Absolute, out var uri)) s = uri.LocalPath;
        s = s.Replace('/', '\\');
        try
        {
            if (Path.IsPathFullyQualified(s))
                return File.Exists(s) || Directory.Exists(s) ? Path.GetFullPath(s) : null;
            if (Path.GetFullPath(Path.Combine(root, s.TrimStart('\\'))) is var full && File.Exists(full))
                return full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        return null;
    }

    // The file the text names (Path), or failing that the root-relative files whose name or path tail
    // it matches (Matches: one means open it, several means let the user pick).
    public (string? Path, List<string> Matches) Find(string text) => FindWithSource(text) is var f ? (f.Path, f.Matches) : default;

    // Find, plus the stretch of text that named the file (for telling a copied path from a passage).
    (string? Path, List<string> Matches, string? Source) FindWithSource(string text)
    {
        var candidates = new[] { text }
            .Concat(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Concat(SpacedCandidates(text))
            .Concat(PathToken.Matches(text).SelectMany(m => TokenStarts(m.Value).Prepend(m.Value)))
            .ToList();
        CollectDirs(candidates);
        foreach (var candidate in candidates)
            if (Resolve(candidate) is { } path && IsListed(path) && File.Exists(path)) return (path, [], candidate);

        // Not a real path: treat it as a file name or path tail. A single match under the root wins;
        // otherwise look next to recently opened files (also outside the root), most recent first;
        // otherwise report every match under the root so the user can pick.
        foreach (var candidate in candidates)
        {
            var tail = Tail(candidate);
            if (tail.Length == 0 || tail.Any(char.IsWhiteSpace) || !IsListed(tail)) continue;
            var hits = NameMatches(tail);
            if (hits.Count == 1) return (null, hits, candidate);
            if (FindInRecentDirs(tail) is { } recent) return (recent, [], candidate);
            if (hits.Count > 1) return (null, hits, candidate);
        }
        return (null, [], null);
    }

    // One file named in a text, and the stretch of text that named it.
    public sealed record Hit(string Path, string Source);

    const int MaxFiles = 20, MaxExtensions = 200;

    // Every file the text names, in the order they appear, each once (at most MaxFiles): for each listed
    // extension, the longest stretch ending there that is a real path, else a file name / path tail with
    // one match under the root or a match in a remembered folder. Names with several matches are left out.
    public List<Hit> FindAll(string text)
    {
        var found = new List<Hit>();
        int seen = 0;
        foreach (var line in text.Split('\n'))
        {
            foreach (Match ext in ExtensionEnd.Matches(line))
            {
                if (++seen > MaxExtensions || found.Count == MaxFiles) return found;
                if (FileEndingAt(line, ext.Index + ext.Length) is { } hit && !found.Any(h => h.Path.Equals(hit.Path, StringComparison.OrdinalIgnoreCase)))
                    found.Add(hit);
            }
        }
        return found;
    }

    Hit? FileEndingAt(string line, int end)
    {
        var stretches = Starts(line, end).Select(start => line[start..end]).ToList();
        foreach (var stretch in stretches)
            if (Resolve(stretch) is { } path && IsListed(path) && File.Exists(path)) return new(path, stretch);
        foreach (var stretch in stretches.Where(s => !s.Any(char.IsWhiteSpace)))
        {
            var tail = Tail(stretch);
            if (tail.Length == 0) continue;
            if (NameMatches(tail) is [var only]) return new(Path.GetFullPath(Path.Combine(root, only.Replace('/', '\\'))), stretch);
            if (FindInRecentDirs(tail) is { } recent) return new(recent, stretch);
        }
        return null;
    }

    // What the clipboard text offers: one file (Path), a name with several matches (Matches), or several
    // different files (Files, in text order); and whether it looks copied on purpose (Deliberate): at most
    // maxExtra characters besides the paths, so "請看 docs/setup.md 這份" opens but a paragraph that happens
    // to mention a file only offers it.
    public sealed record Offer(string? Path, List<string> Matches, List<string> Files, bool Deliberate);

    public Offer Analyze(string text, int maxExtra)
    {
        var (path, matches, source) = FindWithSource(text);
        var all = FindAll(text);
        if (all.Count > 1)
            return new(null, [], all.Select(h => h.Path).ToList(), ExtraChars(text, all.Select(h => h.Source)) <= maxExtra);
        return new(path, matches, [], ExtraChars(text, source is null ? [] : [source]) <= maxExtra);
    }

    // The characters of the text outside the given stretches, not counting spaces, quotes and brackets.
    public static int ExtraChars(string text, IEnumerable<string> stretches)
    {
        foreach (var stretch in stretches.OrderByDescending(s => s.Length))
            if (text.IndexOf(stretch, StringComparison.Ordinal) is >= 0 and var at) text = text.Remove(at, stretch.Length);
        return text.Count(c => !char.IsWhiteSpace(c) && !"\"'`「」『』()（）<>[]".Contains(c));
    }

    // For each listed extension inside a sentence, the stretches that end there and contain a space,
    // longest first: "see C:\My Docs\a.md now" yields "see C:\My Docs\a.md", "C:\My Docs\a.md". Only
    // ones that exist are used.
    public static IEnumerable<string> SpacedCandidates(string text)
    {
        foreach (var line in text.Split('\n'))
            foreach (Match ext in ExtensionEnd.Matches(line))
                foreach (var start in Starts(line, ext.Index + ext.Length))
                    if (line[start..(ext.Index + ext.Length)] is var candidate && candidate.Any(char.IsWhiteSpace))
                        yield return candidate;
    }

    // Where a path ending at `end` may start, farthest first (the nearest 12): a drive letter, or just
    // after whitespace, a quote, an opening bracket or full-width punctuation.
    static IEnumerable<int> Starts(string line, int end)
    {
        var starts = new List<int>();
        for (int i = 0; i < end; i++)
        {
            if (char.IsWhiteSpace(line[i])) continue;
            bool afterBreak = i == 0 || char.IsWhiteSpace(line[i - 1]) || line[i - 1] is '"' or '\'' or '`' or '(' || IsFullWidthMark(line[i - 1]);
            bool drive = i + 2 < line.Length && char.IsAsciiLetter(line[i]) && line[i + 1] == ':' && line[i + 2] is '\\' or '/';
            if (afterBreak || drive) starts.Add(i);
        }
        return starts.TakeLast(12);
    }

    static bool IsFullWidthMark(char c) => c is >= '\u3000' and <= '\u303F' or >= '\uFF00' and <= '\uFFEF';

    static string Tail(string candidate) => candidate.Trim().Trim('"', '\'', '`').Replace('\\', '/').TrimStart('.', '/');

    List<string> NameMatches(string tail) => knownFiles.Where(rel => rel.Equals(tail, StringComparison.OrdinalIgnoreCase)
        || rel.EndsWith("/" + tail, StringComparison.OrdinalIgnoreCase)).ToList();

    // A token can have words glued to its front in Chinese text ("路徑：E:\docs\a.md", "請看E:\docs\a.md",
    // "「docs/a.md」"): also try it from each drive letter and from after each full-width punctuation mark.
    static IEnumerable<string> TokenStarts(string token)
    {
        for (int i = 1; i < token.Length; i++)
        {
            bool afterMark = IsFullWidthMark(token[i - 1]);
            bool drive = i + 2 < token.Length && char.IsAsciiLetter(token[i]) && token[i + 1] == ':' && token[i + 2] is '\\' or '/';
            if (afterMark || drive) yield return token[i..];
        }
    }

    void CollectDirs(IEnumerable<string> candidates)
    {
        SeenDirs.Clear();
        foreach (var candidate in candidates)
        {
            if (Resolve(candidate) is not { } path || !Path.IsPathFullyQualified(candidate.Trim().Trim('"', '\'', '`'))) continue;
            var dir = Directory.Exists(path) ? path : IsListed(path) ? Path.GetDirectoryName(path)! : null;
            if (dir is not null && !SeenDirs.Contains(dir, StringComparer.OrdinalIgnoreCase)) SeenDirs.Add(dir);
        }
    }

    // Folders named in this same text come first, then the remembered ones.
    string? FindInRecentDirs(string tail)
    {
        var rel = tail.Replace('/', '\\');
        foreach (var dir in SeenDirs.Concat(recentDirs))
        {
            try
            {
                var path = Path.GetFullPath(Path.Combine(dir, rel));
                if (File.Exists(path)) return path;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        }
        return null;
    }
}
