using System.Net;

static class ContractTests
{
    static int Main()
    {
        Assert(BridgeCore.IsValidToken(new string('a', 64)), "token 64 hex hợp lệ");
        Assert(!BridgeCore.IsValidToken("abc"), "token ngắn bị từ chối");
        Assert(BridgeCore.IsAllowedRoute("/health", "GET"), "GET /health hợp lệ");
        Assert(!BridgeCore.IsAllowedRoute("/powerbi/powershell", "POST"), "powershell route bị cấm");
        Assert(BridgeCore.BuildReleaseAssetUrl("v1.2.3", "BoPowerBIPet-win-x64.zip") == "https://github.com/boapps/bo-powerbi-pet/releases/download/v1.2.3/BoPowerBIPet-win-x64.zip", "release URL đúng");

        var headers = new WebHeaderCollection
        {
            [HttpRequestHeader.Authorization] = "Bearer " + new string('b', 64)
        };
        Assert(BridgeCore.IsAuthorized(headers, new string('b', 64)), "Authorization đúng");
        Assert(!BridgeCore.IsAuthorized(new WebHeaderCollection(), new string('b', 64)), "Thiếu Authorization bị từ chối");
        return 0;
    }

    static void Assert(bool ok, string message)
    {
        if (!ok) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
