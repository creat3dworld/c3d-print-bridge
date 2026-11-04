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
// - FTPS uploads go through curl.exe (session reuse + PBSZ/PROT),
//   because Bambu printers on current firmware refuse the fresh
//   data-channel TLS session the built-in SslStream uploader opens
//   (proven live Nov 3, 2026: 90MB via curl succeeded where the
//   built-in uploader failed on the same printer). The built-in
//   uploader remains only as a fallback when curl.exe is absent.
//   A refused upload fails the job with that reason and never
//   pretends success.
// - The "project_file" start command shape follows community
//   integrations; the first real print through the bridge must
//   verify it (see SETUP.md "first-print check"). If the printer
//   rejects the command, the job fails with the reason.
// - Still images (2275, best-effort, + RTSP grab Oct 30,
//   2026): P2S/X2D do NOT serve the P1/A1-style HTTP JPEG
//   endpoint, so auto-discovery of that URL always fails for
//   them (proven live: HTTP JPEG unreachable on both). Their
//   camera is an RTSPS stream on port 322, and a single frame
//   is grabbed with the ffmpeg executable installed beside
//   the bridge (C:\C3D_Bridge\tools\ffmpeg\bin\ffmpeg.exe,
//   else ffmpeg on PATH) using the printer's LAN access code
//   (the same code MQTT already uses; the stream URL embeds
//   it, so it is NEVER logged). A grabbed frame must look
//   like a JPEG (SOI marker, 100 bytes - 2 MB) or it is
//   discarded, never forwarded. Snapshots otherwise come from
//   an owner-set snapshot_url, if one is configured.

