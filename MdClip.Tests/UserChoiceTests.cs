namespace MdClip.Tests;

// The UserChoice hash was checked against Windows on a real machine (every default it had stored matched);
// these pin it with a made-up user so a change to the computation shows.
public class UserChoiceTests
{
    const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    static readonly long When = new DateTime(2026, 10, 26, 10, 30, 45, DateTimeKind.Utc).ToFileTimeUtc();

    [Fact]
    public void Minute_is_the_write_time_cut_to_the_minute()
    {
        Assert.Equal(UserChoice.Minute(new DateTime(2026, 10, 26, 10, 30, 0, DateTimeKind.Utc).ToFileTimeUtc()), UserChoice.Minute(When));
        Assert.Equal(16, UserChoice.Minute(When).Length);
    }

    [Fact]
    public void Hash_is_stable() => Assert.Equal("j9GQxvjuNR8=", UserChoice.Hash(".png", Sid, "mdclip.image", When));

    [Fact]
    public void Hash_depends_on_the_app() =>
        Assert.NotEqual(UserChoice.Hash(".png", Sid, "mdclip.image", When), UserChoice.Hash(".png", Sid, "mdclip.md", When));
}
