namespace MdClip;

// What to do with files found on the clipboard: the one place for the clipboard modes' rules, used for the
// page while the window is open and for the tray notification while it is hidden.
public enum ClipAction
{
    None,      // the mode is "off"
    Open,      // open the file
    Offer,     // "hint" mode: say so, with a button that opens it
    Mention,   // the text merely mentions files (more other words than allowed): offer quietly
    List,      // several files: offer the list (or show it at once when asked for)
    OpenFirst, // several files, "open the first": open it and offer the rest
}

public static class ClipboardPolicy
{
    // deliberate: few other words came with the paths (see PathFinder.Analyze). force: the user asked for
    // it by clicking the tray notification, so neither the mode nor the passage holds it back.
    public static ClipAction Decide(string mode, string multi, bool deliberate, bool severalFiles, bool force)
    {
        if (!force && mode == Settings.ClipOff) return ClipAction.None;
        if (!force && !deliberate) return ClipAction.Mention;
        if (!force && mode == Settings.ClipHint) return severalFiles ? ClipAction.List : ClipAction.Offer;
        if (!severalFiles) return ClipAction.Open;
        return multi == Settings.MultiFirst ? ClipAction.OpenFirst : ClipAction.List;
    }

    // Worth bringing the window to the front for: anything but a quiet mention.
    public static bool Raises(ClipAction action) => action is not (ClipAction.None or ClipAction.Mention);

    // While hidden in the tray: a notification for what only offers; a mention is not worth one.
    public static bool Notifies(ClipAction action) => action is ClipAction.Offer or ClipAction.List;
}
