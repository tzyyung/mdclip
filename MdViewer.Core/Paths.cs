using System.Text.RegularExpressions;

namespace MdViewer;

// Which files MdViewer shows, and how a file's path relates to the open folder (the root). The one place
// for both: the tree, the watcher, the clipboard rules, the server and the web page (sent to it) use these.
public static class Paths
{
    public static readonly string[] MarkdownExtensions = [".md", ".markdown"];
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".bmp"];

    static readonly HashSet<string> Markdown = new(MarkdownExtensions, StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> Images = new(ImageExtensions, StringComparer.OrdinalIgnoreCase);

    public static bool IsMarkdown(string path) => Markdown.Contains(Path.GetExtension(path));

    public static bool IsImage(string path) => Images.Contains(Path.GetExtension(path));

    // What the tree lists and the watcher cares about.
    public static bool IsListed(string path) => IsMarkdown(path) || IsImage(path);

    // A regex alternation of extensions without their dots, e.g. "md|markdown".
    public static string Alternation(IEnumerable<string> extensions) => string.Join("|", extensions.Select(e => Regex.Escape(e[1..])));

    public static bool IsUnder(string path, string dir) =>
        path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    // "docs/a.md" for E:\repo\docs\a.md under E:\repo: the form the web page and the tree use.
    public static string ToRel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    public static string FromRel(string root, string rel) => Path.GetFullPath(Path.Combine(root, rel.Replace('/', '\\')));
}
