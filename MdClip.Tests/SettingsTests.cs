namespace MdClip.Tests;

// Only the in-memory rules: Load and Save use the user's real settings file.
public sealed class SettingsTests
{
    static (Settings Settings, Func<int> Changes) Fresh()
    {
        var s = new Settings();
        var n = 0;
        s.Changed += () => n++;
        return (s, () => n);
    }

    [Fact]
    public void Clipboard_options_keep_only_valid_values()
    {
        var (s, _) = Fresh();
        Assert.False(s.SetClip("sometimes"));
        Assert.True(s.SetClip("hint"));
        Assert.Equal("hint", s.Clip);
        Assert.False(s.SetClipChars(-1));
        Assert.True(s.SetClipChars(0));
        Assert.False(s.SetClipMax(Settings.MinClipMax - 1));
        Assert.False(s.SetClipMax(Settings.MaxClipMax + 1));
        Assert.True(s.SetClipMax(Settings.MaxClipMax));
        Assert.False(s.SetClipMulti("all"));
        Assert.True(s.SetClipMulti("first"));
        Assert.Equal((0, Settings.MaxClipMax, "first"), (s.ClipChars, s.ClipMax, s.ClipMulti));
    }

    [Fact]
    public void Every_change_is_announced_and_a_same_value_is_not()
    {
        var (s, changes) = Fresh();
        s.Root = @"E:\docs";
        s.Root = @"E:\docs";
        s.RememberDir(@"E:\docs");
        s.RememberDir(@"E:\docs\"); // the same folder, already first
        Assert.Equal(2, changes());
    }

    [Fact]
    public void Folders_are_kept_once_most_recent_first()
    {
        var (s, _) = Fresh();
        s.RememberDir(@"E:\a");
        s.RememberDir(@"E:\b");
        s.RememberDir(@"e:\A\");
        Assert.Equal([@"e:\A", @"E:\b"], s.Dirs); // stored once, in its latest spelling
        s.ForgetDirs(new HashSet<string>([@"E:\b"], StringComparer.OrdinalIgnoreCase));
        Assert.Equal([@"e:\A"], s.Dirs);
    }

    [Fact]
    public void Reading_history_keeps_the_position_when_a_file_is_read_again()
    {
        var (s, _) = Fresh();
        s.RememberFile(@"E:\a.md");
        s.SetScroll(@"E:\A.md", 420);
        s.RememberFile(@"E:\b.md");
        s.RememberFile(@"E:\a.md");
        Assert.Equal([@"E:\a.md", @"E:\b.md"], s.History.Select(h => h.Path));
        Assert.Equal(420, s.History[0].Scroll);
    }

    [Fact]
    public void Reading_history_holds_the_latest_files()
    {
        var (s, _) = Fresh();
        for (int i = 0; i < Settings.MaxHistory + 5; i++) s.RememberFile($@"E:\f{i}.md");
        Assert.Equal(Settings.MaxHistory, s.History.Count);
        Assert.Equal($@"E:\f{Settings.MaxHistory + 4}.md", s.History[0].Path);
    }

    [Theory]
    [InlineData("2026-10-23T10:00:00|120|E:\\a.md", "E:\\a.md", 120)]
    [InlineData("2026-10-23T10:00:00|E:\\old.md", "E:\\old.md", 0)]
    public void History_lines_load(string line, string path, int scroll)
    {
        var h = HistoryEntry.Parse(line)!;
        Assert.Equal((path, scroll), (h.Path, h.Scroll));
        Assert.Equal(h, HistoryEntry.Parse(h.ToString()));
    }
}

public sealed class PathsTests
{
    [Theory]
    [InlineData("a.md", true, false)]
    [InlineData("A.MARKDOWN", true, false)]
    [InlineData("pic.JPEG", false, true)]
    [InlineData("notes.txt", false, false)]
    [InlineData("folder", false, false)]
    public void File_kinds(string path, bool markdown, bool image)
    {
        Assert.Equal(markdown, Paths.IsMarkdown(path));
        Assert.Equal(image, Paths.IsImage(path));
        Assert.Equal(markdown || image, Paths.IsListed(path));
    }

    [Fact]
    public void Relative_and_full_paths_round_trip()
    {
        Assert.Equal("docs/a b.md", Paths.ToRel(@"E:\repo", @"E:\repo\docs\a b.md"));
        Assert.Equal(@"E:\repo\docs\a b.md", Paths.FromRel(@"E:\repo", "docs/a b.md"));
        Assert.True(Paths.IsUnder(@"E:\repo\docs\a.md", @"E:\repo\"));
        Assert.False(Paths.IsUnder(@"E:\repo2\a.md", @"E:\repo"));
    }

    [Fact]
    public void Extension_alternation_for_patterns() => Assert.Equal("md|markdown", Paths.Alternation(Paths.MarkdownExtensions));
}

public sealed class FormerNameTests : IDisposable
{
    readonly string top = Path.Combine(Path.GetTempPath(), "mdclip-rename-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public void The_former_data_folder_becomes_the_new_one()
    {
        var former = Path.Combine(top, "MdViewer");
        var current = Path.Combine(top, "MdClip");
        Directory.CreateDirectory(Path.Combine(former, "WebView2"));
        File.WriteAllText(Path.Combine(former, "theme.txt"), "theme=nord");
        AppInfo.TakeOverFormerData(former, current);
        Assert.False(Directory.Exists(former));
        Assert.Equal("theme=nord", File.ReadAllText(Path.Combine(current, "theme.txt")));
        Assert.True(Directory.Exists(Path.Combine(current, "WebView2")));
    }

    [Fact]
    public void An_existing_data_folder_is_left_alone()
    {
        var former = Path.Combine(top, "MdViewer");
        var current = Path.Combine(top, "MdClip");
        Directory.CreateDirectory(former);
        File.WriteAllText(Path.Combine(former, "theme.txt"), "theme=old");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "theme.txt"), "theme=new");
        AppInfo.TakeOverFormerData(former, current);
        Assert.Equal("theme=new", File.ReadAllText(Path.Combine(current, "theme.txt")));
        Assert.True(Directory.Exists(former));
    }

    public void Dispose() { if (Directory.Exists(top)) Directory.Delete(top, recursive: true); }
}
