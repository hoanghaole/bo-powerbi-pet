using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new PetForm());
    }
}

sealed class PetForm : Form
{
    readonly RichTextBox log = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.FromArgb(18, 24, 28), ForeColor = Color.FromArgb(210, 235, 220), Font = new Font("Consolas", 10), WordWrap = false };
    readonly Label status = new() { Dock = DockStyle.Top, Height = 88, Font = new Font("Segoe UI", 11), Padding = new Padding(12), Text = "Đang khởi động…" };
    readonly Button copy = new() { Dock = DockStyle.Bottom, Height = 38, Text = "Copy URL + Token" };
    readonly CancellationTokenSource stop = new();
    readonly string token = BridgeCore.LoadOrCreateToken();
    readonly int port = BridgeCore.PickFreePort();
    HttpListener? server;
    Process? tunnel;
    string publicUrl = "";

    public PetForm()
    {
        Text = "BoBIPet — One Click Bridge";
        Width = 900;
        Height = 560;
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(log);
        Controls.Add(status);
        Controls.Add(copy);
        copy.Click += (_, _) => CopyAccess();
        Shown += async (_, _) => await StartAll();
        FormClosing += (_, _) =>
        {
            stop.Cancel();
            try { server?.Stop(); } catch { }
            try { if (tunnel is { HasExited: false }) tunnel.Kill(true); } catch { }
        };
    }

    void Add(string s)
    {
        if (IsDisposed) return;
        Action a = () =>
        {
            log.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\r\n");
            log.SelectionStart = log.TextLength;
            log.ScrollToCaret();
        };
        if (InvokeRequired) BeginInvoke(a); else a();
    }

    void State(string s)
    {
        if (InvokeRequired) BeginInvoke(() => status.Text = s); else status.Text = s;
    }

    void CopyAccess()
    {
        if (string.IsNullOrWhiteSpace(publicUrl))
        {
            MessageBox.Show("Tunnel chưa sẵn sàng.");
            return;
        }
        Clipboard.SetText($"{publicUrl}\r\n{token}");
        State($"Sẵn sàng\n{publicUrl}\nĐã copy URL + token\nLocal: http://localhost:{port}");
    }

    async Task StartAll()
    {
        try
        {
            StartBridge();
            Add($"Bridge: http://localhost:{port}");
            Add($"Token: {token}");
            Add("Endpoint: GET /health, /powerbi/processes, /powerbi/listeners, /powerbi/model-summary; POST /powerbi/dax");
            string cf = await EnsureCloudflared();
            StartTunnel(cf);
            State($"Bridge chạy tại localhost:{port}. Đang lấy URL Cloudflare…");
        }
        catch (Exception ex)
        {
            Add("LỖI: " + ex.Message);
            State("Lỗi khởi động — xem log");
        }
    }

    void StartBridge()
    {
        server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Prefixes.Add($"http://localhost:{port}/");
        server.Start();
        _ = Task.Run(Listen);
    }

    async Task Listen()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext c;
            try { c = await server!.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(c));
        }
    }

    async Task Handle(HttpListenerContext c)
    {
        try
        {
            c.Response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type";
            c.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            c.Response.ContentType = "application/json; charset=utf-8";
            if (c.Request.HttpMethod == "OPTIONS")
            {
                c.Response.StatusCode = 204;
                c.Response.Close();
                return;
            }
            if (!BridgeCore.IsAuthorized(c.Request, token))
            {
                await Json(c, 401, new { ok = false, error = "unauthorized" });
                return;
            }

            string p = c.Request.Url!.AbsolutePath;
            if (p == "/" || p == "/health")
            {
                await Json(c, 200, new { ok = true, service = BridgeCore.ServiceName, time = DateTime.UtcNow, platform = "win32", auth = "bearer", port });
                return;
            }
            if (p == "/powerbi/processes") { await PsJson(c, Scripts.Processes); return; }
            if (p == "/powerbi/listeners") { await PsJson(c, Scripts.Listeners, 30000); return; }
            if (p == "/powerbi/model-summary") { await PsRawJson(c, Scripts.ModelSummary, 120000); return; }
            if (p == "/powerbi/dax" && c.Request.HttpMethod == "POST")
            {
                var body = await ReadBody(c);
                var q = JsonDocument.Parse(body).RootElement.GetProperty("query").GetString() ?? "";
                await PsRawJson(c, Scripts.Dax(q), 120000);
                return;
            }
            await Json(c, 404, new { ok = false, error = "not_found" });
        }
        catch (Exception ex)
        {
            try { await Json(c, 500, new { ok = false, error = ex.Message }); } catch { }
        }
    }

    static async Task<string> ReadBody(HttpListenerContext c)
    {
        using var r = new StreamReader(c.Request.InputStream, c.Request.ContentEncoding);
        return await r.ReadToEndAsync();
    }

    static async Task Json(HttpListenerContext c, int code, object x)
    {
        byte[] b = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(x));
        c.Response.StatusCode = code;
        c.Response.ContentLength64 = b.Length;
        await c.Response.OutputStream.WriteAsync(b);
        c.Response.Close();
    }

    async Task PsJson(HttpListenerContext c, string script, int timeout = 15000)
    {
        var r = await RunPS(script, timeout);
        if (!r.ok) { await Json(c, 500, r); return; }
        object data;
        try { data = JsonSerializer.Deserialize<object>(string.IsNullOrWhiteSpace(r.stdout) ? "[]" : r.stdout)!; }
        catch { data = new { raw = r.stdout, stderr = r.stderr }; }
        await Json(c, 200, new { ok = true, data });
    }

    async Task PsRawJson(HttpListenerContext c, string script, int timeout)
    {
        var r = await RunPS(script, timeout);
        if (!r.ok) { await Json(c, 500, r); return; }
        try
        {
            using var d = JsonDocument.Parse(r.stdout);
            await Json(c, 200, d.RootElement.Clone());
        }
        catch { await Json(c, 200, new { ok = true, raw = r.stdout, stderr = r.stderr }); }
    }

    async Task<(bool ok, string stdout, string stderr, string? error)> RunPS(string script, int timeout)
    {
        string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[Text.Encoding]::UTF8;$OutputEncoding=[Text.Encoding]::UTF8;" + script));
        var pi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {b64}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        using var p = Process.Start(pi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        using var ct = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(ct.Token); }
        catch
        {
            try { p.Kill(true); } catch { }
            return (false, await o, await e, "timeout");
        }
        return (p.ExitCode == 0, (await o).Trim(), (await e).Trim(), p.ExitCode == 0 ? null : $"PowerShell exit {p.ExitCode}");
    }

    async Task<string> EnsureCloudflared()
    {
        string dir = BridgeCore.AppDir();
        string exe = Path.Combine(dir, "cloudflared.exe");
        if (File.Exists(exe)) return exe;

        State("Đang tải Cloudflare Tunnel lần đầu…");
        Add("Tải cloudflared.exe…");
        using var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("BoBIPet/3.0");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        cts.CancelAfter(TimeSpan.FromMinutes(3));
        await BridgeCore.DownloadFileAsync(h, "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe", exe, cts.Token);
        return exe;
    }

    void StartTunnel(string exe)
    {
        var pi = new ProcessStartInfo(exe, $"tunnel --url http://localhost:{port} --http-host-header localhost:{port} --no-autoupdate")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        tunnel = Process.Start(pi)!;
        tunnel.OutputDataReceived += Line;
        tunnel.ErrorDataReceived += Line;
        tunnel.BeginOutputReadLine();
        tunnel.BeginErrorReadLine();
    }

    void Line(object? s, DataReceivedEventArgs e)
    {
        if (e.Data == null) return;
        Add("Tunnel: " + e.Data);
        var m = Regex.Match(e.Data, @"https://[a-z0-9-]+\.trycloudflare\.com");
        if (m.Success)
        {
            publicUrl = m.Value;
            State($"Sẵn sàng\n{publicUrl}\nBấm nút dưới để copy URL + token\nLocal: http://localhost:{port}");
            BeginInvoke(() => copy.BackColor = Color.LightGreen);
        }
    }
}

