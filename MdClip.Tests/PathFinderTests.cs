using MdClip;

namespace MdClip.Tests;

public sealed class PathFinderTests(Tree tree) : IClassFixture<Tree>
{
    PathFinder Finder(params string[] recentDirs) => tree.Finder(recentDirs);

    string? Found(string text, params string[] recentDirs) => Finder(recentDirs).Find(text).Path;

    List<string> Matches(string text, params string[] recentDirs) => Finder(recentDirs).Find(text).Matches;

    // ---------- whole paths ----------

    [Fact]
    public void Absolute_path() => Assert.Equal(tree.In(@"docs\setup.md"), Found(tree.In(@"docs\setup.md")));

    [Fact]
    public void Absolute_path_with_forward_slashes() =>
        Assert.Equal(tree.In(@"docs\setup.md"), Found(tree.In(@"docs\setup.md").Replace('\\', '/')));

    [Theory]
    [InlineData("\"{0}\"")]
    [InlineData("'{0}'")]
    [InlineData("`{0}`")]
    [InlineData("  {0}  \r\n")]
    public void Absolute_path_quoted_or_padded(string format) =>
        Assert.Equal(tree.In(@"docs\setup.md"), Found(string.Format(format, tree.In(@"docs\setup.md"))));

    [Fact]
    public void File_url() => Assert.Equal(tree.In(@"docs\setup.md"), Found(new Uri(tree.In(@"docs\setup.md")).AbsoluteUri));

    [Fact]
    public void Missing_absolute_path_finds_nothing()
    {
        var (path, matches) = Finder().Find(tree.In(@"docs\nope.md"));
        Assert.Null(path);
        Assert.Empty(matches);
    }

    [Theory]
    [InlineData("docs/setup.md")]
    [InlineData(@"docs\setup.md")]
    [InlineData("./docs/setup.md")]
    [InlineData("/docs/setup.md")]
    public void Relative_to_the_root(string text) => Assert.Equal(tree.In(@"docs\setup.md"), Found(text));

    [Fact]
    public void Image() => Assert.Equal(tree.In(@"pics\dot.png"), Found("pics/dot.png"));

    // ---------- paths inside other text ----------

    [Fact]
    public void Path_inside_a_sentence() =>
        Assert.Equal(tree.In(@"docs\setup.md"), Found($"請看 {tree.In(@"docs\setup.md")} 這份"));

    [Fact]
    public void Path_followed_by_cjk_punctuation() =>
        Assert.Equal(tree.In(@"docs\setup.md"), Found($"路徑：{tree.In(@"docs\setup.md")}。"));

    [Theory]
    [InlineData("請看{0}這份")]
    [InlineData("（{0}）")]
    [InlineData("「{0}」")]
    public void Path_glued_to_chinese_text(string format) =>
        Assert.Equal(tree.In(@"docs\setup.md"), Found(string.Format(format, tree.In(@"docs\setup.md"))));

    [Theory]
    [InlineData("路徑：docs/setup.md")]
    [InlineData("「docs/setup.md」")]
    public void Relative_path_after_chinese_punctuation(string text) => Assert.Equal(tree.In(@"docs\setup.md"), Found(text));

    [Fact]
    public void Chinese_file_name()
    {
        tree.Write("會議紀錄.md");
        Assert.Equal(tree.In("會議紀錄.md"), Found("會議紀錄.md"));
    }

    [Fact]
    public void Path_with_line_number() => Assert.Equal(tree.In(@"docs\setup.md"), Found("docs/setup.md:12"));

    [Fact]
    public void Path_on_a_later_line() =>
        Assert.Equal(tree.In(@"docs\setup.md"), Found("改了這幾份：\r\n  docs/setup.md\r\n  其他"));

    [Fact]
    public void Path_with_spaces_inside_a_sentence() =>
        Assert.Equal(tree.In(@"My Docs\a b.md"), Found($"請看 {tree.In(@"My Docs\a b.md")} 這份"));

    [Fact]
    public void Path_with_spaces_in_quotes() =>
        Assert.Equal(tree.In(@"My Docs\a b.md"), Found($"open \"{tree.In(@"My Docs\a b.md")}\" now"));

    [Fact]
    public void Path_with_spaces_after_cjk_colon() =>
        Assert.Equal(tree.In(@"My Docs\a b.md"), Found($"檔案：{tree.In(@"My Docs\a b.md")}"));

    // ---------- file names and path tails ----------

    [Theory]
    [InlineData("setup.md")]
    [InlineData("SETUP.MD")]
    [InlineData("`setup.md`")]
    [InlineData("看一下 setup.md 第三節")]
    [InlineData("setup.md這份")]
    public void Unique_file_name_under_the_root(string text) => Assert.Equal(["docs/setup.md"], Matches(text));

    [Fact]
    public void Path_tail_under_a_deeper_folder() => Assert.Equal(["project/docs/readme.md"], Matches("docs/readme.md"));

