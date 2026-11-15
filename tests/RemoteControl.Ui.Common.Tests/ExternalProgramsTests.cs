using Avalonia.Headless.XUnit;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Common.Tests;

/// <summary>
/// No test starts a program on the user's desktop. A window test once clicked a chat link and the user's real browser opened:
/// the opener and the sound player now start nothing in a test run and only record what they were asked for.
/// </summary>
public sealed class ExternalProgramsTests
{
    [AvaloniaFact]
    public void A_test_run_has_external_programs_switched_off()
    {
        Assert.True(ExternalPrograms.Disabled);
    }

    [AvaloniaFact]
    public void The_services_of_a_test_record_a_link_instead_of_opening_it()
    {
        using var env = new TestEnvironment();
        ExternalPrograms.ClearSuppressed();

        Assert.True(env.Services.Urls.Open("https://www.example.invalid/page"));

        Assert.Equal(["url https://www.example.invalid/page"], ExternalPrograms.Suppressed);
    }

    [AvaloniaFact]
    public void An_opener_made_by_hand_starts_nothing_either()
    {
        using var env = new TestEnvironment();
        ExternalPrograms.ClearSuppressed();
        var opener = new UrlOpener(env.Services.Options, env.Log);

        Assert.True(opener.Open("mailto:someone@example.invalid"));

        Assert.Equal(["url mailto:someone@example.invalid"], ExternalPrograms.Suppressed);
    }

    [AvaloniaFact]
    public void Opening_a_file_with_its_program_starts_nothing_in_a_test()
    {
        using var env = new TestEnvironment();
        string file = Path.Combine(env.UserDirectory, "note.txt");
        File.WriteAllText(file, "hello");
        ExternalPrograms.ClearSuppressed();

        Assert.True(ExternalPrograms.OpenFile(file, env.Log));
        Assert.False(ExternalPrograms.OpenFile(Path.Combine(env.UserDirectory, "missing.txt"), env.Log));

        Assert.Equal(["file " + Path.GetFullPath(file)], ExternalPrograms.Suppressed);
    }

    [AvaloniaFact]
    public void An_enabled_sound_player_starts_nothing_in_a_test()
    {
        using var env = new TestEnvironment();
        ExternalPrograms.ClearSuppressed();
        var sounds = new SoundPlayer(env.Paths, env.Log, enabled: true);
        string? file = Directory.Exists(env.Paths.SoundsDirectory) ? Directory.EnumerateFiles(env.Paths.SoundsDirectory).FirstOrDefault() : null;
        Assert.NotNull(file);

        Assert.False(sounds.Play(file));

        Assert.Equal(0, sounds.StartedCount);
        Assert.Contains(ExternalPrograms.Suppressed, entry => entry.StartsWith("sound ", StringComparison.Ordinal));
    }
}
