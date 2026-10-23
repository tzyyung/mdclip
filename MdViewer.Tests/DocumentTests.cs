namespace MdViewer.Tests;

public sealed class FrontMatterTests
{
    [Fact]
    public void Name_and_description_in_the_summary()
    {
        var html = FrontMatter.ToHtml("name: smoke-guide\ndescription: \"A fixture\"\n");
        Assert.Contains("<span class=\"fm-name\">smoke-guide</span>", html);
        Assert.Contains("<span class=\"fm-desc\">A fixture</span>", html);
    }

    [Fact]
    public void Lists_nested_keys_and_block_scalars()
    {
        var html = FrontMatter.ToHtml("tags:\n  - test\n  - fixture\nauthor:\n  name: Ann\nnote: |\n  first line\n  second line\n");
        Assert.Contains("<dt>tags</dt><dd>test, fixture</dd>", html);
        Assert.Contains("<dt>author.name</dt><dd>Ann</dd>", html);
        Assert.Contains("<dt>note</dt><dd>first line second line</dd>", html);
        Assert.DoesNotContain("<dt>author</dt>", html); // a parent with only nested keys is not listed itself
    }

    [Fact]
    public void Values_are_escaped() => Assert.Contains("&lt;script&gt;", FrontMatter.ToHtml("title: <script>\r\n"));

    [Fact]
    public void Nothing_to_show() => Assert.Equal("", FrontMatter.ToHtml("---\n\n"));
}

public sealed class DocumentTreeTests(Tree tree) : IClassFixture<Tree>
{
    [Fact]
    public void Folders_first_then_files_and_skipped_or_empty_folders_left_out()
    {
        Directory.CreateDirectory(tree.In(@"node_modules\pkg"));
        File.WriteAllText(tree.In(@"node_modules\pkg\readme.md"), "x");
        Directory.CreateDirectory(tree.In(".git"));
        File.WriteAllText(tree.In(@".git\x.md"), "x");
        Directory.CreateDirectory(tree.In("empty"));
        File.WriteAllText(tree.In(@"empty\notes.txt"), "x");

        var items = DocumentTree.Build(tree.Root);
        var names = items.Select(i => i.Name).ToList();
        Assert.DoesNotContain("node_modules", names);
        Assert.DoesNotContain(".git", names);
        Assert.DoesNotContain("empty", names);
        Assert.True(names.IndexOf("docs") < names.IndexOf("guide.md"), string.Join(",", names));
        Assert.Contains("docs/setup.md", DocumentTree.Files(items));
    }

    [Theory]
    [InlineData(@"docs\setup.md", false, true)]
    [InlineData(@"docs\notes.txt", false, false)]
    [InlineData(@"newfolder", true, true)]
    [InlineData(@"newfolder", false, false)]
    [InlineData(@"node_modules\pkg\readme.md", false, false)]
    [InlineData(@".git\objects\ab", true, false)]
    public void Which_changes_matter(string rel, bool structural, bool matters) =>
        Assert.Equal(matters, DocumentTree.Concerns(tree.Root, tree.In(rel), structural));
}

public sealed class DocumentSearchTests(Tree tree) : IClassFixture<Tree>
{
    [Fact]
    public void Hits_count_occurrences_as_the_page_does()
    {
        File.WriteAllText(tree.In("search.md"), "門檻 one\n```mermaid\ngraph 門檻\n```\n門檻 two 門檻\n");
        var result = new DocumentSearch().Run(tree.Root, ["search.md", "pics/dot.png"], "門檻");
        Assert.Equal(4, result.Total);
        Assert.Equal(1, result.Files);
        // the diagram's hit does not count as an occurrence on the page: it opens at the next one
        Assert.Equal([0, 1, 1, 2], result.Hits.Select(h => h.Nth));
        Assert.Equal(new DocumentSearch.Snippet("", "門檻", " one"), result.Hits[0].Snippet);
    }

    [Fact]
    public void A_changed_file_is_read_again()
    {
        var search = new DocumentSearch();
        File.WriteAllText(tree.In("change.md"), "alpha");
        Assert.Equal(1, search.Run(tree.Root, ["change.md"], "alpha").Total);
        File.WriteAllText(tree.In("change.md"), "beta");
        File.SetLastWriteTimeUtc(tree.In("change.md"), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(0, search.Run(tree.Root, ["change.md"], "alpha").Total);
    }

    [Fact]
    public void Empty_query_finds_nothing() => Assert.Equal(0, new DocumentSearch().Run(tree.Root, ["guide.md"], "  ").Total);
}

public sealed class DocumentPagesTests(Tree tree) : IClassFixture<Tree>
{
    readonly DocumentPages pages = new("/*style*/", () => "github-dark");

    [Fact]
    public void Every_page_has_the_theme_the_policy_and_the_styles()
    {
        var html = pages.Error("找不到", "x.md");
        Assert.Contains("data-theme=\"github-dark\"", html);
        Assert.Contains("script-src 'none'", html);
        Assert.Contains("/*style*/", html);
    }

    [Fact]
    public void Markdown_with_front_matter_and_heading_ids()
    {
        File.WriteAllText(tree.In("page.md"), "---\ntitle: T\n---\n# 標題\n");
        var html = pages.Document(tree.In("page.md"));
        Assert.Contains("class=\"frontmatter\"", html);
        Assert.Contains("id=\"標題\"", html);
        Assert.DoesNotContain("class=\"big\"", html);
    }

    [Fact]
    public void A_big_document_is_marked()
    {
        File.WriteAllText(tree.In("big.md"), string.Concat(Enumerable.Repeat("段落文字。\n\n", DocumentPages.BigPage / 8)));
        Assert.Contains("<body class=\"big\">", pages.Document(tree.In("big.md")));
    }

    [Fact]
    public void Image_page_escapes_its_source() => Assert.Contains("src=\"/a%20b.png?x=1&amp;y\"", pages.Image("/a%20b.png?x=1&y", "a b.png"));
}

public sealed class ContentTypeTests
{
    [Theory]
    [InlineData("a.PNG", "image/png")]
    [InlineData("a.svg", "image/svg+xml")]
    [InlineData("a.pdf", "application/pdf")]
    [InlineData("app.js", "text/javascript; charset=utf-8")]
    [InlineData("a.md", null)]
    public void Types(string path, string? type) => Assert.Equal(type, Paths.ContentType(path));

    [Fact]
    public void Every_listed_image_has_a_type() => Assert.All(Paths.ImageExtensions, e => Assert.StartsWith("image/", Paths.ContentType("x" + e)));

    [Fact]
    public void Theme_colours() => Assert.Equal("#0d1117", Themes.Find("github-dark").Hex("bg"));
}
