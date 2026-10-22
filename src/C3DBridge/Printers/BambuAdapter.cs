// C3D Print Bridge — Bambu Lab adapter (local LAN mode).
//
// PROTOCOL (community-documented; see README sources):
//   MQTT: printer IP, port 8883, TLS, username "bblp", password =
//     the printer's LAN access code from its own screen. The printer
//     certificate is self-signed, so validation is skipped — this
//     connection is LAN-only and gated by the access code.
//   Topics: subscribe device/{serial}/report, publish
//     device/{serial}/request. A "pushall" request asks the printer
//     for a full status report.
//   FTPS: implicit TLS on port 990, same credentials. Uploads land
//     in /cache on the printer's storage.
//   Report JSON (defensively parsed — missing fields mean
//     "unverifiable", never a guess):
//     print.gcode_state: RUNNING / PAUSE / IDLE / FINISH / FAILED
//     print.mc_percent, print.mc_remaining_time
//     print.ams.ams[].tray[]: tray_type, tray_color (hex, RGB or
//       ARGB). With exactly one populated tray, that tray is the
//       loaded filament. With several populated trays, the active
//       tray cannot be determined from this data, so AMS is
//       reported as unknown rather than guessed.
//
// KNOWN LIMITS (honest, by design):
// - FTPS data-channel TLS session reuse is a known Bambu quirk:
//   some printers refuse a fresh data-channel session. Upload
//   attempts report the printer's exact response; a refused upload
//   fails the job with that reason and never pretends success.
// - The "project_file" start command shape follows community
//   integrations; the first real print through the bridge must
//   verify it (see SETUP.md "first-print check"). If the printer
//   rejects the command, the job fails with the reason.
// - Still images (2275, best-effort): Bambu's camera is
//   normally an RTSP video stream (port 322, needs a
//   TTCode/cloud token), and grabbing a frame from it would
//   need heavy video dependencies this thin relay deliberately
//   avoids — no ffmpeg/OpenCV/RTSP decoding is added here.
//   Auto-discovery therefore tries ONLY the P1/A1-style HTTP
//   JPEG endpoint http://<ip>:6000/recording/JPG/snapshot.jpg
//   (documented for other Bambu models, unverified for
//   P2S/X2D). If it returns a valid JPEG it is saved to the
//   LOCAL config; otherwise the URL stays blank and no
//   snapshot is sent — never a fake image. Snapshots otherwise
//   come from an owner-set snapshot_url, if one is configured.

using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;

namespace C3DBridge.Printers;

public sealed class BambuAdapter : IPrinterAdapter
{
    private readonly PrinterConfig _cfg;
    private readonly int _mqttPort;
    // Camera auto-discovery (2275): at most one attempt per
    // adapter instance; see TryAutoDiscoverSnapshotUrlAsync.
    private string? _discoveredSnapshotUrl;
    private bool _autoDiscoveryAttempted;
    public string SnapshotSource => _discoveredSnapshotUrl is not null || _cfg.SnapshotAutoDiscovered
        ? "auto_discovered_bambu_http" : "owner_snapshot_url";
    public string Name => _cfg.Name;
    public string Type => _cfg.Type;
    public string Ip => _cfg.Ip;

    public BambuAdapter(PrinterConfig cfg)
    {
        _cfg = cfg;
        _mqttPort = cfg.Port ?? 8883;
    }

    private (bool Ok, string Reason) CheckIdentity()
    {
        if (string.IsNullOrWhiteSpace(_cfg.Serial))
            return (false, $"Printer '{Name}': no serial number in config — MQTT topics need the printer serial.");
        if (string.IsNullOrWhiteSpace(_cfg.AccessCode))
            return (false, $"Printer '{Name}': no LAN access code in config — read it from the printer's screen (see SETUP.md).");
        return (true, "");
    }

