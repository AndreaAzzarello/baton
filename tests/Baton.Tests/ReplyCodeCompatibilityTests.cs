using System.Text.Json;
using Baton.Core.Integration;

namespace Baton.Tests;

/// <summary>
/// <c>batonctl.exe</c> travels in the extension package and can be older than <c>Baton.exe</c>: a refusal with a code it does not know
/// must still be read as a refusal (Failed with the app's own words), never as "the connection broke" (which invites a second try).
/// </summary>
public sealed class ReplyCodeCompatibilityTests
{
    [Fact]
    public void AnUnknownCode_IsReadAs_Failed_AndTheRestOfTheReplyIsKept()
    {
        var reply = JsonSerializer.Deserialize("""{"ok":false,"error":"Nope.","code":"aCodeFromTheFuture","protocol":9}""",
            ProtocolJson.Default.ChannelReply);
        Assert.NotNull(reply);
        Assert.False(reply!.Ok);
        Assert.Equal(ReplyCode.Failed, reply.Code);
        Assert.Equal("Nope.", reply.Error);
    }

    [Theory]
    [InlineData(ReplyCode.NoDownload, "noDownload")]
    [InlineData(ReplyCode.TooManyPending, "tooManyPending")]
    [InlineData(ReplyCode.Busy, "busy")]
    public void EveryKnownCode_KeepsItsStableWireWord(ReplyCode code, string word)
    {
        var json = JsonSerializer.Serialize(new ChannelReply(false, Code: code), ProtocolJson.Default.ChannelReply);
        Assert.Contains($"\"code\":\"{word}\"", json);
        Assert.Equal(code, JsonSerializer.Deserialize(json, ProtocolJson.Default.ChannelReply)!.Code);
    }

    [Fact]
    public void EveryCode_RoundTrips()
    {
        foreach (var code in Enum.GetValues<ReplyCode>())
        {
            var json = JsonSerializer.Serialize(new ChannelReply(false, Code: code), ProtocolJson.Default.ChannelReply);
            Assert.Equal(code, JsonSerializer.Deserialize(json, ProtocolJson.Default.ChannelReply)!.Code);
        }
    }

    [Fact]
    public void TheCliText_ForAnUnknownCode_IsTheGenericOne()
    {
        var reply = JsonSerializer.Deserialize("""{"ok":false,"error":"Nope.","code":"aCodeFromTheFuture"}""", ProtocolJson.Default.ChannelReply)!;
        Assert.Equal(ReplyCode.Failed, reply.Code);
    }
}