static class Scripts
{
    internal const string Processes = @"$items=Get-Process|?{$_.ProcessName -match 'PBIDesktop|Microsoft.Mashup.Container|msmdsrv'}|select ProcessName,Id,MainWindowTitle,Path,StartTime;$items|ConvertTo-Json -Depth 4 -Compress";
    internal const string Listeners = @"$items=Get-NetTCPConnection -State Listen|%{$p=Get-Process -Id $_.OwningProcess -EA SilentlyContinue;if($p -and $p.ProcessName -match 'msmdsrv|PBIDesktop'){[pscustomobject]@{Process=$p.ProcessName;PID=$p.Id;LocalAddress=$_.LocalAddress;LocalPort=$_.LocalPort}}};$items|ConvertTo-Json -Depth 4 -Compress";
    internal const string ModelSummary = @"$listener=Get-NetTCPConnection -State Listen|%{$p=Get-Process -Id $_.OwningProcess -EA SilentlyContinue;if($p -and $p.ProcessName -eq 'msmdsrv'){[pscustomobject]@{Port=$_.LocalPort;PID=$p.Id}}}|select -First 1;if(-not $listener){@{ok=$false;error='msmdsrv_listener_not_found'}|ConvertTo-Json -Compress;exit};$bin=Split-Path (Get-Process PBIDesktop|select -First 1 -Expand Path) -Parent;Add-Type -Path (Join-Path $bin 'Microsoft.PowerBI.AdomdClient.dll');$c=New-Object Microsoft.AnalysisServices.AdomdClient.AdomdConnection(""Data Source=localhost:$($listener.Port)"");$c.Open();function Q($q){$x=$c.CreateCommand();$x.CommandText=$q;$r=$x.ExecuteReader();$a=@();while($r.Read()){$o=[ordered]@{};0..($r.FieldCount-1)|%{$v=$r.GetValue($_);if($v-is[DBNull]){$v=$null};$o[$r.GetName($_)]=$v};$a+=[pscustomobject]$o};$r.Close();$a};$ts=Q 'SELECT * FROM $SYSTEM.TMSCHEMA_TABLES';$cs=Q 'SELECT * FROM $SYSTEM.TMSCHEMA_COLUMNS';$ms=Q 'SELECT * FROM $SYSTEM.TMSCHEMA_MEASURES';$rs=Q 'SELECT * FROM $SYSTEM.TMSCHEMA_RELATIONSHIPS';$ps=Q 'SELECT * FROM $SYSTEM.TMSCHEMA_PARTITIONS';$tb=@{};$ts|%{$tb[[string]$_.ID]=$_.Name};$cb=@{};$cs|%{$n=$_.ExplicitName;if(!$n){$n=$_.InferredName};$cb[[string]$_.ID]=[pscustomobject]@{name=$n;table=$tb[[string]$_.TableID]}};$tc=$ts|%{$t=$_;[pscustomobject]@{id=$t.ID;name=$t.Name;hidden=$t.IsHidden;columns=@($cs|?{$_.TableID-eq$t.ID}|%{$n=$_.ExplicitName;if(!$n){$n=$_.InferredName};[pscustomobject]@{name=$n;dataType=$_.DataType;hidden=$_.IsHidden;key=$_.IsKey}});measures=@($ms|?{$_.TableID-eq$t.ID}|%{[pscustomobject]@{name=$_.Name;expression=$_.Expression;format=$_.FormatString;hidden=$_.IsHidden;folder=$_.DisplayFolder}});partitionCount=@($ps|?{$_.TableID-eq$t.ID}).Count}};$rc=$rs|%{$f=$cb[[string]$_.FromColumnID];$t=$cb[[string]$_.ToColumnID];[pscustomobject]@{name=$_.Name;active=$_.IsActive;from=""$($f.table)[$($f.name)]"";to=""$($t.table)[$($t.name)]"";crossFiltering=$_.CrossFilteringBehavior}};$c.Close();[ordered]@{ok=$true;port=$listener.Port;counts=[ordered]@{tables=@($ts).Count;columns=@($cs).Count;measures=@($ms).Count;relationships=@($rs).Count;partitions=@($ps).Count};tables=$tc;relationships=$rc}|ConvertTo-Json -Depth 10 -Compress";
    internal static string Dax(string q)
    {
        string q64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(q));
        return $@"$l=Get-NetTCPConnection -State Listen|%{{$p=Get-Process -Id $_.OwningProcess -EA SilentlyContinue;if($p.ProcessName-eq'msmdsrv'){{[pscustomobject]@{{Port=$_.LocalPort}}}}}}|select -First 1;$bin=Split-Path (Get-Process PBIDesktop|select -First 1 -Expand Path) -Parent;Add-Type -Path (Join-Path $bin 'Microsoft.PowerBI.AdomdClient.dll');$c=New-Object Microsoft.AnalysisServices.AdomdClient.AdomdConnection(""Data Source=localhost:$($l.Port)"");$c.Open();$cmd=$c.CreateCommand();$cmd.CommandText=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{q64}'));$r=$cmd.ExecuteReader();$rows=@();while($r.Read()){{$o=[ordered]@{{}};0..($r.FieldCount-1)|%{{$v=$r.GetValue($_);if($v-is[DBNull]){{$v=$null}};$o[$r.GetName($_)]=$v}};$rows+=[pscustomobject]$o}};$r.Close();$c.Close();@{{ok=$true;port=$l.Port;rows=$rows;rowCount=@($rows).Count}}|ConvertTo-Json -Depth 8 -Compress";
    }
}
