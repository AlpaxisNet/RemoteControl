namespace RemoteControl.Ui.Common.Tests;

public sealed class FileDropTests
{
    [Fact]
    public void A_uri_list_gives_the_local_files_in_order_once_each()
    {
        string list = "# dropped by a file manager\r\nfile:///home/me/levels/start.nw\r\nfile://localhost/home/me/My%20Pictures/logo%C3%A9.png\r\n"
            + "file:///home/me/levels/start.nw\r\n\r\nhttp://example.com/x.png\r\nfile://otherhost/home/x\r\nfile:relative\r\nfile:///bad%00name\r\n";
        Assert.Equal(["/home/me/levels/start.nw", "/home/me/My Pictures/logoé.png"], FileDrop.ParseUriList(list));
    }

    [Fact]
    public void The_computers_own_name_counts_as_this_computer()
    {
        Assert.Equal(["/tmp/a b"], FileDrop.ParseUriList("file://" + Environment.MachineName + "/tmp/a%20b\n"));
    }

    [Fact]
    public void Nothing_gives_nothing()
    {
        Assert.Empty(FileDrop.ParseUriList(null));
        Assert.Empty(FileDrop.ParseUriList(""));
        Assert.Empty(FileDrop.ParseUriList("\0"));
    }
}