    [Fact]
    public void Same_name_twice_lists_both()
    {
        var matches = Matches("notes.md");
        Assert.Equal(2, matches.Count);
        Assert.Contains("b/notes.md", matches);
    }

    [Fact]
    public void Same_name_twice_prefers_a_remembered_folder() =>
        Assert.Equal(Path.Combine(tree.Outside, "notes.md"), Found("notes.md", tree.Outside));

    [Fact]
    public void Unique_name_under_the_root_beats_a_remembered_folder() =>
        Assert.Equal(["docs/setup.md"], Matches("setup.md", tree.Outside));

    [Fact]
    public void File_name_outside_the_root_found_in_a_remembered_folder() =>
        Assert.Equal(Path.Combine(tree.Outside, "archive.md"), Found("archive.md", tree.In("docs"), tree.Outside));

    [Fact]
    public void File_name_found_in_a_folder_named_in_the_same_text() =>
        Assert.Equal(Path.Combine(tree.Outside, "archive.md"), Found(tree.Outside + "\narchive.md"));

    [Fact]
    public void Unknown_file_name_finds_nothing()
    {
        var (path, matches) = Finder(tree.Outside).Find("nowhere.md");
        Assert.Null(path);
        Assert.Empty(matches);
    }

    // ---------- text that is not a file ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("今天開會討論 commit gate 的時間")]
    [InlineData("notes.txt")]
    [InlineData("see issue #12")]
    [InlineData("https://example.com/readme")]
    public void Ordinary_text_finds_nothing(string text)
    {
        var (path, matches) = Finder(tree.Outside).Find(text);
        Assert.Null(path);
        Assert.Empty(matches);
    }

    // ---------- folders to remember ----------

    [Fact]
    public void Remembers_the_folder_of_an_absolute_file()
    {
        var finder = Finder();
        finder.Find(tree.In(@"docs\setup.md"));
        Assert.Equal([tree.In("docs")], finder.SeenDirs);
    }

    [Fact]
    public void Remembers_an_absolute_folder()
    {
        var finder = Finder();
        finder.Find(tree.Outside);
        Assert.Equal([tree.Outside], finder.SeenDirs);
    }

    [Fact]
    public void Relative_paths_are_not_remembered()
    {
        var finder = Finder();
        finder.Find("docs/setup.md");
        Assert.Empty(finder.SeenDirs);
    }

    // ---------- quick open (Resolve) ----------

    [Fact]
    public void Resolve_accepts_a_folder() => Assert.Equal(tree.Outside, Finder().Resolve(tree.Outside));

    [Fact]
    public void Resolve_rejects_a_bare_name_not_at_the_root() => Assert.Null(Finder().Resolve("setup.md"));

    // ---------- headings (path#heading) ----------

    [Theory]
    [InlineData(@"E:\docs\guide.md#公式", @"E:\docs\guide.md", "#公式")]
    [InlineData(@"請看 E:\docs\guide.md#公式 這裡", @"請看 E:\docs\guide.md 這裡", "#公式")]
    [InlineData(@"E:\docs\guide.md#公式。", @"E:\docs\guide.md。", "#公式")]
    [InlineData(@"(docs/guide.md#step-2)", "(docs/guide.md)", "#step-2")]
    [InlineData(@"docs/README.MARKDOWN#intro", "docs/README.MARKDOWN", "#intro")]
    public void Split_heading(string text, string rest, string hash) =>
        Assert.Equal((rest, hash), PathFinder.SplitFragment(text));

    [Theory]
    [InlineData(@"E:\docs\guide.md")]
    [InlineData("see issue #12")]
    [InlineData(@"E:\pics\a.png#x")]
    public void No_heading(string text) => Assert.Equal((text, (string?)null), PathFinder.SplitFragment(text));

    [Fact]
    public void Heading_link_end_to_end()
    {
        var (rest, hash) = PathFinder.SplitFragment($"請看 {tree.In(@"docs\setup.md")}#步驟-2 這裡");
        Assert.Equal("#步驟-2", hash);
        Assert.Equal(tree.In(@"docs\setup.md"), Found(rest));
    }

    // ---------- what the clipboard offers (Analyze) ----------

    PathFinder.Offer Offer(string text, int maxExtra = 30) => Finder(tree.Outside).Analyze(text, maxExtra);

    [Theory]
    [InlineData("docs/setup.md")]
    [InlineData("請看 docs/setup.md 這份")]
    [InlineData("「docs/setup.md」")]
    public void Copied_on_purpose(string text)
    {
        var offer = Offer(text);
        Assert.Equal(tree.In(@"docs\setup.md"), offer.Path);
        Assert.True(offer.Deliberate);
    }

    [Fact]
    public void Path_in_a_long_passage_is_only_mentioned()
    {
        var offer = Offer("這次修改主要是調整清單排序的規則，原本的做法在重複項目上會多算一次，細節請參考 docs/setup.md 裡第三節的說明，另外也更新了測試資料。");
        Assert.Equal(tree.In(@"docs\setup.md"), offer.Path);
        Assert.False(offer.Deliberate);
    }

