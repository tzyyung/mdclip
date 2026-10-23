using System.Collections.Concurrent;

namespace MdClip;

// Case-insensitive search through the Markdown files of a folder. Each file's text is kept while its
// modification time is unchanged, up to about 64 MB of text, past which the cache starts over.
public sealed class DocumentSearch
{
    public const int MaxHits = 300;
    const long MaxCacheChars = 32_000_000;

    readonly ConcurrentDictionary<string, (DateTime Written, string[] Lines)> cache = new(StringComparer.OrdinalIgnoreCase);
    long cacheChars;

    // A hit with some text on each side, split so the page can mark the match.
    public sealed record Snippet(string Before, string Match, string After);

    // Nth: the occurrence number in the file as the page's find counts it, so a hit opens at that same match.
    public sealed record Hit(string Rel, int Line, int Nth, Snippet Snippet);

    // At most MaxHits hits; Total and Files count them all.
    public sealed record Result(List<Hit> Hits, int Total, int Files);

    public Result Run(string root, IEnumerable<string> files, string query)
    {
        var hits = new List<Hit>();
        int total = 0, fileCount = 0;
        if (query.Trim().Length == 0) return new(hits, 0, 0);
        foreach (var rel in files.Where(Paths.IsMarkdown))
        {
            string[] lines;
            try { lines = Lines(Paths.FromRel(root, rel)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            int nth = 0, fileHits = 0;
            bool inDiagram = false;
            for (int i = 0; i < lines.Length; i++)
            {
                // Text inside a diagram is drawn as SVG, which the page's find does not count;
                // such hits jump to the next match after the diagram.
                var fence = lines[i].TrimStart();
                if (fence.StartsWith("```mermaid") || fence.StartsWith("~~~mermaid")) inDiagram = true;
                else if (inDiagram && (fence.StartsWith("```") || fence.StartsWith("~~~"))) inDiagram = false;
                for (int at = lines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase); at >= 0;
                     at = lines[i].IndexOf(query, at + query.Length, StringComparison.OrdinalIgnoreCase))
                {
                    if (fileHits++ == 0) fileCount++;
                    if (total++ < MaxHits) hits.Add(new(rel, i + 1, nth, Cut(lines[i], at, query.Length)));
                    if (!inDiagram) nth++;
                }
            }
        }
        return new(hits, total, fileCount);
    }

    public void Clear()
    {
        cache.Clear();
        Interlocked.Exchange(ref cacheChars, 0);
    }

    string[] Lines(string path)
    {
        var written = File.GetLastWriteTimeUtc(path);
        if (cache.TryGetValue(path, out var cached) && cached.Written == written) return cached.Lines;
        var text = TextFiles.ReadText(path);
        if (Interlocked.Add(ref cacheChars, text.Length) > MaxCacheChars) Clear();
        return (cache[path] = (written, TextFiles.Lines(text))).Lines;
    }

    static Snippet Cut(string line, int at, int length)
    {
        int start = Math.Max(0, at - 30), end = Math.Min(line.Length, at + length + 90);
        return new((start > 0 ? "…" : "") + line[start..at].TrimStart(), line.Substring(at, length),
            line[(at + length)..end] + (end < line.Length ? "…" : ""));
    }
}