    public async Task<PrinterStatus> GetStatusAsync(CancellationToken ct)
    {
        var (ok, reason) = CheckIdentity();
        if (!ok) return Offline(reason);

        var report = await FetchReportAsync(ct);
        if (report is null)
            return Offline($"Could not reach printer '{Name}' at {_cfg.Ip}:{_mqttPort} (MQTT). Check the IP, Developer/LAN mode, and the access code.");

        try
        {
            using var doc = JsonDocument.Parse(report);
            var root = doc.RootElement;
            if (!root.TryGetProperty("print", out var print))
                return Offline($"Printer '{Name}' returned a report without print state.");

            var gcodeState = print.TryGetProperty("gcode_state", out var gs) ? gs.GetString() ?? "" : "";
            var state = gcodeState switch
            {
                "RUNNING" => "printing",
                "PAUSE" => "paused",
                "IDLE" or "FINISH" => "idle",
                "FAILED" => "error",
                _ => "error",
            };
            var detail = state == "error" && gcodeState != "FAILED"
                ? $"Unrecognized printer state '{gcodeState}'."
                : gcodeState == "FAILED" ? "Printer reports a failed print." : "";

            double? percent = print.TryGetProperty("mc_percent", out var mc) && mc.ValueKind == JsonValueKind.Number
                ? mc.GetDouble() : null;
            long? remaining = print.TryGetProperty("mc_remaining_time", out var mrt) && mrt.ValueKind == JsonValueKind.Number
                ? (long)mrt.GetDouble() : null;

            return new PrinterStatus
            {
                Name = Name,
                Type = Type,
                Ip = Ip,
                State = state,
                ProgressPercent = percent,
                RemainingSeconds = remaining,
                Detail = detail,
                Ams = ParseAms(print),
                CameraAvailable = _cfg.Camera, // camera_available means the printer HAS a camera, not that a still URL is configured; snapshot fetching still needs SnapshotUrl separately
            };
        }
        catch (Exception ex)
        {
            return Offline($"Printer '{Name}' returned a report the bridge could not read: {ex.Message}");
        }
    }

    private static AmsState ParseAms(JsonElement print)
    {
        // Collect populated trays across all AMS units.
        var trays = new List<(string Type, string Color, string Id)>();
        if (print.TryGetProperty("ams", out var ams) && ams.TryGetProperty("ams", out var units) &&
            units.ValueKind == JsonValueKind.Array)
        {
            foreach (var unit in units.EnumerateArray())
            {
                if (!unit.TryGetProperty("tray", out var trayArr) || trayArr.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var tray in trayArr.EnumerateArray())
                {
                    var type = tray.TryGetProperty("tray_type", out var t) ? t.GetString() ?? "" : "";
                    var color = tray.TryGetProperty("tray_color", out var c) ? c.GetString() ?? "" : "";
                    var id = tray.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(color))
                        trays.Add((type, color, id));
                }
            }
        }

