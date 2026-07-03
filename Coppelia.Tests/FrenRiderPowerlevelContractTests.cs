using System.Text.Json;
using Coppelia.Models;

namespace Coppelia.Tests;

public sealed class FrenRiderPowerlevelContractTests
{
    [Fact]
    public void StatusJsonReportsCompatibilityAndFrenState()
    {
        var json = """
                   {
                     "contractVersion": 1,
                     "leaseActive": true,
                     "leaseReason": "active",
                     "frenRiderEnabled": true,
                     "configuredFrenName": "Leader",
                     "visibleFrenName": "Leader",
                     "visibleFrenObjectId": 123,
                     "companionActive": false,
                     "companionName": "",
                     "companionObjectId": 0
                   }
                   """;

        var status = JsonSerializer.Deserialize<FrenRiderPowerlevelStatus>(
            json,
            FrenRiderPowerlevelContract.JsonOptions);

        Assert.NotNull(status);
        Assert.True(status!.IsCompatible);
        Assert.True(status.FrenConfigured);
        Assert.True(status.FrenVisible);
    }

    [Fact]
    public void ResponseJsonPreservesFailureReason()
    {
        var json = """{"ok":false,"reason":"owned","contractVersion":1}""";

        var response = JsonSerializer.Deserialize<FrenRiderPowerlevelResponse>(
            json,
            FrenRiderPowerlevelContract.JsonOptions);

        Assert.NotNull(response);
        Assert.False(response!.Ok);
        Assert.Equal("owned", response.Reason);
    }
}
