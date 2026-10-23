namespace MdClip.Tests;

// The clipboard modes' rules, as one table: mode, several-files choice, copied on purpose, several files,
// clicked in the tray -> what happens.
public sealed class ClipboardPolicyTests
{
    const string Auto = Settings.ClipAuto, Hint = Settings.ClipHint, Off = Settings.ClipOff;
    const string List = Settings.MultiList, First = Settings.MultiFirst;

    [Theory]
    [InlineData(Auto, List, true, false, false, ClipAction.Open)]
    [InlineData(Auto, List, true, true, false, ClipAction.List)]
    [InlineData(Auto, First, true, true, false, ClipAction.OpenFirst)]
    [InlineData(Auto, List, false, false, false, ClipAction.Mention)]   // a passage only mentions it
    [InlineData(Auto, First, false, true, false, ClipAction.Mention)]
    [InlineData(Hint, List, true, false, false, ClipAction.Offer)]
    [InlineData(Hint, First, true, true, false, ClipAction.List)]
    [InlineData(Hint, List, false, false, false, ClipAction.Mention)]
    [InlineData(Off, List, true, false, false, ClipAction.None)]
    [InlineData(Off, List, false, true, false, ClipAction.None)]
    [InlineData(Hint, List, true, false, true, ClipAction.Open)]        // clicked the notification
    [InlineData(Hint, First, true, true, true, ClipAction.OpenFirst)]
    [InlineData(Off, List, false, true, true, ClipAction.List)]
    public void Decide(string mode, string multi, bool deliberate, bool several, bool force, ClipAction expected) =>
        Assert.Equal(expected, ClipboardPolicy.Decide(mode, multi, deliberate, several, force));

    [Fact]
    public void Raising_and_notifying()
    {
        Assert.False(ClipboardPolicy.Raises(ClipAction.Mention));
        Assert.True(ClipboardPolicy.Raises(ClipAction.Offer));
        Assert.True(ClipboardPolicy.Notifies(ClipAction.List));
        Assert.False(ClipboardPolicy.Notifies(ClipAction.Open));
    }

    [Fact]
    public void Reading_a_file_remembers_its_folder()
    {
        var s = new Settings();
        s.RememberFile(@"E:\docs\a.md");
        Assert.Equal(@"E:\docs", s.Dirs[0]);
    }
}
