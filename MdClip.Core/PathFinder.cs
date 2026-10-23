using System.Text.RegularExpressions;

namespace MdClip;

// Works out which markdown or image file a pasted or copied piece of text refers to: an absolute path,
// a path relative to the root, a file:// URL, a path with spaces inside a sentence, or just a file name /
// path tail matched against the files under the root and the folders opened before. Free of UI and
// settings so the rules can be tested on their own (MdClip.Tests).
public sealed class PathFinder(string root, IReadOnlyList<string> knownFiles, IReadOnlyList<string> recentDirs)
{
    // Path-looking tokens ending in a listed extension, e.g. E:\docs\b.md, docs/x.png, ./y.md:12.
    // The extension ends at anything but an ASCII word character, not at \b: CJK counts as a word
    // character, so "setup.md這份" would not end there. At most 260 characters (the usual Windows path
    // limit): unbounded, a long run of text without spaces would be scanned again from every position.
    static readonly string Listed = Paths.Alternation(Paths.MarkdownExtensions.Concat(Paths.ImageExtensions));
    static readonly Regex PathToken = new(
        @"(?:[A-Za-z]:[\\/]|\\\\)?[^\s""'<>|*?()\[\]`]{0,260}?\.(?:" + Listed + ")(?![A-Za-z0-9_])",
        RegexOptions.IgnoreCase);
    // A listed extension at the end of a word; paths with spaces are found by trying starts to its left.
    static readonly Regex ExtensionEnd = new(@"\.(?:" + Listed + ")(?![A-Za-z0-9_])", RegexOptions.IgnoreCase);
    // "E:\docs\a.md#some-heading", anywhere in the text: a file plus the heading to jump to (what the
    // heading link button copies). The heading ends at whitespace, quotes, brackets or CJK punctuation.
    static readonly Regex WithFragment = new(@"\.(?:" + Paths.Alternation(Paths.MarkdownExtensions) + @")(?<frag>#[^\s#""'<>()\[\]。，、；：！？」』）]+)", RegexOptions.IgnoreCase);

    // Absolute folders seen by the last Find (folders themselves, or the folders of files), for the
    // caller to remember so a later copy of just a file name can be matched against them.
    public List<string> SeenDirs { get; } = [];

    // The text without the "#heading" part (so the path can be found as usual), and that part.
    public static (string Text, string? Hash) SplitFragment(string text) =>
        WithFragment.Match(text) is { Success: true } m
            ? (text.Remove(m.Groups["frag"].Index, m.Groups["frag"].Length), m.Groups["frag"].Value)
            : (text, null);

    // Accepts a pasted absolute path (file or folder, quoted or not, / or \), a file:// URL,
    // or a file path relative to the root.
    public string? Resolve(string text) => Locate(text)?.Path;

    // What a piece of text points at on disk. Each text is checked once per PathFinder: Find, FindAll and
    // the folder collection try many of the same stretches.
    readonly record struct Entry(string Path, bool IsDirectory);
    readonly Dictionary<string, Entry?> located = [];

    Entry? Locate(string text) => located.TryGetValue(text, out var known) ? known : located[text] = LocateOnDisk(text);