    [Fact]
    public void Extra_word_limit_is_a_setting()
    {
        Assert.True(Offer("docs/setup.md", maxExtra: 0).Deliberate);
        Assert.True(Offer($"\"{tree.In(@"docs\setup.md")}\"", maxExtra: 0).Deliberate); // quotes do not count
        Assert.False(Offer("請看 docs/setup.md", maxExtra: 0).Deliberate);
        Assert.True(Offer("請看 docs/setup.md", maxExtra: 2).Deliberate);
    }

    [Fact]
    public void Several_files_in_text_order()
    {
        var offer = Offer("pics/dot.png 和 docs/setup.md");
        Assert.Equal([tree.In(@"pics\dot.png"), tree.In(@"docs\setup.md")], offer.Files);
        Assert.True(offer.Deliberate);
    }

    [Fact]
    public void Several_files_one_per_line()
    {
        var offer = Offer($"改了這幾份：\r\n- {tree.In(@"docs\setup.md")}\r\n- archive.md\r\n- `{tree.In(@"My Docs\a b.md")}`");
        Assert.Equal([tree.In(@"docs\setup.md"), Path.Combine(tree.Outside, "archive.md"), tree.In(@"My Docs\a b.md")], offer.Files);
    }

    [Fact]
    public void List_stops_at_the_limit_and_says_there_are_more()
    {
        var offer = Finder(tree.Outside).Analyze("docs/setup.md pics/dot.png guide.md archive.md", 30, listLimit: 2);
        Assert.Equal([tree.In(@"docs\setup.md"), tree.In(@"pics\dot.png")], offer.Files);
        Assert.True(offer.More);
    }

    [Fact]
    public void List_within_the_limit_has_no_more()
    {
        var offer = Finder(tree.Outside).Analyze("docs/setup.md pics/dot.png", 30, listLimit: 2);
        Assert.Equal(2, offer.Files.Count);
        Assert.False(offer.More);
    }

    [Fact]
    public void Open_first_mode_only_finds_the_first_and_whether_there_are_others()
    {
        var offer = Finder(tree.Outside).Analyze("docs/setup.md pics/dot.png guide.md", 30, listLimit: 1);
        Assert.Equal([tree.In(@"docs\setup.md")], offer.Files);
        Assert.True(offer.More);
        Assert.Equal(tree.In(@"docs\setup.md"), Finder().Analyze("docs/setup.md", 30, listLimit: 1).Path);
    }

    [Fact]
    public void Paths_not_looked_up_do_not_count_as_extra_words()
    {
        var three = string.Join("\n", tree.In(@"docs\setup.md"), tree.In(@"pics\dot.png"), tree.In("guide.md"));
        Assert.True(Finder().Analyze(three, 0, listLimit: 1).Deliberate);
        Assert.True(Finder().Analyze("docs/nope.md docs/setup.md", 0).Deliberate);
    }

    [Fact]
    public void Only_the_first_hundred_names_are_looked_at()
    {
        var text = string.Concat(Enumerable.Range(0, 100).Select(i => $"x{i}.md ")) + "docs/setup.md";
        Assert.Null(Finder().Analyze(text, 30).Path);
    }

    [Fact]
    public void Same_file_named_twice_is_one_file()
    {
        var offer = Offer($"{tree.In(@"docs\setup.md")} 也就是 docs/setup.md");
        Assert.Empty(offer.Files);
        Assert.Equal(tree.In(@"docs\setup.md"), offer.Path);
    }

    [Fact]
    public void Several_files_in_a_long_passage_are_only_mentioned()
    {
        var offer = Offer("這次修改主要是調整清單排序的規則，原本的做法在重複項目上會多算一次，細節請參考 docs/setup.md 和 pics/dot.png 的說明。");
        Assert.Equal(2, offer.Files.Count);
        Assert.False(offer.Deliberate);
    }

    [Fact]
    public void Missing_files_are_left_out_of_the_list()
    {
        var offer = Offer("docs/setup.md docs/nope.md pics/dot.png");
        Assert.Equal([tree.In(@"docs\setup.md"), tree.In(@"pics\dot.png")], offer.Files);
    }

    [Fact]
    public void Ambiguous_name_alone_still_offers_its_matches()
    {
        var offer = Finder().Analyze("notes.md", 30);
        Assert.Empty(offer.Files);
        Assert.Equal(2, offer.Matches.Count);
    }

    [Fact]
    public void Nothing_to_offer()
    {
        var offer = Offer("今天開會討論 commit gate 的時間");
        Assert.Null(offer.Path);
        Assert.Empty(offer.Matches);
        Assert.Empty(offer.Files);
    }

    // ---------- candidates for paths with spaces ----------

    [Fact]
    public void Spaced_candidates_longest_first_and_only_with_spaces()
    {
        var list = PathFinder.SpacedCandidates(@"see C:\My Docs\a.md now").ToList();
        Assert.Equal([@"see C:\My Docs\a.md", @"C:\My Docs\a.md"], list);
    }
}
