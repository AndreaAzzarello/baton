using System.IO;
using System.Reflection;
using Baton.App.Services;
using Baton.App.ViewModels;
using Baton.Core;
using Baton.Core.Setup;

namespace Baton.UiTests;

/// <summary>
/// The extension shipped next to Baton.exe as the first-run wizard and the settings offer it (installed and portable
/// layouts), and start with Windows, never preselected and with a warning in the portable copy.
/// </summary>
public sealed class BundledExtensionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", "bundled-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Program(string layout)
    {
        var program = Path.Combine(_dir, layout);
        Directory.CreateDirectory(Path.Combine(program, "extension"));
        File.WriteAllText(Path.Combine(program, "Baton.exe"), "MZ");
        File.WriteAllText(Path.Combine(program, "extension", "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(program, "baton-firefox.xpi"), "PK");
        if (layout == "portable")
            File.WriteAllText(Path.Combine(program, "portable.flag"), "");
        return program;
    }

    private static (IShellService Shell, MainListCommandsTests.Fake<IShellService> Fake) Shell()
    {
        var shell = DispatchProxy.Create<IShellService, MainListCommandsTests.Fake<IShellService>>();
        return (shell, (MainListCommandsTests.Fake<IShellService>)(object)shell);
    }

    [Theory]
    [InlineData("installed")]
    [InlineData("portable")]
    public void TheButtons_AreOn_AndUseTheFullPathNextToTheProgram(string layout)
    {
        var program = Program(layout);
        var (shell, calls) = Shell();
        var extension = new BundledExtensionViewModel(program, shell);

        Assert.True(extension.IsIncluded);
        Assert.Equal(Path.Combine(program, "extension"), extension.Folder);
        Assert.True(Path.IsPathFullyQualified(extension.Folder!));
        Assert.True(extension.OpenFolderCommand.CanExecute(null));
        Assert.True(extension.CopyPathCommand.CanExecute(null));
        extension.OpenFolderCommand.Execute(null);
        extension.CopyPathCommand.Execute(null);
        Assert.Equal([("OpenFolder", extension.Folder), ("SetClipboardText", extension.Folder)],
            calls.Calls.Select(c => (c.Method, (string?)c.Args[0])));
        Assert.Contains(Path.Combine(program, "baton-firefox.xpi"), extension.FirefoxText);
    }

    [Fact]
    public void ADevelopmentBuild_PointsToTheReleasePage()
    {
        var (shell, calls) = Shell();
        var extension = new BundledExtensionViewModel(Path.Combine(_dir, "empty"), shell);

        Assert.True(extension.IsMissing);
        Assert.False(extension.OpenFolderCommand.CanExecute(null));
        Assert.False(extension.CopyPathCommand.CanExecute(null));
        extension.OpenReleasePageCommand.Execute(null);
        Assert.Equal(("OpenFile", BundledExtension.ReleasesPage), (calls.Calls[0].Method, (string?)calls.Calls[0].Args[0]));
    }

    [Fact]
    public void StartWithWindows_IsNeverPreselected_AndWarnsOnlyThePortableCopy()
    {
        var dialogs = DispatchProxy.Create<IDialogService, MainListCommandsTests.Fake<IDialogService>>();
        var (shell, _) = Shell();
        var installedExe = Path.Combine(Program("installed"), "Baton.exe");
        var portableExe = Path.Combine(Program("portable"), "Baton.exe");

        var installed = new FirstRunViewModel(dialogs, shell, [], [], Path.GetDirectoryName(installedExe)!, () => null);
        var portable = new FirstRunViewModel(dialogs, shell, [], [], Path.GetDirectoryName(portableExe)!, () => null, portableExe);
        Assert.False(installed.StartWithWindows);
        Assert.False(portable.StartWithWindows);
        Assert.False(portable.Startup.ShowPortableWarning);
        installed.Startup.Enabled = portable.Startup.Enabled = true;
        Assert.True(installed.StartWithWindows);
        Assert.False(installed.Startup.ShowPortableWarning);
        Assert.True(portable.Startup.ShowPortableWarning);
        Assert.Contains(portableExe, portable.Startup.PortableWarning);

        var settings = new SettingsViewModel(dialogs, new AppSettings(), runAtStartup: false, portableProgram: portableExe);
        Assert.False(settings.RunAtStartup);
        Assert.False(settings.Startup.ShowPortableWarning);
        settings.RunAtStartup = true;
        Assert.True(settings.Startup.ShowPortableWarning);
        Assert.False(new SettingsViewModel(dialogs, new AppSettings(), runAtStartup: true) { RunAtStartup = true }.Startup.ShowPortableWarning);
    }
}