    Entry? LocateOnDisk(string text)
    {
        var s = Unquote(text);
        if (s.Length == 0) return null;
        if (s.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(s, UriKind.Absolute, out var uri)) s = uri.LocalPath;
        s = s.Replace('/', '\\');
        try
        {
            if (!Path.IsPathFullyQualified(s))
                return Paths.FromRel(root, s) is var full && SafePath.FileExists(full) ? new(full, false) : null;
            if (!SafePath.Reachable(s)) return null;
            s = Path.GetFullPath(s);
            return File.Exists(s) ? new(s, false) : Directory.Exists(s) ? new(s, true) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        return null;
    }

    // A markdown or image file the text names.
    string? LocateFile(string text) => Locate(text) is { IsDirectory: false } e && Paths.IsListed(e.Path) ? e.Path : null;

    static string Unquote(string text) => text.Trim().Trim('"', '\'', '`');

    // The file the text names (Path), or failing that the root-relative files whose name or path tail
    // it matches (Matches: one means open it, several means let the user pick).
    public (string? Path, List<string> Matches) Find(string text) => FindWithSource(Prepare(text)) is var f ? (f.Path, f.Matches) : default;

    // A text prepared once for every lookup below: cut after its first MaxNames file names (a pasted log full
    // of names stays quick), split into lines, with where each listed extension ends in each line.
    sealed record Scan(string Text, string[] Lines, int[][] Ends);

    const int MaxNames = 100;

    static Scan Prepare(string text)
    {
        var lines = new List<string>();
        var ends = new List<int[]>();
        int names = 0;
        foreach (var line in text.Split('\n'))
        {
            var lineEnds = ExtensionEnd.Matches(line).Take(MaxNames - names).Select(m => m.Index + m.Length).ToArray();
            names += lineEnds.Length;
            var full = names == MaxNames;
            lines.Add(full ? line[..lineEnds[^1]] : line);
            ends.Add(lineEnds);
            if (full) break;
        }
        return new(string.Join('\n', lines), lines.ToArray(), ends.ToArray());
    }

    // Find, plus the stretch of text that named the file (for telling a copied path from a passage).
    (string? Path, List<string> Matches, string? Source) FindWithSource(Scan scan)
    {
        var candidates = new[] { scan.Text }
            .Concat(scan.Lines.Select(l => l.Trim()).Where(l => l.Length > 0))
            .Concat(SpacedCandidates(scan))
            .Concat(PathToken.Matches(scan.Text).SelectMany(m => TokenStarts(m.Value).Prepend(m.Value)))
            .ToList();
        CollectDirs(candidates);
        foreach (var candidate in candidates)
            if (LocateFile(candidate) is { } path) return (path, [], candidate);

        // Not a real path: treat it as a file name or path tail. A single match under the root wins;
        // otherwise look next to recently opened files (also outside the root), most recent first;
        // otherwise report every match under the root so the user can pick.
        foreach (var candidate in candidates)
        {
            var tail = Tail(candidate);
            if (tail.Length == 0 || tail.Any(char.IsWhiteSpace) || !Paths.IsListed(tail)) continue;
            var hits = NameMatches(tail);
            if (hits.Count == 1) return (null, hits, candidate);
            if (FindInRecentDirs(tail) is { } recent) return (recent, [], candidate);
            if (hits.Count > 1) return (null, hits, candidate);
        }
        return (null, [], null);
    }

    // One file named in a text, and the stretch of text that named it.
    public sealed record Hit(string Path, string Source);

    // Every file the text names, in the order they appear, each once, stopping at max: for each listed
    // extension, the longest stretch ending there that is a real path, else a file name / path tail with
    // one match under the root or a match in a remembered folder. Names with several matches are left out.
    public List<Hit> FindAll(string text, int max = 20) => FindAll(Prepare(text), max);

    List<Hit> FindAll(Scan scan, int max)
    {
        var found = new List<Hit>();
        for (int i = 0; i < scan.Lines.Length; i++)
        {
            foreach (var end in scan.Ends[i])
            {
                if (found.Count == max) return found;
                if (FileEndingAt(scan.Lines[i], end) is { } hit && !found.Any(h => h.Path.Equals(hit.Path, StringComparison.OrdinalIgnoreCase)))
                    found.Add(hit);
            }
        }
        return found;
    }

    Hit? FileEndingAt(string line, int end)
    {
        var stretches = Starts(line, end).Select(start => line[start..end]).ToList();
        foreach (var stretch in stretches)
            if (LocateFile(stretch) is { } path) return new(path, stretch);
        foreach (var stretch in stretches.Where(s => !s.Any(char.IsWhiteSpace)))
        {
            var tail = Tail(stretch);
            if (tail.Length == 0) continue;
            if (NameMatches(tail) is [var only]) return new(Paths.FromRel(root, only), stretch);
            if (FindInRecentDirs(tail) is { } recent) return new(recent, stretch);
        }
        return null;
    }

    // What the clipboard text offers: one file (Path), a name with several matches (Matches), or several
    // different files (Files, in text order, at most listLimit; More when the text names even more); and
    // whether it looks copied on purpose (Deliberate): at most maxExtra characters besides the paths, so
    // "請看 docs/setup.md 這份" opens but a paragraph that happens to mention a file only offers it.
    // listLimit 1 just finds the first file and whether there are others, without looking for them all.
    public sealed record Offer(string? Path, List<string> Matches, List<string> Files, bool More, bool Deliberate)
    {
        // For files known to exist (copied in Explorer, or found in a text): one is the Path; more are the
        // Files, at most listLimit of them, More when there were more.
        public static Offer ForFiles(IReadOnlyList<string> files, int listLimit, bool deliberate = true) =>
            files.Count == 1 ? new(files[0], [], [], false, deliberate)
            : new(null, [], files.Take(listLimit).ToList(), files.Count > listLimit, deliberate);
    }

    public Offer Analyze(string text, int maxExtra, int listLimit = 5)
    {
        var scan = Prepare(text);
        var (path, matches, source) = FindWithSource(scan);
        var all = FindAll(scan, listLimit + 1);
        if (all.Count > 1)
            return Offer.ForFiles(all.Select(h => h.Path).ToList(), listLimit, ExtraChars(text, all.Select(h => h.Source)) <= maxExtra);
        return new(path, matches, [], false, ExtraChars(text, source is null ? [] : [source]) <= maxExtra);
    }

    // The characters of the text outside the given stretches and outside anything else that looks like a
    // path (found or not: a copied list of paths is still a list of paths), not counting spaces, quotes
    // and brackets.
    public static int ExtraChars(string text, IEnumerable<string> stretches)
    {
        foreach (var stretch in stretches.OrderByDescending(s => s.Length))
            if (text.IndexOf(stretch, StringComparison.Ordinal) is >= 0 and var at) text = text.Remove(at, stretch.Length);
        text = PathToken.Replace(text, "");
        return text.Count(c => !char.IsWhiteSpace(c) && !"\"'`「」『』()（）<>[]".Contains(c));
    }

    // For each listed extension inside a sentence, the stretches that end there and contain a space,
    // longest first: "see C:\My Docs\a.md now" yields "see C:\My Docs\a.md", "C:\My Docs\a.md". Only
    // ones that exist are used.
    public static IEnumerable<string> SpacedCandidates(string text) => SpacedCandidates(Prepare(text));

    static IEnumerable<string> SpacedCandidates(Scan scan)
    {
        for (int i = 0; i < scan.Lines.Length; i++)
            foreach (var end in scan.Ends[i])
                foreach (var start in Starts(scan.Lines[i], end))
                    if (scan.Lines[i][start..end] is var candidate && candidate.Any(char.IsWhiteSpace))
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
            if (afterBreak || IsDriveAt(line, i)) starts.Add(i);
        }
        return starts.TakeLast(12);
    }