        if (trays.Count == 0)
            return new AmsState { Known = false, Detail = "No populated AMS tray reported by the printer." };
        if (trays.Count > 1)
            return new AmsState { Known = false, Detail = $"Multiple AMS trays populated ({trays.Count}); the active tray cannot be determined from this data, so AMS is reported as unknown rather than guessed." };
        var only = trays[0];
        return new AmsState { Known = true, Material = only.Type, Color = NormalizeColor(only.Color), Detail = $"AMS tray {only.Id}." };
    }

    private static string NormalizeColor(string raw)
    {
        var v = raw.Trim().TrimStart('#');
        // Bambu reports ARGB (8 hex digits); strip the alpha prefix.
        if (v.Length == 8 && v.All(Uri.IsHexDigit)) return v[2..].ToUpperInvariant();
        return v.ToUpperInvariant();
    }

    /// <summary>Connect, ask for a full report, wait briefly, disconnect.
    /// Short-lived connections keep the bridge light; a missed report
    /// is "unreachable", never a stale or invented state.</summary>
    private async Task<string?> FetchReportAsync(CancellationToken ct)
    {
        var factory = new MqttFactory();
        using var client = factory.CreateMqttClient();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += e =>
        {
            var payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
            received.TrySetResult(payload);
            return Task.CompletedTask;
        };

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_cfg.Ip, _mqttPort)
            .WithCredentials("bblp", _cfg.AccessCode)
            .WithClientId($"c3d-bridge-{Guid.NewGuid():N}"[..24])
            .WithCleanSession()
            .WithTlsOptions(o => o
                .WithCertificateValidationHandler(_ => true)
                .WithSslProtocols(SslProtocols.Tls12))
            .Build();

        try
        {
            await client.ConnectAsync(options, ct);
            await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic($"device/{_cfg.Serial}/report"))
                .Build(), ct);
            var request = JsonSerializer.Serialize(new
            {
                pushing = new { sequence_id = "1", command = "pushall" },
            });
            await client.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic($"device/{_cfg.Serial}/request")
                .WithPayload(request)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build(), ct);

            var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(6), ct));
            if (completed != received.Task) return null;
            return await received.Task;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { /* best effort */ }
        }
    }

    private async Task<(bool Ok, string Reason)> PublishCommandAsync(object command, CancellationToken ct)
    {
        var (ok, reason) = CheckIdentity();
        if (!ok) return (false, reason);

        var factory = new MqttFactory();
        using var client = factory.CreateMqttClient();
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_cfg.Ip, _mqttPort)
            .WithCredentials("bblp", _cfg.AccessCode)
            .WithClientId($"c3d-bridge-{Guid.NewGuid():N}"[..24])
            .WithCleanSession()
            .WithTlsOptions(o => o
                .WithCertificateValidationHandler(_ => true)
                .WithSslProtocols(SslProtocols.Tls12))
            .Build();
        try
        {
            await client.ConnectAsync(options, ct);
            await client.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic($"device/{_cfg.Serial}/request")
                .WithPayload(JsonSerializer.Serialize(command))
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build(), ct);
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, $"Could not send command to printer '{Name}': {ex.Message}");
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { /* best effort */ }
        }
    }

    public async Task<(bool Ok, string Reason)> UploadFileAsync(string localPath, string fileName, CancellationToken ct)
    {
        var (ok, reason) = CheckIdentity();
        if (!ok) return (false, reason);
        try
        {
            return await ImplicitFtps.UploadAsync(_cfg.Ip, 990, "bblp", _cfg.AccessCode!,
                localPath, "/cache/" + fileName, ct);
        }
        catch (Exception ex)
        {
            return (false, $"FTPS upload to printer '{Name}' failed: {ex.Message}");
        }
    }

    public Task<(bool Ok, string Reason)> StartPrintAsync(string fileName, string jobKey, CancellationToken ct)
    {
        // Community-documented start shape; the Worker verifies via
        // status polls that printing actually began, and fails the
        // job if it did not within two minutes.
        var command = new
        {
            print = new
            {
                sequence_id = "1",
                command = "project_file",
                param = "/cache/" + fileName,
                subtask_name = fileName,
                task_id = jobKey,
            },
        };
        return PublishCommandAsync(command, ct);
    }

    public async Task<byte[]?> TryGetSnapshotAsync(CancellationToken ct)
    {
        var url = !string.IsNullOrWhiteSpace(_cfg.SnapshotUrl) ? _cfg.SnapshotUrl : _discoveredSnapshotUrl;
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var bytes = await http.GetByteArrayAsync(url, ct);
            if (bytes.Length < 100 || bytes.Length > 2_000_000) return null;
            // Must look like a JPEG (SOI marker); anything else is not
            // a snapshot and is discarded, never forwarded.
            if (bytes[0] != 0xFF || bytes[1] != 0xD8) return null;
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Camera auto-discovery (2275), best-effort only.
    /// Never overwrites an owner-set URL. Tries ONLY
    /// http://&lt;ip&gt;:6000/recording/JPG/snapshot.jpg — the
    /// P1/A1-style HTTP JPEG endpoint, unverified for P2S/X2D.
    /// A valid JPEG (100 bytes – 2 MB, SOI marker) is kept for
    /// this run and returned for the Worker to persist to the
    /// LOCAL config only. On any miss, the URL stays blank and
    /// the miss is logged once, honestly: Bambu's camera is
    /// normally an RTSP video stream (port 322, needs a
    /// TTCode/cloud token), and frame-grabbing from it would
    /// need heavy video dependencies this thin relay avoids —
    /// no ffmpeg/OpenCV/RTSP decoding is added. No URL is
    /// logged.</summary>
    public async Task<string?> TryAutoDiscoverSnapshotUrlAsync(CancellationToken ct)
    {
        if (_autoDiscoveryAttempted) return null;
        _autoDiscoveryAttempted = true;
        if (!string.IsNullOrWhiteSpace(_cfg.SnapshotUrl)) return null;
        if (!string.IsNullOrWhiteSpace(_discoveredSnapshotUrl)) return _discoveredSnapshotUrl;
        var url = $"http://{_cfg.Ip}:6000/recording/JPG/snapshot.jpg";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var bytes = await http.GetByteArrayAsync(url, ct);
            if (bytes.Length >= 100 && bytes.Length <= 2_000_000 && bytes[0] == 0xFF && bytes[1] == 0xD8)
            {
                _discoveredSnapshotUrl = url;
                Program.Log($"Camera auto-discovery for '{Name}': snapshot URL auto-discovered from the Bambu HTTP JPEG endpoint.");
                return url;
            }
            Program.Log($"Camera auto-discovery for '{Name}': Bambu HTTP JPEG endpoint did not return a valid JPEG; no snapshot URL discovered. Bambu's camera is normally an RTSP video stream (port 322, needs a TTCode/cloud token) and frame-grabbing from it is not built into this thin relay.");
            return null;
        }
        catch (Exception ex)
        {
            Program.Log($"Camera auto-discovery for '{Name}': Bambu HTTP JPEG endpoint unreachable ({ex.Message}); no snapshot URL discovered. Bambu's camera is normally an RTSP video stream (port 322, needs a TTCode/cloud token) and frame-grabbing from it is not built into this thin relay.");
            return null;
        }
    }

    private PrinterStatus Offline(string detail) => new()
    {
        Name = Name,
        Type = Type,
        Ip = Ip,
        State = "offline",
        Detail = detail,
        Ams = new AmsState { Known = false, Detail = "Printer unreachable; AMS unknown." },
        CameraAvailable = false,
    };
}

