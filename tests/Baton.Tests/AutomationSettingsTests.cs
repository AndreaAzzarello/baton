using Baton.Core;
using Baton.Core.Integration;
using Baton.Core.Portable;
using Microsoft.Data.Sqlite;

namespace Baton.Tests;

/// <summary>The settings of the local API and MCP: off by default, the token never in clear, nothing of it in an export.</summary>
public sealed class AutomationSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-tests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "downloads.db");

    public AutomationSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Defaults_AreOff_OnThePortOfTheRoadmap_WithTheLocalNetworkRefused()
    {
        var settings = new AppSettings();
        Assert.Equal(AutomationLevel.Off, settings.ApiLevel);
        Assert.Equal(AutomationLevel.Off, settings.McpLevel);
        Assert.Equal(7745, settings.ApiPort);
        Assert.Equal("", settings.ApiToken);
        Assert.False(settings.AllowLocalNetworkForAgents);
    }

    [Fact]
    public void TheSettings_RoundTrip_AndTheTokenIsStoredEncrypted()
    {
        const string token = "token-that-must-not-appear-in-clear-0123456789";
        using (var store = new DownloadStore(DbPath))
        {
            store.SaveSettings(new AppSettings
            {
                ApiLevel = AutomationLevel.ReadAdd, McpLevel = AutomationLevel.ReadOnly, ApiPort = 8123, ApiToken = token,
                AllowLocalNetworkForAgents = true,
            });
            var loaded = store.LoadSettings();
            Assert.Equal(AutomationLevel.ReadAdd, loaded.ApiLevel);
            Assert.Equal(AutomationLevel.ReadOnly, loaded.McpLevel);
            Assert.Equal(8123, loaded.ApiPort);
            Assert.Equal(token, loaded.ApiToken);
            Assert.True(loaded.AllowLocalNetworkForAgents);
        }

        using var db = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = 'ApiToken';";
        var stored = (string)cmd.ExecuteScalar()!;
        Assert.NotEmpty(stored);
        Assert.DoesNotContain(token, stored);
    }

    [Fact]
    public void ThePort_IsKeptInTheUnprivilegedRange()
    {
        using var store = new DownloadStore(DbPath);
        store.SaveSettings(new AppSettings { ApiPort = 80 });
        Assert.Equal(1024, store.LoadSettings().ApiPort);
        store.SaveSettings(new AppSettings { ApiPort = 70000 });
        Assert.Equal(65535, store.LoadSettings().ApiPort);
    }

    [Fact]
    public void NothingOfTheAutomationSettings_IsPartOfAnExport()
    {
        var names = typeof(ExportedSettings).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("Api", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Mcp", StringComparison.OrdinalIgnoreCase) || n.Contains("Token", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Automation", StringComparison.OrdinalIgnoreCase) || n.Contains("LocalNetwork", StringComparison.OrdinalIgnoreCase));
    }
}