using System.Diagnostics;
using System.IO;
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
    private bool _rtspGrabWorks;
    private DateTime _lastRtspGrabFailure = DateTime.MinValue;
    public string SnapshotSource => _discoveredSnapshotUrl is not null || _cfg.SnapshotAutoDiscovered
        ? "auto_discovered_bambu_http" : _rtspGrabWorks
        ? "bambu_rtsp_grab" : "owner_snapshot_url";
    // Feed (2276): no live feed is reported here - still
    // snapshots only. Still snapshots now come from the RTSP
    // frame grab (see TryGetSnapshotAsync) when no HTTP JPEG
    // URL exists; the grab is a still, not a stream.
    public string FeedType => "none";
    public string? FeedUrl => null;
    public Task<string?> TryAutoDiscoverFeedAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    public string Name => _cfg.Name;
    public string Type => _cfg.Type;
    public string Ip => _cfg.Ip;
    // 2026.1030.1511-farm-dedupe: Bambu identity is the factory serial.
    public string SerialKey => _cfg.Serial ?? "";

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
                Serial = SerialKey,
                State = state,
                // AUTO-START BUILD: the raw gcode_state word
                // survives beside the mapped state, so FINISH
                // is visible as a finish instead of flat "idle".
                NativeState = gcodeState,
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

        // AUTO-START BUILD: every populated tray is reported
        // as a slot, even when no single loaded filament can
        // be picked — the dashboard shows per-slot rows from
        // these instead of guessing an active tray.
        var slots = trays.Select(t => new AmsSlot
        {
            Slot = t.Id,
            Material = t.Type,
            Color = NormalizeColor(t.Color),
        }).ToList();
        if (trays.Count == 0)
            return new AmsState { Known = false, Detail = "No populated AMS tray reported by the printer.", Slots = slots };
        if (trays.Count > 1)
            return new AmsState { Known = false, Detail = $"Multiple AMS trays populated ({trays.Count}); the active tray cannot be determined from this data, so AMS is reported as unknown rather than guessed.", Slots = slots };
        var only = trays[0];
        return new AmsState { Known = true, Material = only.Type, Color = NormalizeColor(only.Color), Detail = $"AMS tray {only.Id}.", Slots = slots };
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

    /// <summary>AUTO-START BUILD — identify a Bambu MQTT
    /// candidate by serial: connect to the candidate IP
    /// with the known printer's serial + access code,
    /// subscribe to device/{serial}/report, send pushall,
    /// and return true ONLY if a report arrives on that
    /// serial's topic. A report on that topic proves the
    /// candidate IS that printer — no other printer
    /// answers for its serial. Used ONLY by IP-change
    /// auto-detection. Mirrors FetchReportAsync, but
    /// parameterized by candidate IP so a moved printer
    /// can be found before its config is updated.</summary>
    public static async Task<bool> TryIdentifyAsync(string ip, int mqttPort, string serial, string accessCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serial) || string.IsNullOrWhiteSpace(accessCode))
            return false;
        var factory = new MqttFactory();
        using var client = factory.CreateMqttClient();
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += e =>
        {
            received.TrySetResult(true);
            return Task.CompletedTask;
        };
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(ip, mqttPort)
            .WithCredentials("bblp", accessCode)
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
                .WithTopicFilter(f => f.WithTopic($"device/{serial}/report"))
                .Build(), ct);
            var request = JsonSerializer.Serialize(new
            {
                pushing = new { sequence_id = "1", command = "pushall" },
            });
            await client.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic($"device/{serial}/request")
                .WithPayload(request)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build(), ct);
            var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(6), ct));
            return completed == received.Task && await received.Task;
        }
        catch
        {
            return false;
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
            // Primary path: curl reuses the control channel's TLS session
            // for the data channel, which Bambu printers on current
            // firmware require (proven live Nov 3, 2026: 90MB via curl
            // succeeded where the built-in uploader failed).
            var curl = await CurlFtps.UploadAsync(_cfg.Ip, "bblp", _cfg.AccessCode!,
                localPath, "/cache/" + fileName, ct);
            if (curl.CurlMissing)
            {
                // curl.exe not present on this machine: fall back to the
                // built-in uploader rather than failing outright.
                return await ImplicitFtps.UploadAsync(_cfg.Ip, 990, "bblp", _cfg.AccessCode!,
                    localPath, "/cache/" + fileName, ct);
            }
            if (!curl.Ok) return (false, $"FTPS upload to printer '{Name}' failed: {curl.Reason}");
            return (true, "");
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

    /// <summary>AUTO-START BUILD — pause / cancel / chamber
    /// light / home, all via the same MQTT request topic as
    /// StartPrintAsync. Command shapes are community-
    /// documented Bambu MQTT (Bambu Lab forum "MQTT for A1"
    /// thread; Home Assistant X1/X1C MQTT thread, which also
    /// shows lights_report confirming ledctrl node/mode):
    /// print pause / stop, system ledctrl chamber_light,
    /// print gcode_line for G28. These shapes are NOT yet
    /// proven on P2S/X2D specifically — the first real use
    /// on each model is a first-use verification (the bridge
    /// reports the printer's answer honestly either way;
    /// a rejected command fails the control, never claims
    /// success). Resume is deliberately NOT added here:
    /// the dashboard exposes no resume dispatch, and this
    /// build invents no dashboard feature.</summary>
    public Task<(bool Ok, string Reason)> PausePrintAsync(CancellationToken ct)
    {
        var command = new
        {
            print = new
            {
                sequence_id = "1",
                command = "pause",
            },
        };
        return PublishCommandAsync(command, ct);
    }

    public Task<(bool Ok, string Reason)> CancelPrintAsync(CancellationToken ct)
    {
        var command = new
        {
            print = new
            {
                sequence_id = "1",
                command = "stop",
                param = "",
            },
        };
        return PublishCommandAsync(command, ct);
    }

    public Task<(bool Ok, string Reason)> SetChamberLedAsync(bool on, CancellationToken ct)
    {
        var command = new
        {
            system = new
            {
                sequence_id = "1",
                command = "ledctrl",
                led_node = "chamber_light",
                led_mode = on ? "on" : "off",
            },
        };
        return PublishCommandAsync(command, ct);
    }

    /// <summary>Home all axes via gcode_line G28. Executed
    /// ONLY behind the bridge's home_power_allowed gate in
    /// FarmPoller (owner A7 decision) — calling this method
    /// directly bypasses that gate, so only FarmPoller may
    /// call it.</summary>
    public Task<(bool Ok, string Reason)> HomeAsync(CancellationToken ct)
    {
        var command = new
        {
            print = new
            {
                sequence_id = "1",
                command = "gcode_line",
                param = "G28",
            },
        };
        return PublishCommandAsync(command, ct);
    }

    public async Task<byte[]?> TryGetSnapshotAsync(CancellationToken ct)
    {
        var url = !string.IsNullOrWhiteSpace(_cfg.SnapshotUrl) ? _cfg.SnapshotUrl : _discoveredSnapshotUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            // No HTTP JPEG URL (the normal P2S/X2D case):
            // fall back to a single RTSP frame grab.
            return await TryGrabRtspFrameAsync(ct);
        }
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

    /// <summary>RTSP frame grab (Oct 30, 2026): P2S/X2D expose
    /// no HTTP JPEG endpoint, so a still is grabbed from the
    /// printer's RTSPS stream with ffmpeg: one frame, JPEG to
    /// stdout, validated like the HTTP path (SOI marker, 100
    /// bytes - 2 MB). The stream URL embeds the LAN access
    /// code, so it is NEVER logged, and ffmpeg's stderr is
    /// never logged either (it echoes the input URL). ffmpeg
    /// resolves from C:\C3D_Bridge\tools\ffmpeg\bin\ffmpeg.exe
    /// first, then PATH. Failures back off 10 minutes so a
    /// broken camera path cannot stall the worker loop; a
    /// 30-second process timeout bounds each attempt.</summary>
    private async Task<byte[]?> TryGrabRtspFrameAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_cfg.AccessCode)) return null;
        if (DateTime.UtcNow - _lastRtspGrabFailure < TimeSpan.FromMinutes(10)) return null;
        try
        {
            // URL built in memory only - never logged. The
            // printer's certificate is self-signed, so TLS
            // verification is disabled (same LAN-only trust
            // as the MQTT connection).
            var url = $"rtsps://bblp:{_cfg.AccessCode}@{_cfg.Ip}:322/streaming/live/1";
            var psi = new ProcessStartInfo
            {
                FileName = ResolveFfmpegPath(),
                Arguments = $"-nostats -loglevel error -tls_verify 0 -rtsp_transport tcp -i \"{url}\" -frames:v 1 -q:v 2 -f image2 pipe:1",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var stderrTask = proc.StandardError.ReadToEndAsync(timeout.Token);
                await using var ms = new MemoryStream();
                await proc.StandardOutput.BaseStream.CopyToAsync(ms, timeout.Token);
                await proc.WaitForExitAsync(timeout.Token);
                _ = await stderrTask; // Drained only to avoid a pipe stall; never logged (may echo the URL).
                if (proc.ExitCode != 0) throw new InvalidOperationException($"ffmpeg exited {proc.ExitCode}");
                var bytes = ms.ToArray();
                if (bytes.Length < 100 || bytes.Length > 2_000_000) return null;
                if (bytes[0] != 0xFF || bytes[1] != 0xD8) return null;
                _rtspGrabWorks = true;
                return bytes;
            }
            finally
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            }
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            _lastRtspGrabFailure = DateTime.UtcNow;
            Program.Log($"Bambu RTSP grab for '{Name}' timed out after 30 seconds; retry in 10 minutes.");
            return null;
        }
        catch (Exception ex)
        {
            _lastRtspGrabFailure = DateTime.UtcNow;
            Program.Log($"Bambu RTSP grab for '{Name}' failed ({ex.GetType().Name}); retry in 10 minutes.");
            return null;
        }
    }

    private static string ResolveFfmpegPath()
    {
        const string toolsPath = @"C:\C3D_Bridge\tools\ffmpeg\bin\ffmpeg.exe";
        if (File.Exists(toolsPath)) return toolsPath;
        return "ffmpeg";
    }

    /// <summary>Camera auto-discovery (2275), best-effort only.
    /// Never overwrites an owner-set URL. Tries ONLY
    /// http://&lt;ip&gt;:6000/recording/JPG/snapshot.jpg - the
    /// P1/A1-style HTTP JPEG endpoint, unverified for P2S/X2D
    /// (and proven absent on both live printers). A valid JPEG
    /// (100 bytes - 2 MB, SOI marker) is kept for this run and
    /// returned for the Worker to persist to the LOCAL config
    /// only. On any miss, the URL stays blank and snapshots
    /// fall back to the RTSP frame grab (see
    /// TryGetSnapshotAsync). No URL is logged.</summary>
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
            Program.Log($"Camera auto-discovery for '{Name}': Bambu HTTP JPEG endpoint did not return a valid JPEG; no snapshot URL discovered. snapshots will use the RTSP frame grab instead.");
            return null;
        }
        catch (Exception ex)
        {
            Program.Log($"Camera auto-discovery for '{Name}': Bambu HTTP JPEG endpoint unreachable ({ex.Message}); no snapshot URL discovered. snapshots will use the RTSP frame grab instead.");
            return null;
        }
    }

    private PrinterStatus Offline(string detail) => new()
    {
        Name = Name,
        Type = Type,
        Ip = Ip,
        Serial = SerialKey,
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

/// <summary>FTPS uploader via curl.exe (primary path since Nov 3, 2026).
/// curl reuses the control channel's TLS session for the data channel
/// and negotiates PBSZ/PROT itself, which is what Bambu printers on
/// current firmware require. The built-in SslStream uploader opens a
/// fresh data-channel session that those printers refuse by dropping
/// the connection mid-transfer. Credentials travel only in a temp
/// curl config file that is deleted afterwards; they never appear on
/// the command line, and any returned reason has them redacted, so
/// they cannot leak into the bridge log or the dashboard.</summary>
internal static class CurlFtps
{
    public static async Task<(bool Ok, string Reason, bool CurlMissing)> UploadAsync(
        string host, string user, string password,
        string localPath, string remotePath, CancellationToken ct)
    {
        var curlExe = FindCurlExe();
        if (curlExe is null) return (false, "curl.exe not found", true);
        long fileLen;
        try { fileLen = new FileInfo(localPath).Length; }
        catch (Exception ex) { return (false, $"cannot read local file: {ex.Message}", false); }
        var configPath = Path.Combine(Path.GetTempPath(), "c3d-curl-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var segments = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString);
            var urlPath = "/" + string.Join("/", segments);
            var config = string.Join("\n",
                "user = \"" + Esc(user) + ":" + Esc(password) + "\"",
                "insecure",
                "ftp-ssl",
                "upload-file = \"" + Esc(localPath) + "\"",
                "url = \"ftps://" + host + ":990" + urlPath + "\"",
                "silent",
                "show-error",
                "write-out = \"%{size_upload}\"");
            await File.WriteAllTextAsync(configPath, config, ct);
            var psi = new ProcessStartInfo
            {
                FileName = curlExe,
                Arguments = "-K \"" + configPath + "\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = new Process { StartInfo = psi };
            proc.Start();
            using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ } });
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
            {
                var err = Redact(stderr.Trim(), password);
                if (err.Length > 300) err = err[..300];
                return (false, $"curl FTPS upload failed (exit {proc.ExitCode}): {err}", false);
            }
            if (!long.TryParse(stdout.Trim(), out var uploaded) || uploaded != fileLen)
                return (false, $"curl FTPS upload size mismatch (uploaded {stdout.Trim()} of {fileLen} bytes)", false);
            return (true, "", false);
        }
        catch (Exception ex)
        {
            return (false, $"curl FTPS upload failed: {Redact(ex.Message, password)}", false);
        }
        finally
        {
            try { File.Delete(configPath); } catch { /* best effort */ }
        }
    }

    private static string? FindCurlExe()
    {
        var dirs = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path)) dirs.AddRange(path.Split(Path.PathSeparator));
        try { dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.System)); } catch { /* best effort */ }
        foreach (var dir in dirs)
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "curl.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* ignore bad PATH entries */ }
        }
        return null;
    }

    private static string Redact(string s, string secret) =>
        string.IsNullOrEmpty(secret) ? s : s.Replace(secret, "****");
}