/// <summary>Minimal implicit-FTPS uploader (TLS from the first byte,
/// port 990). Built on SslStream so the bridge needs no extra FTP
/// dependency. Known Bambu quirk: some printers refuse a fresh
/// data-channel TLS session — a refusal surfaces as an error here
/// and fails the job honestly; it is never hidden or retried as a
/// fake success.</summary>
internal static class ImplicitFtps
{
    public static async Task<(bool Ok, string Reason)> UploadAsync(
        string host, int port, string user, string password,
        string localPath, string remotePath, CancellationToken ct)
    {
        using var control = new TcpClient();
        await control.ConnectAsync(host, port, ct);
        using var controlStream = new SslStream(control.GetStream(), leaveInnerStreamOpen: false,
            (sender, cert, chain, errors) => true);
        await controlStream.AuthenticateAsClientAsync(host, null, SslProtocols.Tls12, checkCertificateRevocation: false);

        async Task<string> ReadReplyAsync()
        {
            var sb = new StringBuilder();
            var buf = new byte[1];
            while (true)
            {
                var n = await controlStream.ReadAsync(buf, ct);
                if (n == 0) break;
                sb.Append((char)buf[0]);
                if (sb.ToString().EndsWith("\n")) break;
            }
            return sb.ToString();
        }

        async Task SendAsync(string line)
        {
            var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
            await controlStream.WriteAsync(bytes, ct);
            await controlStream.FlushAsync(ct);
        }

        await ReadReplyAsync(); // greeting
        await SendAsync($"USER {user}");
        await ReadReplyAsync();
        await SendAsync($"PASS {password}");
        var passReply = await ReadReplyAsync();
        if (!passReply.StartsWith("230"))
            return (false, $"FTPS login rejected by the printer: {passReply.Trim()}");
        await SendAsync("TYPE I");
        await ReadReplyAsync();
        await SendAsync("PASV");
        var pasv = await ReadReplyAsync();
        var (dataHost, dataPort) = ParsePasv(pasv);
        if (dataPort == 0)
            return (false, $"FTPS passive-mode reply not understood: {pasv.Trim()}");

        using var data = new TcpClient();
        await data.ConnectAsync(dataHost, dataPort, ct);
        using var dataStream = new SslStream(data.GetStream(), leaveInnerStreamOpen: false,
            (sender, cert, chain, errors) => true);
        await SendAsync($"STOR {remotePath}");
        await dataStream.AuthenticateAsClientAsync(host, null, SslProtocols.Tls12, checkCertificateRevocation: false);
        var fileBytes = await File.ReadAllBytesAsync(localPath, ct);
        await dataStream.WriteAsync(fileBytes, ct);
        await dataStream.FlushAsync(ct);
        dataStream.Dispose();
        data.Dispose();
        var storReply = await ReadReplyAsync();
        if (!storReply.StartsWith("226") && !storReply.StartsWith("250"))
            return (false, $"FTPS upload not confirmed by the printer: {storReply.Trim()}");
        return (true, "");
    }

    private static (string Host, int Port) ParsePasv(string reply)
    {
        var start = reply.IndexOf('(');
        var end = reply.IndexOf(')');
        if (start < 0 || end <= start) return ("", 0);
        var parts = reply[(start + 1)..end].Split(',');
        if (parts.Length != 6) return ("", 0);
        if (!int.TryParse(parts[4], out var p1) || !int.TryParse(parts[5], out var p2)) return ("", 0);
        return (string.Join(".", parts[..4]), p1 * 256 + p2);
    }
}
