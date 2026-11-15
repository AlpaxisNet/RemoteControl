using System.Runtime.CompilerServices;
using RemoteControl.Scripting;
using RemoteControl.Ui.Scripts;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(RemoteControl.Ui.TestKit.TestAppBuilder))]
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace RemoteControl.Ui.Scripts.Tests;

/// <summary>Makes sure no editor of the tests ever loads the script API from the network.</summary>
internal static class AssemblySetup
{
    /// <summary>The API every test editor gets.</summary>
    public static IReadOnlyList<ApiDefinition> Definitions { get; } =
    [
        new ApiDefinition("echo", "function", ["text"], "", "script", "Writes a line to the RC chat."),
        new ApiDefinition("findplayer", "function", ["account"], "TServerPlayer", "server", "Finds an online player."),
        new ApiDefinition("setshape", "function", ["type", "width", "height"], "", "script", "Sets the shape of the NPC."),
        new ApiDefinition("sendtonc", "function", ["text"], "", "server", "Sends a line to the NC."),
    ];

#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Initialize()
    {
        ApiDefinitionCatalog.Load = (_, _, _) => Task.FromResult(new ApiLoadResult(ApiDefinitionSet.Create(Definitions), ApiDefinitionOrigin.Source, null));
        ExternalEditor.TestCommandFolder = null;
    }
}