    static bool IsFullWidthMark(char c) => c is >= '\u3000' and <= '\u303F' or >= '\uFF00' and <= '\uFFEF';

    // "E:\" or "E:/" starts at i.
    static bool IsDriveAt(string s, int i) => i + 2 < s.Length && char.IsAsciiLetter(s[i]) && s[i + 1] == ':' && s[i + 2] is '\\' or '/';

    static string Tail(string candidate) => Unquote(candidate).Replace('\\', '/').TrimStart('.', '/');

    // The known files that a file name or path tail names. They are grouped by file name once, so each name
    // is looked up instead of being compared with every file.
    ILookup<string, string>? byName;

    List<string> NameMatches(string tail)
    {
        byName ??= knownFiles.ToLookup(rel => rel[(rel.LastIndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase);
        return byName[tail[(tail.LastIndexOf('/') + 1)..]]
            .Where(rel => rel.Equals(tail, StringComparison.OrdinalIgnoreCase) || rel.EndsWith("/" + tail, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // A token can have words glued to its front in Chinese text ("路徑：E:\docs\a.md", "請看E:\docs\a.md",
    // "「docs/a.md」"): also try it from each drive letter and from after each full-width punctuation mark.
    static IEnumerable<string> TokenStarts(string token)
    {
        for (int i = 1; i < token.Length; i++)
        {
            if (IsFullWidthMark(token[i - 1]) || IsDriveAt(token, i)) yield return token[i..];
        }
    }

    void CollectDirs(IEnumerable<string> candidates)
    {
        SeenDirs.Clear();
        recentNames = null;
        foreach (var candidate in candidates)
        {
            if (!Path.IsPathFullyQualified(Unquote(candidate)) || Locate(candidate) is not { } entry) continue;
            var dir = entry.IsDirectory ? entry.Path : Paths.IsListed(entry.Path) ? Path.GetDirectoryName(entry.Path)! : null;
            if (dir is not null && !SeenDirs.Contains(dir, StringComparer.OrdinalIgnoreCase)) SeenDirs.Add(dir);
        }
    }

    // Folders named in this same text come first, then the remembered ones; then the folders above them,
    // nearest first, since a link like "docs/shots/x.png" is written from its project's top folder while
    // only a folder inside it (its docs) may be remembered.
    string? FindInRecentDirs(string tail)
    {
        var rel = tail.Replace('/', '\\');
        if (!rel.Contains('\\')) return (recentNames ??= IndexRecentDirs()).GetValueOrDefault(rel);
        var dirs = SeenDirs.Concat(recentDirs).ToList();
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (dirs.Count > 0)
        {
            foreach (var dir in dirs)
            {
                if (!tried.Add(dir)) continue;
                try
                {
                    var path = Paths.FromRel(dir, rel);
                    if (SafePath.FileExists(path)) return path;
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
            }
            dirs = dirs.Select(Path.GetDirectoryName).OfType<string>().ToList();
        }
        return null;
    }

    // A bare file name is looked up in one listing of every remembered folder rather than by asking the
    // disk about each folder for each name: file name -> its path in the first (most recent) folder.
    Dictionary<string, string>? recentNames;

    Dictionary<string, string> IndexRecentDirs()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in SeenDirs.Concat(recentDirs).Where(SafePath.Reachable))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                    if (Paths.IsListed(file)) names.TryAdd(Path.GetFileName(file), file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return names;
    }
}
