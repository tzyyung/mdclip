using System.Diagnostics;

namespace MdViewer.Tests;

// Clipboard text is looked at on the window's thread, so even the worst text must not hold it up:
// 20,000 characters full of file names that exist nowhere, against 200 remembered folders.
// (This took 2.4 s before bare names were looked up in one listing per folder.)
public sealed class SpeedTests(Tree tree) : IClassFixture<Tree>
{
    [Fact]
    public void Long_text_full_of_unknown_names_is_quick()
    {
        var dirs = Enumerable.Range(0, 200).Select(i => Path.Combine(tree.Outside, "d" + i)).ToList();
        var words = string.Join(" ", Enumerable.Range(0, 2000).Select(i => i % 7 == 0 ? $"notes{i}.md" : "字"));
        var text = (words + " ").PadRight(20_000, '字')[..20_000];
        var finder = new PathFinder(tree.Root, tree.Known(), dirs);
        finder.Analyze("warm up", 30);
        var sw = Stopwatch.StartNew();
        finder.Analyze(text, 30);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
    }
}
