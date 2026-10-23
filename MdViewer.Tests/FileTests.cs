using System.Diagnostics;
using System.Text;

namespace MdViewer.Tests;

public sealed class TextFilesTests
{
    const string Text = "# 排序規則\n重複項目只算一次。";

    [Fact]
    public void Utf8() => Assert.Equal(Text, TextFiles.Decode(Encoding.UTF8.GetBytes(Text)));

    [Fact]
    public void Utf8_with_bom() => Assert.Equal(Text, TextFiles.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Text)]));

    [Fact]
    public void Utf16_with_bom() => Assert.Equal(Text, TextFiles.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Text)]));

    [Fact]
    public void Big5_from_an_old_editor()
    {
        var big5 = TextFiles.CodePage(950);
        Assert.Equal(Text, TextFiles.Decode(big5.GetBytes(Text), big5));
    }

    [Fact]
    public void Plain_ascii_is_not_mistaken_for_big5() =>
        Assert.Equal("# Title\nplain", TextFiles.Decode(Encoding.ASCII.GetBytes("# Title\nplain"), TextFiles.CodePage(950)));
}

public sealed class SafePathTests(Tree tree) : IClassFixture<Tree>
{
    // A private address nothing answers on: a plain check of it waits about 25 s.
    const string DeadShare = @"\\10.255.255.1\share";

    [Fact]
    public void Unreachable_share_answers_quickly_and_then_from_memory()
    {
        var sw = Stopwatch.StartNew();
        Assert.False(SafePath.FileExists(DeadShare + @"\docs\a.md"));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"first check took {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        Assert.False(SafePath.DirectoryExists(DeadShare + @"\docs"));
        Assert.True(sw.ElapsedMilliseconds < 50, $"second check took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Remembered_folder_on_an_unreachable_share_does_not_hold_up_a_lookup()
    {
        var finder = tree.Finder(DeadShare + @"\docs", tree.Outside);
        var sw = Stopwatch.StartNew();
        Assert.Equal(Path.Combine(tree.Outside, "archive.md"), finder.Find("archive.md").Path);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Local_paths_are_checked_directly()
    {
        Assert.True(SafePath.FileExists(tree.In(@"docs\setup.md")));
        Assert.True(SafePath.DirectoryExists(tree.Root));
        Assert.False(SafePath.FileExists(tree.In("nope.md")));
        Assert.True(SafePath.Reachable(@"Q:\not\a\drive"));
    }
}
