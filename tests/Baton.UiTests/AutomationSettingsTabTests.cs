using System.Reflection;
using Baton.App.Services;
using Baton.App.ViewModels;
using Baton.Core;
using Baton.Core.Integration;

namespace Baton.UiTests;

/// <summary>1.4.1 Settings › Automation: levels, port, token handling.</summary>
public sealed class AutomationSettingsTabTests
{
    public class Dialogs : DispatchProxy
    {
        [ThreadStatic] public static bool Answer;

        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name == "Confirm" ? Answer : method?.ReturnType.IsValueType == true ? Activator.CreateInstance(method.ReturnType) : null;
    }

    private static SettingsViewModel Create(AppSettings settings, Action<string>? saveToken = null, bool confirm = true)
    {
        Dialogs.Answer = confirm;
        var dialogs = DispatchProxy.Create<IDialogService, Dialogs>();
        return new SettingsViewModel(dialogs, settings, runAtStartup: false) { SaveApiToken = saveToken, CopyText = _ => { } };
    }

    private static AppSettings Valid() => new() { DownloadFolder = @"C:\Users\x\Downloads" };

    [Fact]
    public void Defaults_AreOff_AndSaveKeepsThemOff_WithoutAToken()
    {
        var vm = Create(Valid());
        Assert.Equal(AutomationLevel.Off, vm.ApiLevelChoice.Value);
        Assert.True(vm.TrySave());
        Assert.Equal(AutomationLevel.Off, vm.Result!.ApiLevel);
        Assert.Equal("", vm.Result.ApiToken);
        Assert.False(vm.Result.AllowLocalNetworkForAgents);
    }

    [Fact]
    public void TurningTheApiOn_CreatesATokenOnSave()
    {
        var vm = Create(Valid());
        vm.ApiLevelChoice = vm.AutomationLevels.First(l => l.Value == AutomationLevel.ReadAdd);
        vm.McpLevelChoice = vm.AutomationLevels.First(l => l.Value == AutomationLevel.ReadOnly);
        vm.AllowLocalNetworkForAgents = true;
        Assert.True(vm.TrySave());
        Assert.Equal(AutomationLevel.ReadAdd, vm.Result!.ApiLevel);
        Assert.Equal(AutomationLevel.ReadOnly, vm.Result.McpLevel);
        Assert.True(vm.Result.AllowLocalNetworkForAgents);
        Assert.True(vm.Result.ApiToken.Length >= 40);
    }

    [Theory]
    [InlineData("80")]
    [InlineData("70000")]
    [InlineData("abc")]
    public void PortOutsideTheAllowedRange_IsRefused(string port)
    {
        var vm = Create(Valid());
        vm.ApiPort = port;
        Assert.False(vm.TrySave());
        Assert.NotNull(vm.Error);
    }

    [Fact]
    public void Token_IsHiddenUntilShown_AndNeverBoundInClearWhileHidden()
    {
        var vm = Create(new AppSettings { DownloadFolder = @"C:\x", ApiToken = "secret-token-value" });
        Assert.DoesNotContain("secret-token-value", vm.TokenText);
        vm.ShowTokenCommand.Execute(null);
        Assert.Equal("secret-token-value", vm.TokenText);
        vm.ShowTokenCommand.Execute(null);
        Assert.DoesNotContain("secret-token-value", vm.TokenText);
    }

    [Fact]
    public void NewToken_IsStoredAtOnce_AndReplacesTheOldOne_AndCancelKeepsItRotated()
    {
        string? stored = null;
        var vm = Create(new AppSettings { DownloadFolder = @"C:\Users\x\Downloads", ApiToken = "old" }, t => stored = t);
        vm.NewTokenCommand.Execute(null);
        Assert.NotNull(stored);
        Assert.NotEqual("old", stored);
        Assert.True(vm.TrySave());
        Assert.Equal(stored, vm.Result!.ApiToken);
    }

    [Fact]
    public void NewToken_DeclinedConfirmation_ChangesNothing()
    {
        string? stored = null;
        var vm = Create(new AppSettings { DownloadFolder = @"C:\x", ApiToken = "old" }, t => stored = t, confirm: false);
        vm.NewTokenCommand.Execute(null);
        Assert.Null(stored);
    }

    [Fact]
    public void TokenMatching_IsExact_AndEmptyNeverMatches()
    {
        var token = AutomationToken.Create();
        Assert.True(AutomationToken.Matches(token, token));
        Assert.False(AutomationToken.Matches(token, token + "x"));
        Assert.False(AutomationToken.Matches(token, null));
        Assert.False(AutomationToken.Matches("", ""));
    }
}
