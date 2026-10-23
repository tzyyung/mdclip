using System.Drawing;
using System.Text.RegularExpressions;

namespace MdViewer;

// Page palettes. Each theme only sets CSS variables; the app's stylesheets consume them.
// Colors follow each project's published palette (GitHub Primer, draculatheme.com, nordtheme.com, ...).
public sealed record Theme(string Id, string Name, bool Dark, string Vars)
{
    public string Hex(string name) => Regex.Match(Vars, $@"--{name}:\s*(#[0-9a-fA-F]{{6}})").Groups[1].Value;

    public Color Color(string name) => ColorTranslator.FromHtml(Hex(name));
}

public static class Themes
{
    public const string Auto = "auto";
    // The pair "auto" switches between with Windows' light/dark setting (the web page is told these too).
    public const string AutoLight = "colorful-light", AutoDark = "colorful-dark";

    public static readonly Theme[] All =
    [
        new("colorful-light", "繽紛（亮）", false, """
            --bg:#ffffff; --fg:#1d2433; --muted:#5b6578; --border:#d5dce8;
            --h1:#123b7a; --h2:#1c5bb8; --h3:#0f7b74; --h4:#6a3fb5; --accent:#1c5bb8; --accent-soft:#e8f0fc;
            --strong:#0e1a33; --mark:#ffe36b; --em:#9a3d8f; --td-strong:#c2410c;
            --code-fg:#0b6b63; --code-bg:#e6f5f2; --pre-bg:#f4f6fa; --link:#0a63d6;
            --th-bg:#1f4f99; --th-fg:#ffffff; --stripe:#f5f8fd; --hover:#fff8d6; --quote-bg:#eef4ff; --quote-bar:#3b7be0;
            """),
        new("colorful-dark", "繽紛（暗）", true, """
            --bg:#14181f; --fg:#dbe2ee; --muted:#93a0b5; --border:#323b4a;
            --h1:#8fb8ff; --h2:#6ea6ff; --h3:#4fd1c0; --h4:#c39bff; --accent:#6ea6ff; --accent-soft:#1c2a40;
            --strong:#ffffff; --mark:#8a6d00; --em:#f0a3e4; --td-strong:#ffad6b;
            --code-fg:#6fe0cf; --code-bg:#17302d; --pre-bg:#1b212b; --link:#7db4ff;
            --th-bg:#24467d; --th-fg:#f2f6ff; --stripe:#1a1f28; --hover:#3a3214; --quote-bg:#1a2536; --quote-bar:#5b95f0;
            """),
        new("github-light", "GitHub Light", false, """
            --bg:#ffffff; --fg:#1f2328; --muted:#59636e; --border:#d1d9e0;
            --h1:#1f2328; --h2:#0969da; --h3:#1a7f37; --h4:#8250df; --accent:#0969da; --accent-soft:#ddf4ff;
            --strong:#1f2328; --mark:#fff8c5; --em:#8250df; --td-strong:#bc4c00;
            --code-fg:#0550ae; --code-bg:#eff1f3; --pre-bg:#f6f8fa; --link:#0969da;
            --th-bg:#f6f8fa; --th-fg:#1f2328; --stripe:#f6f8fa; --hover:#fff8c5; --quote-bg:#f6f8fa; --quote-bar:#0969da;
            """),
        new("github-dark", "GitHub Dark", true, """
            --bg:#0d1117; --fg:#f0f6fc; --muted:#9198a1; --border:#3d444d;
            --h1:#f0f6fc; --h2:#4493f8; --h3:#3fb950; --h4:#ab7df8; --accent:#4493f8; --accent-soft:#121d2f;
            --strong:#ffffff; --mark:#5a4a10; --em:#d2a8ff; --td-strong:#ffa657;
            --code-fg:#79c0ff; --code-bg:#262c36; --pre-bg:#151b23; --link:#4493f8;
            --th-bg:#151b23; --th-fg:#f0f6fc; --stripe:#151b23; --hover:#2e2a16; --quote-bg:#121d2f; --quote-bar:#4493f8;
            """),
        new("dracula", "Dracula", true, """
            --bg:#282a36; --fg:#f8f8f2; --muted:#a0a8cd; --border:#44475a;
            --h1:#ff79c6; --h2:#bd93f9; --h3:#50fa7b; --h4:#8be9fd; --accent:#bd93f9; --accent-soft:#343746;
            --strong:#ffffff; --mark:#6e6a3a; --em:#f1fa8c; --td-strong:#ffb86c;
            --code-fg:#50fa7b; --code-bg:#343746; --pre-bg:#21222c; --link:#8be9fd;
            --th-bg:#44475a; --th-fg:#f8f8f2; --stripe:#2d2f3d; --hover:#3b3d4f; --quote-bg:#343746; --quote-bar:#bd93f9;
            """),
        new("nord", "Nord", true, """
            --bg:#2e3440; --fg:#d8dee9; --muted:#a0a8b7; --border:#434c5e;
            --h1:#88c0d0; --h2:#81a1c1; --h3:#a3be8c; --h4:#b48ead; --accent:#88c0d0; --accent-soft:#3b4252;
            --strong:#eceff4; --mark:#7a6a48; --em:#ebcb8b; --td-strong:#d08770;
            --code-fg:#8fbcbb; --code-bg:#3b4252; --pre-bg:#3b4252; --link:#88c0d0;
            --th-bg:#434c5e; --th-fg:#eceff4; --stripe:#323946; --hover:#434c5e; --quote-bg:#3b4252; --quote-bar:#81a1c1;
            """),
        new("one-dark", "One Dark", true, """
            --bg:#282c34; --fg:#abb2bf; --muted:#7f848e; --border:#3e4451;
            --h1:#61afef; --h2:#c678dd; --h3:#98c379; --h4:#e5c07b; --accent:#61afef; --accent-soft:#2c313a;
            --strong:#e6e6e6; --mark:#6b5d3c; --em:#c678dd; --td-strong:#d19a66;
            --code-fg:#e06c75; --code-bg:#2c313a; --pre-bg:#21252b; --link:#61afef;
            --th-bg:#3e4451; --th-fg:#e6e6e6; --stripe:#2c313a; --hover:#3a3f4b; --quote-bg:#2c313a; --quote-bar:#61afef;
            """),
        new("solarized-light", "Solarized Light", false, """
            --bg:#fdf6e3; --fg:#586e75; --muted:#839496; --border:#e4dcc4;
            --h1:#cb4b16; --h2:#268bd2; --h3:#859900; --h4:#6c71c4; --accent:#268bd2; --accent-soft:#eee8d5;
            --strong:#073642; --mark:#f0d78a; --em:#d33682; --td-strong:#cb4b16;
            --code-fg:#2aa198; --code-bg:#eee8d5; --pre-bg:#eee8d5; --link:#268bd2;
            --th-bg:#eee8d5; --th-fg:#073642; --stripe:#f7f0dc; --hover:#f3e7c0; --quote-bg:#eee8d5; --quote-bar:#b58900;
            """),
        new("solarized-dark", "Solarized Dark", true, """
            --bg:#002b36; --fg:#93a1a1; --muted:#839496; --border:#0d4a5a;
            --h1:#cb4b16; --h2:#268bd2; --h3:#859900; --h4:#6c71c4; --accent:#268bd2; --accent-soft:#073642;
            --strong:#eee8d5; --mark:#5c5a12; --em:#d33682; --td-strong:#cb4b16;
            --code-fg:#2aa198; --code-bg:#073642; --pre-bg:#073642; --link:#268bd2;
            --th-bg:#073642; --th-fg:#eee8d5; --stripe:#01313d; --hover:#0a3f4c; --quote-bg:#073642; --quote-bar:#b58900;
            """),
        new("tokyo-night", "Tokyo Night", true, """
            --bg:#1a1b26; --fg:#c0caf5; --muted:#7f87b0; --border:#292e42;
            --h1:#7aa2f7; --h2:#bb9af7; --h3:#9ece6a; --h4:#7dcfff; --accent:#7aa2f7; --accent-soft:#24283b;
            --strong:#e0e6ff; --mark:#6b5530; --em:#ff9e64; --td-strong:#ff9e64;
            --code-fg:#73daca; --code-bg:#24283b; --pre-bg:#16161e; --link:#7aa2f7;
            --th-bg:#24283b; --th-fg:#c0caf5; --stripe:#1f2030; --hover:#292e42; --quote-bg:#1f2335; --quote-bar:#7aa2f7;
            """),
        new("catppuccin-mocha", "Catppuccin Mocha", true, """
            --bg:#1e1e2e; --fg:#cdd6f4; --muted:#a6adc8; --border:#45475a;
            --h1:#cba6f7; --h2:#89b4fa; --h3:#a6e3a1; --h4:#f9e2af; --accent:#cba6f7; --accent-soft:#313244;
            --strong:#ffffff; --mark:#6a6040; --em:#f5c2e7; --td-strong:#fab387;
            --code-fg:#94e2d5; --code-bg:#313244; --pre-bg:#181825; --link:#89b4fa;
            --th-bg:#313244; --th-fg:#cdd6f4; --stripe:#24243a; --hover:#3a3b52; --quote-bg:#262637; --quote-bar:#cba6f7;
            """),
        new("gruvbox-dark", "Gruvbox Dark", true, """
            --bg:#282828; --fg:#ebdbb2; --muted:#a89984; --border:#504945;
            --h1:#fb4934; --h2:#fabd2f; --h3:#b8bb26; --h4:#83a598; --accent:#fabd2f; --accent-soft:#32302f;
            --strong:#fbf1c7; --mark:#79600e; --em:#d3869b; --td-strong:#fe8019;
            --code-fg:#8ec07c; --code-bg:#3c3836; --pre-bg:#1d2021; --link:#83a598;
            --th-bg:#3c3836; --th-fg:#fbf1c7; --stripe:#2e2c2b; --hover:#3c3836; --quote-bg:#32302f; --quote-bar:#fabd2f;
            """),
    ];

    public static Theme Find(string id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];

    // "auto" follows Windows' app light/dark setting.
    public static Theme Resolve(string id) => id == Auto ? Find(SystemIsDark() ? AutoDark : AutoLight) : Find(id);

    // Every theme's variables, for the app page and every document; built once.
    public static readonly string Css = string.Join("\n", All.Select(t =>
        $":root[data-theme=\"{t.Id}\"] {{ color-scheme: {(t.Dark ? "dark" : "light")}; {t.Vars} }}"));

    public static bool SystemIsDark()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }
}
