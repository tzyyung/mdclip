using System.Text.RegularExpressions;

namespace MdViewer;

// Which files MdViewer shows, what type each file is served as, and how a file's path relates to the open
// folder (the root). The one place for all three: the tree, the watcher, the clipboard rules, the server and
// the web page (sent to it) use these.
public static class Paths
{
    // The pictures MdViewer shows, with the type each is served as (declared first: the lists below use it).
    static readonly (string Extension, string Type)[] ImageTypes =
    [
        (".png", "image/png"), (".jpg", "image/jpeg"), (".jpeg", "image/jpeg"), (".gif", "image/gif"),
        (".svg", "image/svg+xml"), (".webp", "image/webp"), (".bmp", "image/bmp"),
    ];

    public static readonly string[] MarkdownExtensions = [".md", ".markdown"];
    public static readonly string[] ImageExtensions = ImageTypes.Select(t => t.Extension).ToArray();

    static readonly HashSet<string> Markdown = new(MarkdownExtensions, StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> Images = new(ImageExtensions, StringComparer.OrdinalIgnoreCase);

    public static bool IsMarkdown(string path) => Markdown.Contains(Path.GetExtension(path));

    public static bool IsImage(string path) => Images.Contains(Path.GetExtension(path));

    // What the tree lists and the watcher cares about.
    public static bool IsListed(string path) => IsMarkdown(path) || IsImage(path);

    // A regex alternation of extensions without their dots, e.g. "md|markdown".
    public static string Alternation(IEnumerable<string> extensions) => string.Join("|", extensions.Select(e => Regex.Escape(e[1..])));

    public const string HtmlType = "text/html; charset=utf-8";

    // The one table of content types: pictures, and the other files the server hands out as they are.
    static readonly Dictionary<string, string> ContentTypes = new(
        ImageTypes.Select(t => KeyValuePair.Create(t.Extension, t.Type)).Concat(
        [
            KeyValuePair.Create(".ico", "image/x-icon"), KeyValuePair.Create(".pdf", "application/pdf"),
            KeyValuePair.Create(".html", HtmlType), KeyValuePair.Create(".htm", HtmlType),
            KeyValuePair.Create(".js", "text/javascript; charset=utf-8"), KeyValuePair.Create(".css", "text/css; charset=utf-8"),
            KeyValuePair.Create(".woff2", "font/woff2"),
        ]),
        StringComparer.OrdinalIgnoreCase);

    public static string? ContentType(string path) => ContentTypes.GetValueOrDefault(Path.GetExtension(path));

    public static bool IsUnder(string path, string dir) =>
        path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    // A folder as it is stored and compared: full path, no trailing separator.
    public static string NormalizeDir(string dir) => Path.GetFullPath(dir).TrimEnd('\\');

    // "docs/a.md" for E:\repo\docs\a.md under E:\repo: the form the web page and the tree use.
    public static string ToRel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    // The full path of a root-relative path (either separator; a leading one is ignored).
    public static string FromRel(string root, string rel) => Path.GetFullPath(Path.Combine(root, rel.Replace('/', '\\').TrimStart('\\')));
}
