namespace MdClip.Tests;

// A throwaway folder tree standing in for a docs repo plus one folder outside it:
//   root\docs\setup.md            root\guide.md          root\a\notes.md, root\b\notes.md (same name twice)
//   root\project\docs\readme.md root\pics\dot.png      root\My Docs\a b.md
//   outside\archive.md    outside\notes.md      outside\setup.md
public sealed class Tree : IDisposable
{
    public string Root { get; }
    public string Outside { get; }

    public Tree()
    {
        var top = Path.Combine(Path.GetTempPath(), "mdclip-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Root = Path.Combine(top, "root");
        Outside = Path.Combine(top, "outside");
        foreach (var file in new[] { @"docs\setup.md", "guide.md", @"a\notes.md", @"b\notes.md", @"project\docs\readme.md", @"pics\dot.png", @"My Docs\a b.md" })
            Write(file);
        foreach (var file in new[] { "archive.md", "notes.md", "setup.md" })
            WriteAt(Path.Combine(Outside, file), "# x");
    }

    // A file under the root (its folders made as needed).
    public void Write(string rel, string text = "# x") => WriteAt(In(rel), text);

    // A file in the folder outside the root; returns its full path.
    public string WriteOutside(string rel, string text = "# x")
    {
        var path = Path.Combine(Outside, rel);
        WriteAt(path, text);
        return path;
    }

    static void WriteAt(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public string In(string rel) => Path.Combine(Root, rel);

    // The relative paths the app's tree scan would list for the root.
    public List<string> Known() => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
        .Select(f => Paths.ToRel(Root, f)).ToList();

    // The clipboard rules over this tree, with these folders remembered.
    public PathFinder Finder(params string[] recentDirs) => new(Root, Known(), recentDirs);

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(Root)!, recursive: true);
}
