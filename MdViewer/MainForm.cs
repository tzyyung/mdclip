using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace MdViewer;

// Native host: one WebView2 filling the window. The UI (sidebar, toolbar, outline, palette) is the
// web app in web/, served at https://md.view/__app/. Files under the root folder are served at
// https://md.view/<relative path> (markdown rendered to HTML) and shown in the app's iframe, so
// relative links and images inside documents resolve naturally.
sealed class MainForm : Form
{
    const string Host = "md.view";
    const string Origin = $"https://{Host}/";
    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", "bin", "obj", "target", "dist", "build", "__pycache__", "venv", "packages" };
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub) // keeps CJK heading ids
        .UseAdvancedExtensions()
        .Build();
    // Path-looking tokens ending in a listed extension, e.g. E:\docs\b.md, docs/x.png, ./y.md:12
    static readonly Regex PathToken = new(
        @"(?:[A-Za-z]:[\\/]|\\\\)?[^\s""'<>|*?()\[\]`]*?\.(?:md|markdown|png|jpe?g|gif|svg|webp|bmp)\b",
        RegexOptions.IgnoreCase);
    // A listed extension at the end of a word; paths with spaces are found by trying starts to its left.
    static readonly Regex ExtensionEnd = new(@"\.(?:md|markdown|png|jpe?g|gif|svg|webp|bmp)\b", RegexOptions.IgnoreCase);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    WebView2 web = new() { Dock = DockStyle.Fill };
    readonly FileSystemWatcher watcher = new()
    {
        IncludeSubdirectories = true,
        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
    };
    readonly System.Windows.Forms.Timer debounce = new() { Interval = 300 };
    readonly HashSet<string> changedFiles = new(StringComparer.OrdinalIgnoreCase);
    readonly ThemeSettings settings = ThemeSettings.Load();

    Theme theme;
    string root = "";
    bool treeDirty;
    readonly System.Windows.Forms.Timer clipboardDelay = new() { Interval = 120 };
    uint lastClipboardSeq;
    int clipboardReadAttempts;
    List<string> knownFiles = []; // relative paths from the last tree scan, for file-name lookups
    readonly System.Windows.Forms.Timer saveDelay = new() { Interval = 1000 }; // batches reading-position saves
    bool systemDark = Themes.SystemIsDark();

    public MainForm(string? startPath)
    {
        Text = "MdViewer";
        Size = new Size(1440, 920);
        StartPosition = FormStartPosition.CenterScreen;
        theme = Themes.Resolve(settings.Theme);
        BackColor = theme.Color("bg");
        web.DefaultBackgroundColor = BackColor;
        Controls.Add(web);

        watcher.Changed += OnFsEvent;
        watcher.Created += OnFsEvent;
        watcher.Deleted += OnFsEvent;
        watcher.Renamed += OnFsEvent;
        clipboardDelay.Tick += (_, _) => ReadClipboard();
        saveDelay.Tick += (_, _) => { saveDelay.Stop(); settings.Save(); };
        FormClosing += (_, _) => { if (saveDelay.Enabled) settings.Save(); };
        SystemEvents.UserPreferenceChanged += OnSystemPreference;
        Disposed += (_, _) => SystemEvents.UserPreferenceChanged -= OnSystemPreference;
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            if (treeDirty) { treeDirty = false; Post(new { type = "tree" }); }
            foreach (var path in changedFiles) Post(new { type = "changed", rel = ToRel(path) });
            changedFiles.Clear();
        };

        Load += async (_, _) =>
        {
            ApplyTitleBar();
            ListenForOpenRequests();
            PruneMissingDirs();
            await InitAsync(startPath);
        };
    }

    async Task InitAsync(string? startPath)
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdViewer", "WebView2");
        try
        {
            await web.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, dataDir));
        }
        catch (COMException ex)
        {
            Program.LogError(ex);
            // The shared profile is held by a browser process started with other options
            // (e.g. a leftover debug session). Fall back to a profile private to this process.
            Controls.Remove(web);
            web.Dispose();
            web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            Controls.Add(web);
            var privateDir = Path.Combine(Path.GetTempPath(), "MdViewer", "WebView2-" + Environment.ProcessId);
            await web.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, privateDir));
        }

        var core = web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = true;
        core.Settings.IsZoomControlEnabled = false; // the web app zooms the document text itself
        core.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnResourceRequested;
        core.NavigationStarting += BlockExternal;
        core.FrameNavigationStarting += BlockExternal;
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
        core.WebMessageReceived += OnMessage;

        string? startRel = null;
        if (startPath is not null && File.Exists(startPath))
        {
            var full = Path.GetFullPath(startPath);
            SetRoot(FindRoot(full));
            startRel = ToRel(full);
        }
        else if (startPath is not null && Directory.Exists(startPath)) SetRoot(startPath);
        else if (Directory.Exists(settings.Root)) SetRoot(settings.Root);
        else SetRoot(Environment.CurrentDirectory);

        core.Navigate(Origin + "__app/" + (startRel is null ? "" : "#" + Uri.EscapeDataString(startRel)));
    }

    // ---------- single instance ----------

    // Later launches (double-clicking another .md) hand their path over this pipe; see Program.
    void ListenForOpenRequests()
    {
        Task.Run(async () =>
        {
            while (!IsDisposed)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Program.PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server);
                    var line = await reader.ReadLineAsync();
                    if (IsHandleCreated) BeginInvoke(() => OpenFromOtherInstance(line));
                }
                catch (IOException) { }
            }
        });
    }

    void OpenFromOtherInstance(string? path)
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        if (string.IsNullOrEmpty(path)) return;
        if (Directory.Exists(path)) SetRoot(path);
        else if (File.Exists(path) && OpenPath(path) is { } rel) Post(new { type = "open", rel });
    }

    // ---------- root & paths ----------

    void SetRoot(string dir)
    {
        root = Path.GetFullPath(dir).TrimEnd('\\');
        watcher.EnableRaisingEvents = false;
        watcher.Path = root;
        watcher.EnableRaisingEvents = true;
        settings.Root = root;
        settings.Save();
        Post(new { type = "root", root, name = RootName });
    }

    string RootName => Path.GetFileName(root) is { Length: > 0 } name ? name : root;

    // Prefer the enclosing git repo so sibling docs show up; otherwise the file's own folder.
    static string FindRoot(string file)
    {
        for (var d = new FileInfo(file).Directory; d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
        return Path.GetDirectoryName(file)!;
    }

    string ToRel(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    string? FromRel(string rel)
    {
        var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', '\\')));
        return IsUnder(full, root) ? full : null;
    }

    static bool IsUnder(string path, string dir) =>
        path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    static bool IsMarkdown(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown";

    static bool IsImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".webp" or ".bmp";

    // What the tree lists and the watcher cares about.
    static bool IsListed(string path) => IsMarkdown(path) || IsImage(path);

    // Accepts a pasted absolute path (file or folder, quoted or not, / or \), a file:// URL,
    // or a file path relative to the current root.
    string? ResolveInput(string text)
    {
        var s = text.Trim().Trim('"', '\'');
        if (s.Length == 0) return null;
        if (s.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(s, UriKind.Absolute, out var uri)) s = uri.LocalPath;
        s = s.Replace('/', '\\');
        try
        {
            if (Path.IsPathFullyQualified(s))
                return File.Exists(s) || Directory.Exists(s) ? Path.GetFullPath(s) : null;
            if (Path.GetFullPath(Path.Combine(root, s.TrimStart('\\'))) is var full && File.Exists(full))
                return full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        return null;
    }

    // Opens a file (switching root if it lives elsewhere) and returns its relative path,
    // or switches root and returns null for a folder.
    string? OpenPath(string path)
    {
        if (Directory.Exists(path)) { SetRoot(path); return null; }
        if (!IsUnder(path, root)) SetRoot(FindRoot(path));
        return ToRel(path);
    }

    // ---------- messages from the web app ----------

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var msg = doc.RootElement;
        string Str(string name) => msg.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

        switch (Str("type"))
        {
            case "theme": ApplyTheme(Str("id")); break;
            case "title": Text = Str("text") is { Length: > 0 } t ? $"{t} — MdViewer" : "MdViewer"; break;
            case "raise": BringToTop(); break;
            case "opened":
                if (FromRel(Str("rel")) is { } opened)
                {
                    settings.RememberDir(Path.GetDirectoryName(opened)!);
                    settings.RememberFile(opened);
                    Post(new { type = "history", items = HistoryItems() });
                }
                break;
            case "scroll":
                if (FromRel(Str("rel")) is { } read
                    && settings.History.FindIndex(h => h.Path.Equals(read, StringComparison.OrdinalIgnoreCase)) is >= 0 and var at)
                {
                    settings.History[at] = settings.History[at] with { Scroll = msg.TryGetProperty("y", out var y) && y.TryGetInt32(out var px) ? px : 0 };
                    saveDelay.Stop();
                    saveDelay.Start();
                }
                break;
            case "search": Search(Str("q"), Str("id")); break;
            case "clearHistory":
                settings.ClearHistory();
                Post(new { type = "history", items = HistoryItems() });
                break;
            case "resolve":
                var text = Str("text");
                var (found, matches) = ResolveInput(text) is { } direct ? (direct, []) : FindPathIn(text);
                if (found is null && matches.Count == 1) found = FromRel(matches[0]);
                if (found is not null) Post(new { type = "resolved", id = Str("id"), rel = OpenPath(found) });
                else Post(new
                {
                    type = "resolved", id = Str("id"),
                    error = matches.Count > 1 ? $"有 {matches.Count} 個同名檔，請用搜尋挑選" : "找不到這個檔案或資料夾",
                });
                break;
            case "pickFile": PickFile(); break;
            case "pickFolder": PickFolder(); break;
            case "reveal":
                if (FromRel(Str("rel")) is { } file) Process.Start("explorer.exe", $"/select,\"{file}\"");
                break;
            case "external": OpenExternal(Str("url")); break;
        }
    }

    // The reading history as the web app shows it: full path, when, and whether the file is still there.
    object[] HistoryItems() => settings.History
        .Select(h => (object)new { path = h.Path, when = new DateTimeOffset(h.When).ToUnixTimeMilliseconds(), scroll = h.Scroll, exists = File.Exists(h.Path) })
        .ToArray();

    // ---------- full-text search ----------

    const int MaxHits = 300;

    // Case-insensitive search through every Markdown file of the last tree scan, off the UI thread.
    // Each hit carries its occurrence number in the file so the page can jump to that same match.
    void Search(string query, string id)
    {
        var (files, dir) = (knownFiles, root);
        Task.Run(() =>
        {
            var hits = new List<object>();
            int total = 0, fileCount = 0;
            if (query.Trim().Length > 0)
            {
                foreach (var rel in files.Where(IsMarkdown))
                {
                    string[] lines;
                    try { lines = File.ReadAllLines(Path.Combine(dir, rel.Replace('/', '\\'))); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                    int nth = 0, fileHits = 0;
                    bool inDiagram = false;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        // Text inside a diagram is drawn as SVG, which the page's find does not count;
                        // such hits jump to the next match after the diagram.
                        var fence = lines[i].TrimStart();
                        if (fence.StartsWith("```mermaid") || fence.StartsWith("~~~mermaid")) inDiagram = true;
                        else if (inDiagram && (fence.StartsWith("```") || fence.StartsWith("~~~"))) inDiagram = false;
                        for (int at = lines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase); at >= 0;
                             at = lines[i].IndexOf(query, at + query.Length, StringComparison.OrdinalIgnoreCase))
                        {
                            if (fileHits++ == 0) fileCount++;
                            if (total++ < MaxHits) hits.Add(new { rel, line = i + 1, nth, snippet = Snippet(lines[i], at, query.Length) });
                            if (!inDiagram) nth++;
                        }
                    }
                }
            }
            if (IsHandleCreated) BeginInvoke(() => Post(new { type = "searchResults", id, hits, total, files = fileCount }));
        });
    }

    // The hit with some text on each side, split so the page can mark the match.
    static object Snippet(string line, int at, int length)
    {
        int start = Math.Max(0, at - 30), end = Math.Min(line.Length, at + length + 90);
        return new
        {
            before = (start > 0 ? "…" : "") + line[start..at].TrimStart(),
            match = line.Substring(at, length),
            after = line[(at + length)..end] + (end < line.Length ? "…" : ""),
        };
    }

    // ---------- system theme, remembered folders ----------

    // Follows Windows switching between light and dark while the theme is "auto".
    void OnSystemPreference(object? sender, UserPreferenceChangedEventArgs e)
    {
        var dark = Themes.SystemIsDark();
        if (e.Category != UserPreferenceCategory.General || dark == systemDark || !IsHandleCreated) return;
        systemDark = dark;
        BeginInvoke(() =>
        {
            if (settings.Theme == Themes.Auto) ApplyTheme(Themes.Auto);
            Post(new { type = "system", dark });
        });
    }

    // Forgets remembered folders that no longer exist; checked in the background since a folder on a
    // disconnected network drive can take seconds to answer.
    void PruneMissingDirs()
    {
        var dirs = settings.Dirs.ToList();
        Task.Run(() =>
        {
            var missing = dirs.Where(d => !Directory.Exists(d)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (missing.Count > 0 && IsHandleCreated) BeginInvoke(() => { settings.Dirs.RemoveAll(missing.Contains); settings.Save(); });
        });
    }

    void Post(object message)
    {
        if (web.CoreWebView2 is { } core) core.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
    }

    void PickFile()
    {
        using var dlg = new OpenFileDialog { Filter = "Markdown (*.md;*.markdown)|*.md;*.markdown|所有檔案|*.*", InitialDirectory = root };
        if (dlg.ShowDialog(this) == DialogResult.OK && OpenPath(dlg.FileName) is { } rel) Post(new { type = "open", rel });
    }

    void PickFolder()
    {
        using var dlg = new FolderBrowserDialog { InitialDirectory = root };
        if (dlg.ShowDialog(this) == DialogResult.OK) SetRoot(dlg.SelectedPath);
    }

    void BlockExternal(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("about:") || e.Uri.StartsWith("data:")) return;
        e.Cancel = true;
        OpenExternal(e.Uri);
    }

    static void OpenExternal(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    // ---------- clipboard watching ----------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AddClipboardFormatListener(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_CLIPBOARDUPDATE = 0x031D;
        if (m.Msg == WM_CLIPBOARDUPDATE) OnClipboardChanged();
        base.WndProc(ref m);
    }

    // Tells the web app when the clipboard holds a markdown or image file we can find: a copied file,
    // or text containing an absolute path, a root-relative path, or just a file name / path tail
    // ("archive.md", "docs/setup.md") that matches files under the root. The app decides what to do.
    // One copy often produces a burst of updates (one per clipboard format), and the copying app may
    // still hold the clipboard open when the first arrives. So wait until the burst settles, then read
    // once, and use the clipboard sequence number to skip content we have already handled.
    void OnClipboardChanged()
    {
        clipboardReadAttempts = 0;
        clipboardDelay.Stop();
        clipboardDelay.Start();
    }

    void ReadClipboard()
    {
        clipboardDelay.Stop();
        var seq = GetClipboardSequenceNumber();
        if (seq == lastClipboardSeq) return;

        string? found = null;
        List<string> matches = [];
        try
        {
            if (Clipboard.ContainsFileDropList())
                found = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(f => IsListed(f) && File.Exists(f));
            else if (Clipboard.ContainsText() && Clipboard.GetText() is { Length: <= 4000 } text)
                (found, matches) = FindPathIn(text);
        }
        catch (ExternalException ex)
        {
            // Still held by another app: try again shortly, and leave a trace if it never frees up.
            if (++clipboardReadAttempts < 5) clipboardDelay.Start();
            else Program.LogError(ex);
            return;
        }
        lastClipboardSeq = seq;

        if (found is null && matches.Count == 0) return;
        Post(new
        {
            type = "clipboard",
            path = found,
            rel = found is not null && IsUnder(found, root) ? ToRel(found) : null,
            matches = found is null ? matches : null,
            fromSelf = ActiveForm == this,
        });
    }

    (string? Path, List<string> Matches) FindPathIn(string text)
    {
        var candidates = new[] { text }
            .Concat(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Concat(SpacedCandidates(text))
            .Concat(PathToken.Matches(text).Select(m => m.Value))
            .ToList();
        RememberDirsIn(candidates);
        foreach (var candidate in candidates)
            if (ResolveInput(candidate) is { } path && IsListed(path) && File.Exists(path)) return (path, []);

        // Not a real path: treat it as a file name or path tail. A single match under the root wins;
        // otherwise look next to recently opened files (also outside the root), most recent first;
        // otherwise report every match under the root so the user can pick.
        foreach (var candidate in candidates)
        {
            var tail = candidate.Trim().Trim('"', '\'', '`').Replace('\\', '/').TrimStart('.', '/');
            if (tail.Length == 0 || tail.Any(char.IsWhiteSpace) || !IsListed(tail)) continue;
            var hits = knownFiles.Where(rel => rel.Equals(tail, StringComparison.OrdinalIgnoreCase)
                || rel.EndsWith("/" + tail, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 1) return (null, hits);
            if (FindInRecentDirs(tail) is { } recent) return (recent, []);
            if (hits.Count > 1) return (null, hits);
        }
        return (null, []);
    }

    // For each listed extension inside a sentence, the stretches that end there and contain a space,
    // starting at a drive letter, a UNC prefix or a word boundary, longest first: "see C:\My Docs\a.md now"
    // yields "see C:\My Docs\a.md", "C:\My Docs\a.md", ... Only ones that exist are used.
    static IEnumerable<string> SpacedCandidates(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            foreach (Match ext in ExtensionEnd.Matches(line))
            {
                var starts = new List<int>();
                for (int i = 0; i < ext.Index; i++)
                {
                    if (char.IsWhiteSpace(line[i])) continue;
                    bool afterBreak = i == 0 || char.IsWhiteSpace(line[i - 1]) || line[i - 1] is '"' or '\'' or '`' or '(' or '「' or '：';
                    bool drive = i + 2 < line.Length && char.IsAsciiLetter(line[i]) && line[i + 1] == ':' && line[i + 2] is '\\' or '/';
                    if (afterBreak || drive) starts.Add(i);
                }
                foreach (var start in starts.TakeLast(12))
                    if (line[start..(ext.Index + ext.Length)] is var candidate && candidate.Any(char.IsWhiteSpace))
                        yield return candidate;
            }
        }
    }

    // Any absolute folder in the copied text, or the folder of any absolute file path, is remembered
    // so a later copy of just a file name can be matched against it.
    void RememberDirsIn(IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (ResolveInput(candidate) is not { } path || !Path.IsPathFullyQualified(candidate.Trim().Trim('"', '\'', '`'))) continue;
            if (Directory.Exists(path)) settings.RememberDir(path);
            else if (IsListed(path)) settings.RememberDir(Path.GetDirectoryName(path)!);
        }
    }

    // Puts the window on top of the others so a clipboard-triggered page is visible, without taking
    // keyboard focus from the app the user copied in.
    void BringToTop()
    {
        const int SW_SHOWNOACTIVATE = 4;
        if (WindowState == FormWindowState.Minimized) ShowWindow(Handle, SW_SHOWNOACTIVATE);
        TopMost = true;
        TopMost = false;
    }

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hwnd, int command);

    string? FindInRecentDirs(string tail)
    {
        var rel = tail.Replace('/', '\\');
        foreach (var dir in settings.Dirs)
        {
            try
            {
                var path = Path.GetFullPath(Path.Combine(dir, rel));
                if (File.Exists(path)) return path;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        }
        return null;
    }

    [DllImport("user32.dll")]
    static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern uint GetClipboardSequenceNumber();

    // ---------- theme ----------

    void ApplyTheme(string id)
    {
        theme = Themes.Resolve(id);
        settings.Theme = id;
        if (id != Themes.Auto)
        {
            if (theme.Dark) settings.LastDark = id; else settings.LastLight = id;
        }
        settings.Save();
        BackColor = theme.Color("bg");
        web.DefaultBackgroundColor = BackColor;
        ApplyTitleBar();
    }

    void ApplyTitleBar()
    {
        var dark = theme.Dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); // immersive dark title bar
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ---------- file watching ----------

    void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        var structural = e.ChangeType != WatcherChangeTypes.Changed;
        var md = IsMarkdown(e.FullPath);
        if (!IsListed(e.FullPath) && !(structural && !Path.HasExtension(e.FullPath))) return;
        BeginInvoke(() =>
        {
            if (structural) treeDirty = true;
            if (md && e.ChangeType == WatcherChangeTypes.Changed) changedFiles.Add(e.FullPath);
            debounce.Stop();
            debounce.Start();
        });
    }

    // ---------- serving ----------

    void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var env = web.CoreWebView2.Environment;
        var rel = Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath.TrimStart('/'));
        try
        {
            if (rel.StartsWith("__app/"))
            {
                e.Response = AppResource(rel["__app/".Length..]) is var (bytes, type)
                    ? Respond(env, bytes, type)
                    : Respond(env, Encoding.UTF8.GetBytes(ErrorPage("找不到", rel)), "text/html; charset=utf-8", 404);
                return;
            }
            if (rel == "__api/tree")
            {
                var deferral = e.GetDeferral();
                var r = root;
                Task.Run(() => BuildChildren(new DirectoryInfo(r), r) ?? [])
                    .ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully)
                        {
                            if (r == root) knownFiles = Flatten(t.Result).ToList();
                            e.Response = Respond(env, JsonSerializer.SerializeToUtf8Bytes(t.Result, Json), "application/json");
                        }
                        else e.Response = Respond(env, "[]"u8.ToArray(), "application/json", 500);
                        deferral.Complete();
                    }, TaskScheduler.FromCurrentSynchronizationContext());
                return;
            }

            var path = FromRel(rel);
            if (path is null || !File.Exists(path))
            {
                e.Response = Respond(env, Encoding.UTF8.GetBytes(ErrorPage("找不到檔案", rel)), "text/html; charset=utf-8", 404);
                return;
            }
            var mime = MimeFor(path);
            if (IsImage(path) && new Uri(e.Request.Uri).Query.Contains("view"))
            {
                e.Response = Respond(env, Encoding.UTF8.GetBytes(ImagePage(rel)), "text/html; charset=utf-8");
                return;
            }
            e.Response = IsMarkdown(path) ? Respond(env, Encoding.UTF8.GetBytes(RenderMarkdown(path)), "text/html; charset=utf-8")
                : mime.StartsWith("text/plain") ? Respond(env, Encoding.UTF8.GetBytes(RenderPlain(path)), "text/html; charset=utf-8")
                : Respond(env, ReadShared(path), mime);
        }
        catch (IOException ex)
        {
            e.Response = Respond(env, Encoding.UTF8.GetBytes(ErrorPage("讀取失敗", ex.Message)), "text/html; charset=utf-8", 500);
        }
    }

    static CoreWebView2WebResourceResponse Respond(CoreWebView2Environment env, byte[] body, string type, int status = 200) =>
        env.CreateWebResourceResponse(new MemoryStream(body), status, status == 200 ? "OK" : "Error",
            $"Content-Type: {type}\nCache-Control: no-store");

    (byte[] Bytes, string Type)? AppResource(string name)
    {
        switch (name)
        {
            case "" or "index.html":
                var config = JsonSerializer.Serialize(new
                {
                    root,
                    name = RootName,
                    theme = settings.Theme,
                    lastLight = settings.LastLight,
                    lastDark = settings.LastDark,
                    systemDark = Themes.SystemIsDark(),
                    history = HistoryItems(),
                    themes = Themes.All.Select(t => new { t.Id, t.Name, t.Dark, bg = t.Hex("bg"), accent = t.Hex("accent"), h3 = t.Hex("h3") }),
                }, Json);
                var html = Resource("app.html")
                    .Replace("__THEME__", theme.Id)
                    .Replace("/*THEMES*/", Themes.Css)
                    .Replace("/*CONFIG*/", $"window.MD = {config};");
                return (Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8");
            case "app.css" or "zoom.css":
                return (Encoding.UTF8.GetBytes(Resource(name)), "text/css; charset=utf-8");
            case "app.js" or "zoom.js" or "hljs.min.js" or "mermaid.min.js":
                return (Encoding.UTF8.GetBytes(Resource(name)), "text/javascript; charset=utf-8");
            default:
                return null;
        }
    }

    static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new FileNotFoundException(name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    sealed record TreeItem(string Name, string? Path, List<TreeItem>? Children);

    static IEnumerable<string> Flatten(List<TreeItem> items) =>
        items.SelectMany(it => it.Children is { } children ? Flatten(children) : [it.Path!]);

    // Folders first, then markdown and image files; folders with none of those anywhere below are dropped.
    static List<TreeItem>? BuildChildren(DirectoryInfo dir, string root)
    {
        List<DirectoryInfo> dirs;
        List<FileInfo> files;
        try
        {
            dirs = dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
            files = dir.EnumerateFiles().Where(f => IsListed(f.Name)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }

        var items = new List<TreeItem>();
        foreach (var d in dirs)
        {
            if (d.Name.StartsWith('.') || SkipDirs.Contains(d.Name) || d.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (BuildChildren(d, root) is { Count: > 0 } children) items.Add(new(d.Name, null, children));
        }
        foreach (var f in files)
            items.Add(new(f.Name, Path.GetRelativePath(root, f.FullName).Replace('\\', '/'), null));
        return items;
    }

    // ---------- rendering ----------

    static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    static string ReadText(string path) => new UTF8Encoding(false).GetString(ReadShared(path)).TrimStart('﻿');

    string RenderMarkdown(string path) => Page(Path.GetFileName(path), Markdown.ToHtml(ReadText(path), Pipeline));

    string RenderPlain(string path) =>
        Page(Path.GetFileName(path), $"<h1>{WebUtility.HtmlEncode(Path.GetFileName(path))}</h1><pre class=\"plain\"><code>{WebUtility.HtmlEncode(ReadText(path))}</code></pre>");

    // Standalone image viewer (wheel zoom, drag pan) shown when an image file is opened from the tree.
    string ImagePage(string rel)
    {
        var url = "/" + string.Join('/', rel.Split('/').Select(Uri.EscapeDataString));
        var label = JsonSerializer.Serialize(Path.GetFileName(rel));
        return $$"""
            <!doctype html>
            <html data-theme="{{theme.Id}}"><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="{{ImagePolicy}}">
            <title>{{WebUtility.HtmlEncode(Path.GetFileName(rel))}}</title>
            <style>{{Themes.Css}}{{Resource("doc.css")}}</style><link rel="stylesheet" href="/__app/zoom.css"></head>
            <body class="image-page"><div class="zoom-stage" id="stage"><img id="img" src="{{url}}" alt=""></div>
            <script src="/__app/zoom.js"></script>
            <script>
              const viewer = mdZoom(document.getElementById("stage"), document.getElementById("img"), { label: {{label}} });
              document.addEventListener("keydown", (e) => { if (viewer.key(e)) { e.preventDefault(); e.stopImmediatePropagation(); } });
            </script>
            </body></html>
            """;
    }

    string ErrorPage(string title, string detail) =>
        Page(title, $"<h1>{WebUtility.HtmlEncode(title)}</h1><p><code>{WebUtility.HtmlEncode(detail)}</code></p>");

    // Documents may contain raw HTML. They share an origin with the app UI, so nothing in them may run:
    // no scripts (incl. inline handlers and javascript: links), frames, forms or fetches. Images may be remote.
    // The app's own script reaches into the page from the parent frame and is not affected.
    const string DocumentPolicy =
        "default-src 'self'; script-src 'none'; style-src 'self' 'unsafe-inline'; img-src * data: blob:; " +
        "media-src * data: blob:; font-src 'self' data:; connect-src 'none'; frame-src 'none'; object-src 'none'; " +
        "form-action 'none'; base-uri 'none'";

    // The image viewer page only runs the app's zoom.js plus its own inline setup.
    const string ImagePolicy =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
        "connect-src 'none'; frame-src 'none'; object-src 'none'; form-action 'none'; base-uri 'none'";

    string Page(string title, string body) => $$"""
        <!doctype html>
        <html data-theme="{{theme.Id}}"><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="{{DocumentPolicy}}">
        <title>{{WebUtility.HtmlEncode(title)}}</title>
        <style>{{Themes.Css}}{{Resource("doc.css")}}</style></head>
        <body><main>{{body}}</main></body></html>
        """;

    static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".ico" => "image/x-icon",
        ".pdf" => "application/pdf",
        ".html" or ".htm" => "text/html; charset=utf-8",
        _ => "text/plain; charset=utf-8",
    };
}
