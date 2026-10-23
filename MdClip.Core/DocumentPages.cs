using System.Net;
using System.Text;
using System.Text.Json;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.Yaml;
using Markdig.Renderers;
using Markdig.Syntax;

namespace MdClip;

// The HTML pages MdClip shows: rendered Markdown, plain text, the image viewer and error pages. Every page
// starts the same way (the current theme, the no-scripts policy, the styles), from one place.
// style: every theme's variables plus the stylesheets (the app holds them); themeId: the theme right now.
public sealed class DocumentPages(string style, Func<string> themeId)
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub) // keeps CJK heading ids
        .UseAdvancedExtensions()
        .UseYamlFrontMatter() // parsed out of the body; shown as a collapsible "文件資訊" box instead
        .Build();

    // What no page may do: fetch, frame, embed, post forms or change its base.
    const string Locked = "connect-src 'none'; frame-src 'none'; object-src 'none'; form-action 'none'; base-uri 'none'";

    // Documents may contain raw HTML. They share an origin with the app UI, so nothing in them may run:
    // no scripts (incl. inline handlers and javascript: links). Images may be remote. The app's own script
    // reaches into the page from the parent frame and is not affected. Also sent as a header with files
    // shown as they are (.html, .svg, PDF, pictures).
    public const string DocumentPolicy =
        "default-src 'self'; script-src 'none'; style-src 'self' 'unsafe-inline'; img-src * data: blob:; media-src * data: blob:; font-src 'self' data:; " + Locked;

    // The image viewer page only runs the app's zoom.js plus its own inline setup.
    const string ImagePolicy = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " + Locked;

    // Pages past this size (HTML characters) are marked "big": doc.css then lets the browser skip laying
    // out what is off screen, which is most of the time spent opening a document of several MB.
    public const int BigPage = 500_000;

    public string Document(string path)
    {
        var doc = Markdown.Parse(TextFiles.ReadText(path), Pipeline);
        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(doc);
        writer.Flush();
        var front = doc.Descendants<YamlFrontMatterBlock>().FirstOrDefault();
        return Page(Path.GetFileName(path), (front is null ? "" : FrontMatter.ToHtml(front.Lines.ToString())) + writer);
    }

    public string Plain(string path) =>
        Page(Path.GetFileName(path), $"<h1>{Encode(Path.GetFileName(path))}</h1><pre class=\"plain\"><code>{Encode(TextFiles.ReadText(path))}</code></pre>");

    public string Error(string title, string detail) =>
        Page(title, $"<h1>{Encode(title)}</h1><p><code>{Encode(detail)}</code></p>");

    // The zoomable viewer for a picture opened from the tree; src is the picture's URL as requested.
    public string Image(string src, string name) => Head(name, ImagePolicy) + $$"""
        <link rel="stylesheet" href="/__app/zoom.css"></head>
        <body class="image-page"><div class="zoom-stage" id="stage"><img id="img" src="{{Encode(src)}}" alt=""></div>
        <script src="/__app/zoom.js"></script>
        <script>
          const viewer = mdZoom(document.getElementById("stage"), document.getElementById("img"), { label: {{JsonSerializer.Serialize(name)}} });
          document.addEventListener("keydown", (e) => { if (viewer.key(e)) { e.preventDefault(); e.stopImmediatePropagation(); } });
        </script>
        </body></html>
        """;

    string Page(string title, string body) =>
        Head(title, DocumentPolicy) + $"</head><body{(body.Length > BigPage ? " class=\"big\"" : "")}><main>{body}</main></body></html>";

    // The start of every page, up to the open <head>.
    string Head(string title, string policy) => $$"""
        <!doctype html>
        <html data-theme="{{themeId()}}"><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="{{policy}}">
        <title>{{Encode(title)}}</title>
        <style>{{style}}</style>
        """;

    static string Encode(string text) => WebUtility.HtmlEncode(text);
}

// A document's YAML header as a collapsed box: name and description stay visible in the summary line,
// every field is listed when opened. Nested keys show as parent.child, lists joined with commas, block
// scalars (| and >) joined into one value. Not a full YAML parser: enough for front matter.
public static class FrontMatter
{
    public static string ToHtml(string yaml)
    {
        var rows = new List<(string Key, string Value)>();
        string parent = "";
        int blockIndent = -1; // inside a | or > value: lines indented deeper than this belong to it
        foreach (var line in TextFiles.Lines(yaml))
        {
            var text = line.Trim();
            if (text is "" or "---" or "...") continue;
            var indent = line.Length - line.TrimStart().Length;
            if (blockIndent >= 0 && indent > blockIndent && rows.Count > 0)
            {
                rows[^1] = (rows[^1].Key, (rows[^1].Value + " " + text).Trim());
                continue;
            }
            blockIndent = -1;
            if (text.StartsWith("- ") && rows.Count > 0)
            {
                var item = Unquote(text[2..]);
                rows[^1] = (rows[^1].Key, rows[^1].Value.Length > 0 ? rows[^1].Value + ", " + item : item);
                continue;
            }
            var colon = text.IndexOf(": ") is >= 0 and var c ? c : text.EndsWith(':') ? text.Length - 1 : -1;
            if (colon <= 0)
            {
                if (rows.Count > 0) rows[^1] = (rows[^1].Key, (rows[^1].Value + " " + text).Trim());
                continue;
            }
            var key = text[..colon].Trim();
            var value = text[(colon + 1)..].Trim();
            if (value is "|" or "|-" or ">" or ">-" or "|+" or ">+") { value = ""; blockIndent = indent; }
            value = Unquote(value);
            if (indent == 0) parent = value.Length == 0 && blockIndent < 0 ? key : "";
            rows.Add((indent > 0 && parent.Length > 0 ? $"{parent}.{key}" : key, value));
        }
        var shown = rows.Where(r => r.Value.Length > 0 || !rows.Any(o => o.Key.StartsWith(r.Key + "."))).ToList();
        if (shown.Count == 0) return "";

        string Find(params string[] keys) => shown.FirstOrDefault(r => keys.Contains(r.Key, StringComparer.OrdinalIgnoreCase)).Value ?? "";
        var name = Find("name", "title");
        var description = Find("description", "summary");
        var summary = new StringBuilder("<span class=\"fm-label\">文件資訊</span>");
        if (name.Length > 0) summary.Append($"<span class=\"fm-name\">{WebUtility.HtmlEncode(name)}</span>");
        if (description.Length > 0) summary.Append($"<span class=\"fm-desc\">{WebUtility.HtmlEncode(description)}</span>");
        var items = string.Concat(shown.Select(r => $"<dt>{WebUtility.HtmlEncode(r.Key)}</dt><dd>{WebUtility.HtmlEncode(r.Value)}</dd>"));
        return $"<details class=\"frontmatter\"><summary>{summary}</summary><dl>{items}</dl></details>";
    }

    static string Unquote(string value) => value.Trim().Trim('"', '\'');
}
