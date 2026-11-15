namespace RemoteControl.Core.Tests;

/// <summary>A temporary folder that is removed afterwards; tests never use the real user folder.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rc-core-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A user folder and a program folder in one temporary folder.</summary>
internal sealed class TestInstall : IDisposable
{
    private readonly TempFolder _folder = new();

    public TestInstall(bool withBackground = true)
    {
        Directory.CreateDirectory(UserDirectory);
        Directory.CreateDirectory(ProgramDirectory);
        if (withBackground)
        {
            Directory.CreateDirectory(System.IO.Path.Combine(ProgramDirectory, "images"));
            File.WriteAllBytes(System.IO.Path.Combine(ProgramDirectory, "images", "rc_background.png"), [1, 2, 3]);
        }
        Paths = new AppPaths(UserDirectory, ProgramDirectory);
    }

    public string UserDirectory => _folder.Combine("user");

    public string ProgramDirectory => _folder.Combine("program");

    public AppPaths Paths { get; }

    public void Dispose() => _folder.Dispose();
}

internal static class RepositoryFiles
{
    /// <summary>Finds a file of the repository by walking up from the test assembly.</summary>
    public static string Find(params string[] relative)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            string candidate = Path.Combine([directory, .. relative]);
            if (File.Exists(candidate)) return candidate;
            directory = Path.GetDirectoryName(directory);
        }
        throw new FileNotFoundException("Repository file not found: " + string.Join('/', relative));
    }
}
