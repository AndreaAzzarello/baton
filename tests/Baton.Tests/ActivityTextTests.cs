using System.Globalization;
using Baton.Core.Automation;

namespace Baton.Tests;

/// <summary>The activity log keeps stable codes; the words come at display time, in the language of the interface.</summary>
public sealed class ActivityTextTests
{
    private static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    [Fact]
    public void EveryCode_HasItalianWords_ThatDifferFromTheEnglishOnes()
    {
        var missing = new List<string>();
        void Check(string what, string it, string en)
        {
            if (string.IsNullOrWhiteSpace(it) || string.IsNullOrWhiteSpace(en) || it == en)
                missing.Add($"{what}: it='{it}' en='{en}'");
        }
        foreach (var code in ActivityText.AllActions)
            Check("action " + code, ActivityText.Action(code, It), ActivityText.Action(code, En));
        foreach (var code in ActivityText.AllReasons)
            Check("reason " + code, ActivityText.Detail("!" + code, It), ActivityText.Detail("!" + code, En));
        foreach (var code in ActivityText.AllOutcomes)
            Check("outcome " + code, ActivityText.Outcome(code, It), ActivityText.Outcome(code, En));
        Assert.Empty(missing);
    }

    [Fact]
    public void Codes_AreNeverShownRaw()
    {
        foreach (var code in ActivityText.AllActions)
            Assert.DoesNotContain("api.", ActivityText.Action(code, It));
        foreach (var code in ActivityText.AllReasons)
            Assert.DoesNotContain("!", ActivityText.Detail("!" + code, It));
    }

    [Fact]
    public void Detail_KeepsFreeText_AfterTheTranslatedReason()
    {
        Assert.Equal("Duplicato: https://a.test/f.zip", ActivityText.Detail(ActivityText.Reason("duplicate", "https://a.test/f.zip"), It));
        Assert.Equal("Duplicate: https://a.test/f.zip", ActivityText.Detail(ActivityText.Reason("duplicate", "https://a.test/f.zip"), En));
        Assert.Equal("Work files", ActivityText.Detail("Work files", It)); // a rule name carries no code
    }

    [Theory]
    [InlineData("GET", "/api/v1/downloads", "api.list")]
    [InlineData("POST", "/api/v1/downloads", "api.add")]
    [InlineData("DELETE", "/api/v1/downloads/12", "api.remove")]
    [InlineData("POST", "/api/v1/downloads/12/pause", "api.pause")]
    [InlineData("POST", "/api/v1/queues/Main/start", "api.queueStart")]
    [InlineData("GET", "/nothing", "api.request")]
    public void ApiAction_IsAStableCode_WithNoIdsInIt(string method, string path, string expected) =>
        Assert.Equal(expected, ActivityText.ApiAction(method, path));

    [Fact]
    public void Outcome_OfAnHttpStatus_ShowsWordsAndNumber()
    {
        Assert.Equal("Troppe richieste (429)", ActivityText.Outcome("429", It));
        Assert.Equal("Too many requests (429)", ActivityText.Outcome("429", En));
        Assert.Equal("Errore (500)", ActivityText.Outcome("500", It));
    }
}
