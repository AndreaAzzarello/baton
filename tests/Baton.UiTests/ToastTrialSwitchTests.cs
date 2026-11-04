using Baton.App.Services;
using Xunit;

namespace Baton.UiTests;

/// <summary>BATON_TOAST_TRIAL (real notifications and their XML in a file, for the real trials) must never act without BATON_DATA.</summary>
public class ToastTrialSwitchTests
{
    [Theory]
    [InlineData(null, @"C:\t\toasts.xml")]       // a normal or portable copy: no BATON_DATA
    [InlineData("", @"C:\t\toasts.xml")]
    [InlineData(@"C:\t\data", null)]             // isolated, but the switch is off
    [InlineData(@"C:\t\data", "")]
    public void TrialSwitch_IsOff_UnlessBothVariablesAreSet(string? batonData, string? trialLog) =>
        Assert.Null(ToastService.TrialLogPath(batonData, trialLog));

    [Fact]
    public void TrialSwitch_IsOn_WithBatonDataAndTheLogFile() =>
        Assert.Equal(@"C:\t\toasts.xml", ToastService.TrialLogPath(@"C:\t\data", @"C:\t\toasts.xml"));
}
