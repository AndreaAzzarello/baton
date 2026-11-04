using System.IO;
using System.Reflection;
using Baton.App.Services;
using Baton.App.ViewModels;
using Baton.Core;

namespace Baton.UiTests;

/// <summary>1.4.0 folders: one folder or a subfolder per type (wizard, settings tab, add dialog), with honest previews.</summary>
public sealed class SaveInTests
{
    private const string Desktop = @"C:\Users\x\Desktop";

    private static IDialogService Dialogs() => DispatchProxy.Create<IDialogService, MainListCommandsTests.Fake<IDialogService>>();

    /// <summary>The built-in categories as a database returns them: saved, so with an Id.</summary>
    private static List<Category> Saved()
    {
        var all = Categories.Defaults();
        for (var i = 0; i < all.Count; i++)
            all[i].Id = i + 1;
        return all;
    }

    private static AppSettings Settings(bool subfolders) => new() { DownloadFolder = Desktop, CategorySubfolders = subfolders };

    [Fact]
    public void Wizard_ProposesOneFolder_AndPassesTheChoiceOn()
    {
        var shell = DispatchProxy.Create<IShellService, MainListCommandsTests.Fake<IShellService>>();
        var wizard = new FirstRunViewModel(Dialogs(), shell, Saved(), [], AppContext.BaseDirectory, () => null);
        wizard.Folder = Desktop;
        Assert.True(wizard.OneFolder);
        Assert.False(wizard.ShowCategoryFolders);
        Assert.Equal(Path.Combine(Desktop, "file.ext"), wizard.OneFolderPreview);
        Assert.False(wizard.TryGetChoices()!.CategorySubfolders);

        wizard.CategorySubfolders = true;
        Assert.True(wizard.ShowCategoryFolders);
        Assert.Equal(Path.Combine(Desktop, wizard.Categories[0].Folder, "file.ext"), wizard.SubfolderPreview);
        Assert.True(wizard.TryGetChoices()!.CategorySubfolders);
    }

    [Fact]
    public void CategoryEditor_ShowsTheFolderADownloadReallyGets_WithOrWithoutSubfolders()
    {
        var video = Saved().Single(c => c.Key == Categories.VideoKey);
        Assert.Equal(Desktop, new CategoryEditViewModel(video, Desktop, subfolders: false).Folder); // the "Desktop\Video" surprise
        var edit = new CategoryEditViewModel(video, Desktop, subfolders: true);
        Assert.Equal(Path.Combine(Desktop, video.Folder), edit.Folder);

        edit.Follow(Desktop, subfolders: false); // the mode changed in the settings tab
        Assert.Equal(Desktop, edit.Folder);
        Assert.False(edit.IsChanged(Desktop));     // untouched: stays relative, nothing is saved
        Assert.Equal(video.Folder, edit.ToCategory(Desktop).Folder);

        edit.Folder = @"D:\Films";                 // a folder typed by the user is kept and no longer follows the mode
        edit.Follow(Desktop, subfolders: true);
        Assert.Equal(@"D:\Films", edit.Folder);
        Assert.Equal(@"D:\Films", edit.ToCategory(Desktop).Folder);
    }

    [Fact]
    public void SettingsTab_FollowsTheMode_AndSavesOnlyWhatChanged()
    {
        var settings = new SettingsViewModel(Dialogs(), Settings(subfolders: true), runAtStartup: false) { Categories = Saved() };
        var video = settings.CategoryEdits.Single(e => e.Original.Key == Categories.VideoKey);
        settings.SelectedCategory = video;
        Assert.Equal(Path.Combine(Desktop, video.Original.Folder), video.Folder);
        Assert.Contains("example.mp4", settings.SavePreview);
        Assert.Empty(settings.ChangedCategories);

        settings.OneFolder = true;
        Assert.Equal(Desktop, video.Folder);
        Assert.Empty(settings.ChangedCategories);

        video.RememberLast = true;
        Assert.Equal(video.Original.Id, settings.ChangedCategories.Single().Id);
        Assert.True(settings.ChangedCategories.Single().RememberLast);
    }

    [Fact]
    public void AddDialog_OffersTheChoiceOnlyForTypesWithARelativeFolder_AndItAppliesToThatDownloadOnly()
    {
        var categories = Saved();
        var oneFolder = new AddDownloadViewModel(Dialogs(), categories, Settings(subfolders: false), "https://e.test/film.mp4");
        Assert.True(oneFolder.CanChooseSubfolder);
        Assert.True(oneFolder.UseMainFolder);
        Assert.Equal(Desktop, oneFolder.Folder);
        Assert.Null(oneFolder.ToRequest().Folder); // automatic: the service decides

        oneFolder.UseSubfolder = true;
        Assert.Equal(Path.Combine(Desktop, oneFolder.Category.Folder), oneFolder.Folder);
        Assert.Equal(oneFolder.Folder, oneFolder.ToRequest().Folder); // explicit for this download
        Assert.False(oneFolder.FolderChosen);                          // but not remembered as the user's folder

        var general = new AddDownloadViewModel(Dialogs(), categories, Settings(subfolders: true), "https://e.test/unknown.bin");
        Assert.False(general.CanChooseSubfolder); // General has no folder of its own
    }
}
