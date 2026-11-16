using System.Reflection;
using Baton.App.Services;
using Baton.App.ViewModels;
using Baton.Core;
using Baton.Core.Resources;
using Baton.Core.Rules;

namespace Baton.UiTests;

/// <summary>
/// The add dialog and the rules: the queue (and folder, category) it preselects is not a choice, so a rule can set it; one the
/// user changes is. The dialog says which rule applies.
/// </summary>
public sealed class AddDialogRuleTests
{
    private const string Desktop = @"C:\Users\x\Desktop";
    private static readonly DownloadQueue Main = new() { Id = 1, Key = DownloadQueue.MainKey, Name = "Main" };
    private static readonly DownloadQueue Night = new() { Id = 2, Name = "Night" };
    private static readonly DownloadQueue Other = new() { Id = 3, Name = "Other" };

    private static IDialogService Dialogs() => DispatchProxy.Create<IDialogService, MainListCommandsTests.Fake<IDialogService>>();

    private static AddDownloadViewModel Dialog(string url, Func<string, string?, RulePreview>? rules, long? queueId = null) =>
        new(Dialogs(), Categories.Defaults(), new AppSettings { DownloadFolder = Desktop }, url, queues: [Main, Night, Other], queueId: queueId, ruleLookup: rules);

    private static RulePreview Preview(string url, Rule rule, string? folder = null) =>
        url.Contains("example.com") ? new RulePreview(rule, folder, null) : new RulePreview(null, null, null);

    private static Rule ToQueue() => new() { Id = 5, Name = "Example to Night", Conditions = { Domains = ["example.com"] }, Actions = { QueueId = Night.Id } };

    [Fact]
    public void ThePreselectedQueueIsNotExplicit_SoARuleCanSetItFromTheDialog()
    {
        var rule = ToQueue();
        var dialog = Dialog("https://files.example.com/a.zip", (u, _) => Preview(u, rule));

        Assert.Equal(string.Format(Strings.AddRuleApplied, "Example to Night"), dialog.RuleText);
        Assert.Contains("Example to Night", dialog.RuleText);
        Assert.Same(Night, dialog.Queue);                 // the dialog shows what the rule will do
        Assert.Null(dialog.ToRequest().QueueId);          // and leaves the choice to the rule
    }

    [Fact]
    public void AQueuePickedInTheDialogStandsOverTheRule()
    {
        var rule = ToQueue();
        var dialog = Dialog("https://files.example.com/a.zip", (u, _) => Preview(u, rule));

        dialog.Queue = Other;
        Assert.Equal(Other.Id, dialog.ToRequest().QueueId);
        dialog.Url = "https://elsewhere.test/a.zip";       // the rule no longer matches: the pick stays
        Assert.Null(dialog.RuleText);
        Assert.Same(Other, dialog.Queue);
    }

    [Fact]
    public void WithoutAMatchingRuleThePreselectedQueueIsPassedOn_AsBefore()
    {
        var withLookup = Dialog("https://elsewhere.test/a.zip", (u, _) => Preview(u, ToQueue()), queueId: Other.Id);
        Assert.Null(withLookup.RuleText);
        Assert.Equal(Other.Id, withLookup.ToRequest().QueueId);   // the queue being viewed

        var noRules = Dialog("https://files.example.com/a.zip", null);
        Assert.Equal(Main.Id, noRules.ToRequest().QueueId);
    }

    [Fact]
    public void TheQueueFollowsTheRuleWhileTheAddressIsTyped_AndTheFolderOfARuleIsShownButNotForced()
    {
        var rule = new Rule { Id = 6, Name = "Example folder", Conditions = { Domains = ["example.com"] }, Actions = { Folder = @"D:\Fixed", QueueId = Night.Id } };
        var dialog = Dialog("https://elsewhere.test/a.zip", (u, _) => Preview(u, rule, @"D:\Fixed"));
        Assert.Same(Main, dialog.Queue);
        var categoryFolder = dialog.Folder;

        dialog.Url = "https://files.example.com/a.zip";
        Assert.Same(Night, dialog.Queue);
        Assert.Equal(@"D:\Fixed", dialog.Folder);          // shown
        Assert.Null(dialog.ToRequest().Folder);            // not an explicit choice: the rule gives it

        dialog.Url = "https://elsewhere.test/a.zip";
        Assert.Same(Main, dialog.Queue);                   // back to the default
        Assert.Equal(categoryFolder, dialog.Folder);       // and the category's folder again
    }
}
