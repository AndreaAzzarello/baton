using System.Globalization;
using System.IO;
using System.Windows.Controls;
using System.Windows.Markup;
using Baton.App;
using Baton.App.Mvvm;
using Baton.App.Services;
using Baton.Core;
using Baton.Core.Resources;

// The interface language is process-wide (like in the app): tests that set it must not overlap.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Baton.UiTests;

/// <summary>Texts, dates and sizes in English and Italian, and a change of language while windows are open.</summary>
public sealed class LocalizationTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    private static void Sta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ex; }
            finally { Localizer.Apply(AppLanguage.Italian); } // what the other UI tests expect
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Theory]
    [InlineData(AppLanguage.Italian, "it-IT", "en-US", "it-IT")]
    [InlineData(AppLanguage.English, "it-IT", "it-IT", "en-US")]
    [InlineData(AppLanguage.Automatic, "it-IT", "it-IT", "it-IT")]
    [InlineData(AppLanguage.Automatic, "it-IT", "it-CH", "it-CH")] // Swiss Italian formats stay
    [InlineData(AppLanguage.Automatic, "de-DE", "de-DE", "en-US")] // any other language: English
    [InlineData(AppLanguage.Automatic, "en-GB", "en-GB", "en-GB")]
    [InlineData(AppLanguage.Italian, "en-GB", "en-GB", "it-IT")]
    [InlineData(AppLanguage.English, "it-IT", "en-AU", "en-AU")]
    public void Language_FollowsTheChoiceOrWindows(AppLanguage choice, string windowsLanguage, string windowsFormat, string expected) =>
        Assert.Equal(expected, UiLanguage.Resolve(choice, CultureInfo.GetCultureInfo(windowsLanguage), CultureInfo.GetCultureInfo(windowsFormat)).Name);

    [Theory]
    [InlineData("Automatic", AppLanguage.Automatic)]
    [InlineData("Italian", AppLanguage.Italian)]
    [InlineData("English", AppLanguage.English)]
    [InlineData("7", AppLanguage.Automatic)]
    [InlineData("", AppLanguage.Automatic)]
    [InlineData(null, AppLanguage.Automatic)]
    public void StoredLanguage_UnknownMeansAutomatic(string? stored, AppLanguage expected) => Assert.Equal(expected, UiLanguage.Parse(stored));

    [Fact]
    public void BothLanguages_HaveTheSameKeys_AndPlaceholders()
    {
        var english = Strings.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, true, false)!
            .Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        var italian = Strings.ResourceManager.GetResourceSet(CultureInfo.GetCultureInfo("it"), true, false)!
            .Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        Assert.True(english.Count > 800);
        Assert.Equal(english.Keys.Order(), italian.Keys.Order());
        static string Holes(string text) => string.Join(",", System.Text.RegularExpressions.Regex.Matches(text, @"\{(\d+)").Select(m => m.Groups[1].Value).Distinct().Order());
        var mismatched = english.Keys.Where(k => english[k].Length == 0 || italian[k].Length == 0 || Holes(english[k]) != Holes(italian[k])).ToList();
        Assert.True(mismatched.Count == 0, "empty or different placeholders: " + string.Join(", ", mismatched));
        Assert.Equal("Add", english["ActionAdd"]);
        Assert.Equal("Aggiungi", italian["ActionAdd"]);
    }

    [Fact]
    public void Sizes_UseTheDecimalSeparatorOfTheLanguage() => Sta(() =>
    {
        Localizer.Apply(AppLanguage.English);
        Assert.Equal("1.2 GB", Format.Bytes(1.2 * 1024 * 1024 * 1024));
        Assert.Equal("512 KB", Format.Bytes(512 * 1024));
        Assert.Equal("1.5 MB/s", Format.Speed(1.5 * 1024 * 1024));
        Localizer.Apply(AppLanguage.Italian);
        Assert.Equal("1,2 GB", Format.Bytes(1.2 * 1024 * 1024 * 1024));
        Assert.Equal("1,5 MB/s", Format.Speed(1.5 * 1024 * 1024));
    });

    [Fact]
    public void Dates_FollowTheLanguage()
    {
        var wednesday = new DateTime(2026, 10, 21, 22, 30, 0);
        Assert.Equal("Scheduled: Wed 22:30", string.Format(English, Strings.ResourceManager.GetString("QueueStateScheduled", English)!, wednesday));
        Assert.Equal("Pianificata: mer 22:30", string.Format(Italian, Strings.ResourceManager.GetString("QueueStateScheduled", Italian)!, wednesday));
        Assert.Equal("Next start: Wednesday, October 21, 22:30",
            string.Format(English, Strings.ResourceManager.GetString("QueueNextStart", English)!, wednesday));
        Assert.Equal("Prossimo avvio: mercoledì 21 ottobre, 22:30",
            string.Format(Italian, Strings.ResourceManager.GetString("QueueNextStart", Italian)!, wednesday));
        Assert.Equal("from 10/21/2026 to 12/31/2026",
            string.Format(English, Strings.ResourceManager.GetString("CertificateValidityRange", English)!, wednesday, new DateTime(2026, 12, 31)));
        Assert.Equal("dal 21/10/2026 al 31/12/2026",
            string.Format(Italian, Strings.ResourceManager.GetString("CertificateValidityRange", Italian)!, wednesday, new DateTime(2026, 12, 31)));
    }

    private sealed class Probe : ObservableObject
    {
        public string Text => Strings.ActionAdd;
    }

    [Fact]
    public void ChangingTheLanguage_UpdatesXamlTexts_MenusBuiltInCode_AndViewModels() => Sta(() =>
    {
        Localizer.Apply(AppLanguage.Italian);
        var block = (TextBlock)XamlReader.Parse("""
            <TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                       xmlns:l="clr-namespace:Baton.App.Services;assembly=Baton" Text="{l:Loc ActionSettings}" />
            """);
        var item = new MenuItem().Text(nameof(Strings.MenuOpenFolder));
        var probe = new Probe();
        var refreshed = new List<string?>();
        probe.PropertyChanged += (_, e) => refreshed.Add(e.PropertyName);
        Assert.Equal("Impostazioni", block.Text);
        Assert.Equal("Apri cartella", item.Header);
        Assert.Equal("Aggiungi", probe.Text);

        Assert.True(Localizer.Apply(AppLanguage.English));
        Assert.Equal("Settings", block.Text);
        Assert.Equal("Open folder", item.Header);
        Assert.Equal("Add", probe.Text);
        Assert.Contains(string.Empty, refreshed); // every binding of the view model is read again
        Assert.Equal("en-US", CultureInfo.CurrentCulture.Name);

        Assert.False(Localizer.Apply(AppLanguage.English)); // nothing to do
        GC.KeepAlive(probe);
    });

    [Fact]
    public void TextsComputedForTheWizardAndSettings_FollowALiveLanguageChange() => Sta(() =>
    {
        Localizer.Apply(AppLanguage.Italian);
        var program = Path.Combine(Path.GetTempPath(), "dm-tests", "lang-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(program, "extension"));
        File.WriteAllText(Path.Combine(program, "extension", "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(program, "baton-firefox.xpi"), "");
        try
        {
            var shell = System.Reflection.DispatchProxy.Create<IShellService, MainListCommandsTests.Fake<IShellService>>();
            var extension = new Baton.App.ViewModels.BundledExtensionViewModel(program, shell);
            var startup = new Baton.App.ViewModels.StartWithWindowsViewModel(Path.Combine(program, "Baton.exe"), enabled: true);
            var refreshed = new List<object>();
            extension.PropertyChanged += (sender, e) => { if (e.PropertyName == string.Empty) refreshed.Add(sender!); };
            startup.PropertyChanged += (sender, e) => { if (e.PropertyName == string.Empty) refreshed.Add(sender!); };
            var before = (extension.FirefoxText, startup.PortableWarning);

            Assert.True(Localizer.Apply(AppLanguage.English));
            Assert.Equal([extension, startup], refreshed); // their bindings are read again
            Assert.NotEqual(before.FirefoxText, extension.FirefoxText);
            Assert.NotEqual(before.PortableWarning, startup.PortableWarning);
            Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.ResourceManager.GetString(nameof(Strings.ExtensionFirefoxIncluded), English)!,
                Path.Combine(program, "baton-firefox.xpi")), extension.FirefoxText);
        }
        finally
        {
            Directory.Delete(program, recursive: true);
        }
    });

    [Fact]
    public void BrowserHostErrors_UseTheLanguageChosenInTheApp_NotThatOfWindows() => Sta(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "dm-tests", "host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var db = Path.Combine(folder, "downloads.db");
        try
        {
            foreach (var (choice, expected) in new[] { (AppLanguage.English, "Baton did not answer in time."), (AppLanguage.Italian, "Baton non ha risposto in tempo.") })
            {
                using (var store = new DownloadStore(db))
                    store.SaveSettings(new AppSettings { Language = choice });
                Localizer.UseSavedLanguage(db);
                Assert.Equal(expected, Strings.ErrorAppNoAnswer);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    });

    [Fact]
    public void Editors_KeepTheStoredBuiltInName_WhenItIsOnlyShownInAnotherLanguage() => Sta(() =>
    {
        Localizer.Apply(AppLanguage.English);
        var music = new Category { Id = 3, Key = Categories.MusicKey, Name = "Musica", Folder = "Musica", Extensions = ["mp3"] };
        var edit = new Baton.App.ViewModels.CategoryEditViewModel(music, @"C:\Downloads");
        Assert.Equal("Music", edit.Name);
        Assert.Equal("Musica", edit.ToCategory(@"C:\Downloads").Name);
        Assert.False(edit.IsChanged(@"C:\Downloads"));
        edit.Name = "Songs";
        Assert.Equal("Songs", edit.ToCategory(@"C:\Downloads").Name);

        var queue = new Baton.App.ViewModels.QueueEditViewModel(new DownloadQueue { Id = 1, Key = DownloadQueue.MainKey, Name = "Coda principale" }, () => DateTime.UtcNow);
        Assert.Equal("Main queue", queue.Name);
        Assert.Null(queue.TryBuild(out var built));
        Assert.Equal("Coda principale", built.Name);
    });

    [Fact]
    public void Settings_DoNotAnswerThePortableHostQuestion_ForTheUser() => Sta(() =>
    {
        var dialogs = System.Reflection.DispatchProxy.Create<Baton.App.Services.IDialogService, MainListCommandsTests.Fake<Baton.App.Services.IDialogService>>();
        var settings = new AppSettings { PortableBrowserHost = null };
        var untouched = new Baton.App.ViewModels.SettingsViewModel(dialogs, settings, runAtStartup: false, portableProgram: @"X:\Baton\Baton.exe");
        Assert.True(untouched.TrySave());
        Assert.Null(untouched.Result!.PortableBrowserHost); // asked again at the next start
        var ticked = new Baton.App.ViewModels.SettingsViewModel(dialogs, settings, runAtStartup: false, portableProgram: @"X:\Baton\Baton.exe") { PortableBrowserHost = true };
        Assert.True(ticked.TrySave());
        Assert.True(ticked.Result!.PortableBrowserHost);
    });

    [Fact]
    public void BuiltInNames_FollowTheLanguage_UntilTheUserRenamesThem() => Sta(() =>
    {
        var music = new Category { Key = Categories.MusicKey, Name = "Musica" };
        var renamed = new Category { Key = Categories.MusicKey, Name = "Brani" };
        var main = new DownloadQueue { Key = DownloadQueue.MainKey, Name = "Coda principale" };
        Localizer.Apply(AppLanguage.English);
        Assert.Equal("Music", music.DisplayName);
        Assert.Equal("Brani", renamed.DisplayName);
        Assert.Equal("Main queue", main.DisplayName);
        Localizer.Apply(AppLanguage.Italian);
        Assert.Equal("Musica", new Category { Key = Categories.MusicKey, Name = "Music" }.DisplayName);
        Assert.Equal("Coda principale", main.DisplayName);
    });
}
