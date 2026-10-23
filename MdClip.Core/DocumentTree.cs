namespace MdClip;

// One entry of the side panel's tree: a folder (Children) or a file (Path, relative to the root).
public sealed record TreeItem(string Name, string? Path, List<TreeItem>? Children);

// A folder's tree as the side panel shows it: folders first, then Markdown and image files, leaving out
// hidden folders, build and package output, and folders with nothing to show.
public static class DocumentTree
{
    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", "bin", "obj", "target", "dist", "build", "__pycache__", "venv", "packages" };

    // Folders the tree leaves out, and whose changes are therefore ignored: hidden ones (.git, .vs, ...)
    // and build or package output.
    public static bool IsSkippedDir(string name) => name.StartsWith('.') || SkipDirs.Contains(name);

    // Whether a file change under the root can matter to the tree or an open document: a listed file, or
    // (when files come and go) something without an extension, i.e. a folder; never inside skipped folders,
    // where a git checkout or package install changes thousands of files.
    public static bool Concerns(string root, string path, bool structural) =>
        (Paths.IsListed(path) || (structural && !Path.HasExtension(path)))
        && !Paths.ToRel(root, path).Split('/').SkipLast(1).Any(IsSkippedDir);

    public static List<TreeItem> Build(string root) => Children(new DirectoryInfo(root), root) ?? [];

    // Every file in the tree, as root-relative paths.
    public static IEnumerable<string> Files(IEnumerable<TreeItem> items) =>
        items.SelectMany(it => it.Children is { } children ? Files(children) : [it.Path!]);

    static List<TreeItem>? Children(DirectoryInfo dir, string root)
    {
        List<DirectoryInfo> dirs;
        List<FileInfo> files;
        try
        {
            dirs = dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
            files = dir.EnumerateFiles().Where(f => Paths.IsListed(f.Name)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }

        var items = new List<TreeItem>();
        foreach (var d in dirs)
        {
            if (IsSkippedDir(d.Name) || d.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (Children(d, root) is { Count: > 0 } children) items.Add(new(d.Name, null, children));
        }
        foreach (var f in files)
            items.Add(new(f.Name, Paths.ToRel(root, f.FullName), null));
        return items;
    }
}
