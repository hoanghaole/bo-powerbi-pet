using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

static class BridgeCore
{
    internal const string ServiceName = "powerbi-bridge-pet";
    internal static readonly HashSet<string> AllowedRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/",
        "/health",
        "/powerbi/processes",
        "/powerbi/listeners",
        "/powerbi/model-summary",
        "/powerbi/dax"
    };

    internal static string AppDir()
    {
        var current = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoBIPet");
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoPowerBIPet");
        if (!Directory.Exists(current) && Directory.Exists(legacy))
        {
            Directory.CreateDirectory(current);
            var legacyToken = Path.Combine(legacy, "token.txt");
            var currentToken = Path.Combine(current, "token.txt");
            if (File.Exists(legacyToken) && !File.Exists(currentToken))
                File.Copy(legacyToken, currentToken);
        }
        Directory.CreateDirectory(current);
        return current;
    }

    internal static string TokenPath() => Path.Combine(AppDir(), "token.txt");

    internal static string LoadOrCreateToken()
    {
        var path = TokenPath();
        if (File.Exists(path))
        {
            var token = File.ReadAllText(path, Encoding.UTF8).Trim();
            if (IsValidToken(token)) return token;
        }

        var created = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        File.WriteAllText(path, created + Environment.NewLine, new UTF8Encoding(false));
        return created;
    }

    internal static bool IsValidToken(string? token)
        => !string.IsNullOrWhiteSpace(token)
           && token.Length == 64
           && token.All(static c => char.IsAsciiHexDigit(c));

    internal static int PickFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    internal static bool IsAuthorized(HttpListenerRequest request, string token)
        => IsAuthorized((WebHeaderCollection)request.Headers, token);

    internal static bool IsAuthorized(WebHeaderCollection headers, string token)
        => string.Equals(headers[HttpRequestHeader.Authorization], $"Bearer {token}", StringComparison.Ordinal);

    internal static bool IsAllowedRoute(string path, string method)
    {
        if (path.Equals("/powerbi/dax", StringComparison.OrdinalIgnoreCase))
            return method.Equals("POST", StringComparison.OrdinalIgnoreCase);

        return AllowedRoutes.Contains(path) && method.Equals("GET", StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task DownloadFileAsync(HttpClient client, string url, string destination, CancellationToken cancellationToken)
    {
        var temp = destination + ".download";
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        ValidateDownloadedExecutable(temp);
        File.Move(temp, destination, true);
    }

    internal static void ValidateDownloadedExecutable(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length < 1_000_000)
            throw new InvalidDataException("cloudflared tải về không hợp lệ");

        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[2];
        if (stream.Read(header) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
            throw new InvalidDataException("cloudflared tải về không phải file thực thi hợp lệ");
    }

    internal static string BuildReleaseAssetUrl(string version, string asset)
        => $"https://github.com/boapps/bo-powerbi-pet/releases/download/{version}/{asset}";

    internal static string Json(object value) => JsonSerializer.Serialize(value);
}
