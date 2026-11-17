using System.Reflection;
using Baton.App.Services;
using Baton.App.ViewModels;
using Baton.Core;
using Baton.Core.Rules;

namespace Baton.UiTests;

/// <summary>1.4.1 rules window: editing, order, validation and the preview.</summary>
public sealed class RulesViewModelTests
{
    public class Dialogs : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.ReturnType.IsValueType == true ? Activator.CreateInstance(method.ReturnType) : null;
    }

    /// <summary>A dialog service that says yes to every confirmation.</summary>
    public class YesDialogs : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.ReturnType == typeof(bool) ? true : method?.ReturnType.IsValueType == true ? Activator.CreateInstance(method.ReturnType) : null;
    }

    private static readonly IReadOnlyList<Category> Categories = Baton.Core.Categories.Defaults();

    private static Rule Unreadable(long id) => new() { Id = id, Name = "From a newer version", Enabled = false, Unreadable = true };

    [Fact]
    public void UnreadableRule_IsOff_NotEditable_KeptAsItIs_AndDeletedOnlyWithConfirmation()
    {
        var vm = Create(Make("Work", "example.com"), Unreadable(7));
        var item = vm.Items[1];
        Assert.True(item.IsUnreadable);
        Assert.False(item.IsEditable);
        Assert.Contains(Baton.Core.Resources.Strings.RuleUnreadable, item.ListLabel);
        item.Name = "tampered";
        item.Enabled = true;
        Assert.True(vm.Validate()); // nothing of it needs fixing
        var saved = vm.Result.Single(r => r.Id == 7);
        Assert.True(saved.Unreadable);
        Assert.False(saved.Enabled);
        Assert.Equal("From a newer version", saved.Name);

        vm.Selected = item;
        vm.DeleteCommand.Execute(null); // the test dialogs answer "no"
        Assert.Equal(2, vm.Items.Count);

        var yes = new RulesViewModel(DispatchProxy.Create<IDialogService, YesDialogs>(), [Make("Work", "example.com"), Unreadable(7)], Categories,
            [new DownloadQueue { Id = 1, Key = DownloadQueue.MainKey, Name = "Main" }], @"C:\Downloads");
        yes.Selected = yes.Items[1];
        yes.DeleteCommand.Execute(null);
        Assert.Single(yes.Items);
    }

    private static RulesViewModel Create(params Rule[] rules) =>
        new(DispatchProxy.Create<IDialogService, Dialogs>(), rules, Categories, [new DownloadQueue { Id = 1, Key = DownloadQueue.MainKey, Name = "Main" }], @"C:\Downloads");

    private static Rule Make(string name, string domain) => new() { Id = 0, Name = name, Conditions = { Domains = [domain] } };

    [Fact]
    public void Edited_Rule_RoundTrips_WithItsConditionsAndActions()
    {
        using var _ = TestCulture.Use("it-IT"); // "1,5" is a number only where the comma is the decimal separator
        var vm = Create(Make("Work", "example.com"));
        var edit = vm.Items[0];
        edit.Extensions = ".PDF docx";
        edit.MinSize = "1,5";
        edit.Rename = "{date}-{name}";
        edit.Connections = "99";
        edit.OpenWhenDone = true;
        edit.Origins.First(o => o.Id == RuleOrigins.Api).IsChecked = true;

        Assert.True(vm.Validate());
        var rule = vm.Result.Single();
        Assert.Equal(["PDF", "docx"], rule.Conditions.Extensions);
        Assert.Equal((long)(1.5 * 1024 * 1024), rule.Conditions.MinSize);
        Assert.Equal([RuleOrigins.Api], rule.Conditions.Origins);
        Assert.Equal("{date}-{name}", rule.Actions.Rename);
        Assert.Equal(32, rule.Actions.Connections); // clamped
        Assert.True(rule.Actions.OpenWhenDone);
    }

    [Fact]
    public void Validation_RefusesAnEmptyName_NoCondition_AndABadRegex()
    {
        var vm = Create(Make("Ok", "example.com"));
        vm.Items[0].Name = " ";
        Assert.False(vm.Validate());
        Assert.NotNull(vm.Error);

        vm.Items[0].Name = "Ok";
        vm.Items[0].Domains = "";
        Assert.False(vm.Validate());
        Assert.Contains("Ok", vm.Error);

        vm.Items[0].UrlRegex = "([";
        Assert.False(vm.Validate());

        vm.Items[0].UrlRegex = @"\.zip$";
        Assert.True(vm.Validate());
        Assert.Null(vm.Error);
    }

    [Fact]
    public void Validation_RefusesARelativeFolderAndANumberThatIsNotOne()
    {
        var vm = Create(Make("Ok", "example.com"));
        vm.Items[0].Folder = @"relative\folder";
        Assert.False(vm.Validate());
        vm.Items[0].Folder = @"C:\Downloads\Work";
        vm.Items[0].SpeedLimit = "fast";
        Assert.False(vm.Validate());
        vm.Items[0].SpeedLimit = "250";
        Assert.True(vm.Validate());
    }

    [Fact]
    public void Rules_CanBeReordered_AndDeleted()
    {
        var vm = Create(Make("a", "a.test"), Make("b", "b.test"), Make("c", "c.test"));
        vm.Selected = vm.Items[2];
        vm.MoveUpCommand.Execute(null);
        Assert.Equal(["a", "c", "b"], vm.Result.Select(r => r.Name));
        vm.Selected = vm.Items[0];
        Assert.False(vm.MoveUpCommand.CanExecute(null));
        vm.DeleteCommand.Execute(null);
        Assert.Equal(["c", "b"], vm.Result.Select(r => r.Name));
        Assert.Equal("c", vm.Selected!.Name);
    }

    [Fact]
    public void Preview_NamesTheFirstRuleThatMatches_UsingTheRulesAsEdited()
    {
        var vm = Create(Make("first", "example.com"), Make("second", "example.com"));
        vm.PreviewUrl = "https://cdn.example.com/a.zip";
        Assert.Contains("first", vm.PreviewText);

        vm.Items[0].Enabled = false; // an edit not saved yet already counts
        Assert.Contains("second", vm.PreviewText);

        vm.Items[1].Rename = "x-{name}";
        Assert.Contains("x-a.zip", vm.PreviewText);

        vm.PreviewUrl = "https://other.test/a.zip";
        Assert.Equal(Baton.Core.Resources.Strings.RulePreviewNone, vm.PreviewText);
        vm.PreviewUrl = "";
        Assert.Equal("", vm.PreviewText);
    }
}
