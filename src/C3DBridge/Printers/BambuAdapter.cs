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
//       RRGGBBAA — alpha LAST). With exactly one populated tray, that tray is the
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
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MQTTnet;
using MQTTnet.Client;

namespace C3DBridge.Printers;

public sealed class BambuAdapter : IPrinterAdapter
{
    private readonly PrinterConfig _cfg;
    private readonly int _mqttPort;
    // 2026.1105-push-log: last push summary written to the log, so each
    // printer answer is recorded once per change (and once on the first
    // push) instead of once per status poll.
    private string? _lastPushSummary;
    // 2026.1105-gate-light: last printer-confirmed chamber-light
    // mode ("on" / "off") and when it was observed, read from
    // the push report's lights_report — never from a command
    // this bridge sent. "unknown" until a report actually
    // carries lights_report. The pre-snapshot light ensure
    // below is throttled on these two fields.
    private string _verifiedLightMode = "unknown";
    private DateTime _verifiedLightUtc = DateTime.MinValue;
    private DateTime _lastLightEnsureUtc = DateTime.MinValue;
    private bool _lastLightEnsureOk;
    /// <summary>Chamber-light mode the printer itself last
    /// reported (lights_report): "on" / "off" / "unknown".</summary>
    public string VerifiedLightMode => _verifiedLightMode;
    /// <summary>Unix time the verified light mode was last
    /// observed; 0 = never observed.</summary>
    public long VerifiedLightUnix => _verifiedLightUtc == DateTime.MinValue
        ? 0 : new DateTimeOffset(_verifiedLightUtc).ToUnixTimeSeconds();
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

    // 2026.1105-push-log: record a printer push answer once per change
    // (and once on the first push). Logging only: no state, gate, or
    // status value changes here.
    private void LogPushSummary(string summary)
    {
        if (string.Equals(summary, _lastPushSummary, StringComparison.Ordinal)) return;
        _lastPushSummary = summary;
        Program.Log($"[Bambu] Push {Name} ({Ip}): {summary}");
    }

    public async Task<PrinterStatus> GetStatusAsync(CancellationToken ct)
    {
        var (ok, reason) = CheckIdentity();
        if (!ok) return Offline(reason);

        var report = await FetchReportAsync(ct);
        if (report is null) LogPushSummary("no report (printer did not answer the push request)");
        if (report is null)
            return Offline($"Could not reach printer '{Name}' at {_cfg.Ip}:{_mqttPort} (MQTT). Check the IP, Developer/LAN mode, and the access code.");

        try
        {
            using var doc = JsonDocument.Parse(report);
            var root = doc.RootElement;
            if (!root.TryGetProperty("print", out var print))
                return Offline($"Printer '{Name}' returned a report without print state.");

            var gcodeState = print.TryGetProperty("gcode_state", out var gs) ? gs.GetString() ?? "" : "";
        // 2026.1105-push-log: record what the printer actually answered
        // (logging only). print_error is logged when nonzero; HMS entries
        // are logged as code:attr; subtask_id and gcode_state always.
        var subtaskId = print.TryGetProperty("subtask_id", out var st)
            ? (st.ValueKind == JsonValueKind.String ? st.GetString() ?? "" : st.GetRawText())
            : "";
        var printError = 0u;
        if (print.TryGetProperty("print_error", out var pe))
            {
                if (pe.ValueKind == JsonValueKind.Number && pe.TryGetUInt32(out var peNum)) printError = peNum;
                else if (pe.ValueKind == JsonValueKind.String && uint.TryParse(pe.GetString(), out var peStr)) printError = peStr;
            }        var hmsText = "";
        if (print.TryGetProperty("hms", out var hms) && hms.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in hms.EnumerateArray())
                    {
                        var code = entry.TryGetProperty("code", out var hc) ? hc.GetRawText() : "?";
                        var attr = entry.TryGetProperty("attr", out var ha) ? ha.GetRawText() : "?";
                        hmsText += (hmsText.Length == 0 ? "" : ",") + code + ":" + attr;
                    }
                }
        var pushSummary = $"gcode_state={gcodeState} subtask_id={subtaskId}"
            + (printError != 0 ? $" print_error={printError}" : "")
            + (hmsText.Length > 0 ? $" hms=[{hmsText}]" : "");
        LogPushSummary(pushSummary);
        // 2026.1105-gate-light: record the printer-confirmed
        // chamber-light mode from this same fresh report, so
        // the status push carries the light as the printer
        // reports it (with its age), not as a command this
        // bridge once sent. A report without lights_report
        // leaves the previous observation untouched — its
        // timestamp then shows how stale it is.
        var lightMode = ParseLightsReport(root);
        if (lightMode is not null)
        {
            _verifiedLightMode = lightMode;
            _verifiedLightUtc = DateTime.UtcNow;
        }
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
                // 2026.1105-gate-light: printer-confirmed light
                // mode + observation time (see fields above).
                LightVerified = _verifiedLightMode,
                LightVerifiedUnix = VerifiedLightUnix,
            };
        }
        catch (Exception ex)
        {
            return Offline($"Printer '{Name}' returned a report the bridge could not read: {ex.Message}");
        }
    }

    private static AmsState ParseAms(JsonElement print)
    {
        // GATE-AMS BUILD (2026.1105): stamp every AMS reading
        // with the time it was observed from the printer's own
        // report, so the dashboard can show the age instead of
        // presenting a stale value as current.
        var observedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // Collect populated trays across all AMS units. A tray
        // reporting either a type or a color is a populated
        // slot (a tray with neither is empty and stays
        // excluded) — a half-reported tray still carries the
        // half the printer did report, instead of vanishing.
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
                    var type = (tray.TryGetProperty("tray_type", out var t) ? t.GetString() ?? "" : "").Trim();
                    var color = (tray.TryGetProperty("tray_color", out var c) ? c.GetString() ?? "" : "").Trim();
                    var id = (tray.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "").Trim();
                    if (!string.IsNullOrWhiteSpace(type) || !string.IsNullOrWhiteSpace(color))
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
            return new AmsState { Known = false, Detail = "No populated AMS tray reported by the printer.", Slots = slots, AmsObservedUnix = observedUnix };
        if (trays.Count > 1)
            return new AmsState { Known = false, Detail = $"Multiple AMS trays populated ({trays.Count}); the active tray cannot be determined from this data, so AMS is reported as unknown rather than guessed.", Slots = slots, AmsObservedUnix = observedUnix };
        var only = trays[0];
        return new AmsState { Known = true, Material = only.Type, Color = NormalizeColor(only.Color), Detail = $"AMS tray {only.Id}.", Slots = slots, AmsObservedUnix = observedUnix };
    }

    private static string NormalizeColor(string raw)
    {
        var v = raw.Trim().TrimStart('#');
        // Bambu reports RRGGBBAA (8 hex digits, alpha LAST); strip
        // the alpha suffix to get RRGGBB. The old code stripped the
        // alpha PREFIX (ARGB assumption), which dropped the red byte
        // and shifted every color toward blue — black "161616FF"
        // read as vivid blue "1616FF", red "FF4343FF" as "4343FF".
        if (v.Length == 8 && v.All(Uri.IsHexDigit)) return v[..6].ToUpperInvariant();
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

    // Plate-filename repair (follow-up to 9d04e61): the dashboard
    // sanitizes ".gcode.3mf" to ".gcode_.3mf", and WordPress / Media
    // dedup renames a stored plate to ".gcode-<digits>.3mf"
    // (defensively also ".gcode_<digits>.3mf"). All three resolve to
    // the canonical Bambu plate name ".gcode.3mf"; anything else is
    // returned unchanged.
    // No-underscore rule (Nov 7, 2026): Bambu/printer file names
    // must not contain "_". After the plate-extension repair above,
    // any remaining underscore is replaced with a hyphen, so the
    // name the printer receives is always clean. The repair runs
    // first, so the repaired ".gcode.3mf" suffix (which has no
    // underscore) is unaffected. Both UploadFileAsync and
    // StartPrintAsync call this, so the upload name and the start
    // command name are always the same sanitized value.
    public static string RepairPlateFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return fileName;
        var repaired = fileName;
        if (repaired.EndsWith(".gcode_.3mf", StringComparison.OrdinalIgnoreCase))
            repaired = repaired.Substring(0, repaired.Length - ".gcode_.3mf".Length) + ".gcode.3mf";
        else
        {
            var dedup = Regex.Match(repaired, @"\.gcode[-_]\d+\.3mf$", RegexOptions.IgnoreCase);
            if (dedup.Success)
                repaired = repaired.Substring(0, dedup.Index) + ".gcode.3mf";
        }
        return SanitizePrinterFileName(repaired);
    }

    // Printer-bound filename sanitizer (Nov 7, 2026): Bambu/printer
    // file names must not contain "_". Replace every underscore with
    // a hyphen; the extension and the rest of the name are unchanged.
    // Used by RepairPlateFileName (Bambu) and by the Snapmaker
    // adapter, so every printer receives a clean name no matter
    // what the dashboard or Bambu Studio named the file.
    public static string SanitizePrinterFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return fileName;
        return fileName.Replace("_", "-", StringComparison.Ordinal);
    }
    public async Task<(bool Ok, string Reason)> UploadFileAsync(string localPath, string fileName, CancellationToken ct)
    {
        var (ok, reason) = CheckIdentity();
        if (!ok) return (false, reason);
        // Repair dashboard-mangled plate filenames (see
        // RepairPlateFileName): the dashboard sanitizes ".gcode.3mf"
        // to ".gcode_.3mf", and WordPress/Media dedup renames it to
        // ".gcode-<digits>.3mf"; Bambu printers reject both forms.
        var uploadFileName = RepairPlateFileName(fileName);
        if (!string.Equals(uploadFileName, fileName, StringComparison.Ordinal))
            Program.Log($"[Bambu] Repaired mangled plate filename '{fileName}' -> '{uploadFileName}' for upload");
        try
        {
            // Primary path: curl reuses the control channel TLS session
            // for the data channel, which Bambu printers on current
            // firmware require (proven live Nov 3, 2026: 90MB via curl
            // succeeded where the built-in uploader failed).
            var curl = await CurlFtps.UploadAsync(_cfg.Ip, "bblp", _cfg.AccessCode!, localPath, "/cache/" + uploadFileName, ct);
            if (curl.CurlMissing)
            {
                // curl.exe not present (or it failed to start) - fall back to
                // the built-in uploader, which previously failed on large files.
                var implicitResult = await ImplicitFtps.UploadAsync(_cfg.Ip, 990, "bblp", _cfg.AccessCode!, localPath, "/cache/" + uploadFileName, ct);
                Program.Log($"[Bambu] Uploaded '{uploadFileName}' to '/cache/{uploadFileName}' ({new System.IO.FileInfo(localPath).Length} bytes)");
                return implicitResult;
            }
            if (!curl.Ok) return (false, $"FTPS upload to printer '{Name}' failed: {curl.Reason}");
            Program.Log($"[Bambu] Uploaded '{uploadFileName}' to '/cache/{uploadFileName}' ({new System.IO.FileInfo(localPath).Length} bytes)");
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, $"FTPS upload to printer '{Name}' failed: {ex.Message}");
        }
    }
    // H2-ONLY (2026.1108, Marlena's decision): EVERY Bambu
    // start uses the H2-style project_file payload — the legacy
    // P1-style payload (url file:///sdcard/cache/<name>, md5 "",
    // boolean flags, project_id/task_id/subtask_id "0", fixed
    // ams_mapping [0,-1,-1,-1,-1]) is DELETED, so the old command
    // can never be sent again. History: the P2S rejected the
    // legacy payload with 0500-4002 (five identical failures)
    // until it was moved to the H2 form Nov 5, 2026
    // (bambu-printer-mcp PR #15, verified on a real P2S running
    // firmware 01.02.00.00); the X2D's Nov 7 clean-filename
    // retry (fresh hyphen-only plate, SHA-256 verified) still
    // answered 0500-4002, fresh 8 s after print_started
    // (print_error 83902466 = 0x05004002), so the X2D takes the
    // same H2 route with the dual-nozzle mapping work below.
    // The UsesH2ProjectFile gate is deleted with the legacy
    // path. The fleet is P2S + X2D only, both H2-family — if a
    // P1-series printer is ever added, its own start path must
    // be re-introduced; the legacy form is gone, not preserved.
    // Dual-nozzle differences live in IsDualNozzle /
    // ReadNozzleMapping / the dual-nozzle form of
    // H2Mapping2Entry below.

    // Dual-nozzle Bambu (X2D): the H2 project_file carries the
    // extra nozzle_mapping field and dual-nozzle mapping
    // semantics. The serial prefix is the load-bearing check
    // (the shop config's Type value for the X2D was not
    // re-verified when this was written — "x2d" mirrors the
    // P2S "p2s" pattern and LithophanePoller's "x2d" slug).
    private bool IsDualNozzle =>
        (_cfg.Serial ?? "").StartsWith("20P", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_cfg.Type, "x2d", StringComparison.OrdinalIgnoreCase);

    // Plate metadata for the H2 start payload, read from the
    // downloaded .3mf at localPath. Returns (filamentCount,
    // usedPositions, md5). Never throws: any unreadable piece
    // falls back (count 1, used [0], md5 "") and the failure is
    // logged — a metadata failure must not block the start.
    // filamentCount follows bambu-printer-mcp's
    // resolveProjectFileMetadata: the authoritative source is
    // the gcode header "; filament_colour = ..." (count of
    // ;-separated colours), falling back to "; filament_ids = ...".
    // usedPositions come from Metadata/plate_1.json filament_ids.
    // md5 is the MD5 of Metadata/plate_1.gcode — read from the
    // slicer's Metadata/plate_1.gcode.md5 entry when present,
    // else computed over the entry bytes.
    private static (int FilamentCount, int[] UsedPositions, string Md5) ReadPlateMetadata(string? localPath)
    {
        var fallback = (1, new[] { 0 }, "");
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
            return fallback;
        try
        {
            using var zip = new ZipArchive(File.OpenRead(localPath), ZipArchiveMode.Read);
            var gcodeEntry = zip.GetEntry("Metadata/plate_1.gcode");
            if (gcodeEntry is null)
                return fallback;
            var filamentCount = 1;
            var usedPositions = new[] { 0 };
            var md5 = "";
            var md5Entry = zip.GetEntry("Metadata/plate_1.gcode.md5");
            if (md5Entry is not null)
            {
                using var md5Reader = new StreamReader(md5Entry.Open());
                var text = md5Reader.ReadToEnd().Trim().ToLowerInvariant();
                if (text.Length == 32 && Regex.IsMatch(text, "^[0-9a-f]{32}$"))
                    md5 = text;
            }
            using (var gcodeStream = gcodeEntry.Open())
            {
                var head = new byte[32 * 1024];
                var read = 0;
                while (read < head.Length)
                {
                    var n = gcodeStream.Read(head, read, head.Length - read);
                    if (n == 0) break;
                    read += n;
                }
                var headText = Encoding.UTF8.GetString(head, 0, read);
                var colour = Regex.Match(headText, @";\s*filament_colour\s*=\s*([^\r\n]+)", RegexOptions.IgnoreCase);
                if (colour.Success)
                {
                    filamentCount = Math.Max(1, colour.Groups[1].Value.Split(';').Count(s => !string.IsNullOrWhiteSpace(s)));
                }
                else
                {
                    var ids = Regex.Match(headText, @";\s*filament_ids\s*=\s*([^\r\n]+)", RegexOptions.IgnoreCase);
                    if (ids.Success)
                        filamentCount = Math.Max(1, ids.Groups[1].Value.Split(';').Count(s => !string.IsNullOrWhiteSpace(s)));
                }
            }
            if (string.IsNullOrEmpty(md5))
            {
                // No usable .md5 entry: compute over the gcode bytes.
                using var fullStream = gcodeEntry.Open();
                using var md5Hash = MD5.Create();
                md5 = Convert.ToHexString(md5Hash.ComputeHash(fullStream)).ToLowerInvariant();
            }
            var jsonEntry = zip.GetEntry("Metadata/plate_1.json");
            if (jsonEntry is not null)
            {
                try
                {
                    using var jsonReader = new StreamReader(jsonEntry.Open());
                    using var doc = JsonDocument.Parse(jsonReader.ReadToEnd());
                    if (doc.RootElement.TryGetProperty("filament_ids", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array)
                    {
                        var ids = idsEl.EnumerateArray()
                            .Where(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out _))
                            .Select(e => e.GetInt32())
                            .Where(i => i >= 0)
                            .ToArray();
                        if (ids.Length > 0)
                            usedPositions = ids;
                    }
                }
                catch { /* malformed plate_1.json: keep [0] */ }
            }
            filamentCount = Math.Max(Math.Max(filamentCount, usedPositions.Max() + 1), 1);
            return (filamentCount, usedPositions, md5);
        }
        catch (Exception ex)
        {
            Program.Log($"[Bambu] Plate metadata read failed ({ex.GetType().Name}: {ex.Message}); using single-filament defaults.");
            return fallback;
        }
    }

    // H2 ams_mapping2 entry (bambu-printer-mcp ams-mapping.ts,
    // bridgeAmsMapping2Entry, single-nozzle form): tray values
    // 0..n map to ams_id = tray / 4, slot_id = tray % 4; -1 (and
    // the 255 virtual tray) mean unused.
    // Dual-nozzle form (same function, dualNozzle = true):
    // virtual trays carry slot_id 0 instead of echoing the
    // virtual id ({254,0} / {255,0}), and AMS-HT trays
    // (>= 128) carry {ams_id: value, slot_id: 0}. For real
    // AMS trays (0..n) both forms agree ({ams_id: 0,
    // slot_id: 0} for tray 0), so today's single-slot tray-0
    // jobs are unaffected — the difference only matters for
    // external-spool / AMS-HT jobs.
    private static object H2Mapping2Entry(int v, bool dualNozzle = false)
    {
        if (v < 0) return new { ams_id = 255, slot_id = 255 };
        if (dualNozzle)
        {
            if (v == 255) return new { ams_id = 255, slot_id = 0 };
            if (v == 254) return new { ams_id = 254, slot_id = 0 };
            if (v >= 128) return new { ams_id = v, slot_id = 0 };
        }
        else
        {
            if (v == 255) return new { ams_id = 255, slot_id = 255 };
            if (v == 254) return new { ams_id = 254, slot_id = 254 };
            if (v >= 128) return new { ams_id = 128, slot_id = v - 128 };
        }
        return new { ams_id = v / 4, slot_id = v % 4 };
    }

    // X2D nozzle_mapping (2026.1108): the raw MQTT
    // project_file command on multi-extruder printers carries
    // nozzle_mapping, a flat int array, 3MF filament slot ->
    // physical nozzle (ChestnutLabs printer-protocol-orchard,
    // protocols/bambu.md, "Print-time routing": "nozzle_mapping
    // (flat int array, 3MF-slot -> physical nozzle) on
    // multi-extruder / H2D only"). bambu-printer-mcp never
    // constructs this value — it is a caller-supplied raw JSON
    // string for FULU PrintParams / the native helper — so it
    // is composed here from the plate's own slicer metadata.
    // Composition (bambu-printer-mcp index.ts:3378 — "the gcode
    // header's filament_map (1=left, 2=right) says which nozzle
    // each filament was sliced for"; CHANGELOG 1.1.29 — "Bind
    // each selected spool to the inspected plate's composed
    // filament_map and physical_extruder_map"):
    //   physical nozzle for slot i =
    //     physical_extruder_map[filament_map[i] - 1]
    // filament_map is 1-based (1 = left, 2 = right);
    // physical_extruder_map is indexed by 1-based logical
    // extruder minus 1 and holds 0-based physical nozzle ids.
    // Verified against the real X2D collar plate
    // (Ryobi-Stick-Collar-1x-Support.gcode.3mf):
    //   filament_map = [1,1,1,1],
    //   physical_extruder_map = [1,0]
    //   -> nozzle_mapping = [1,1,1,1] (every slot -> physical
    //   nozzle 1, the left/deputy nozzle; cross-checked by the
    //   plate's start G-code, which heats T1:
    //   M104 ... T{filament_map[0] % 2} = T1).
    // The array is project-length, parallel to ams_mapping.
    // Returns null when the maps cannot be read or fail
    // validation — the caller must NOT guess a value.
    // Never throws.
    private static int[]? ReadNozzleMapping(string? localPath, int filamentCount)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath) || filamentCount < 1)
            return null;
        try
        {
            using var zip = new ZipArchive(File.OpenRead(localPath), ZipArchiveMode.Read);
            // Bambu Studio writes the project settings at
            // Metadata/project_settings.config (the reference's
            // 3mf_parser.ts reads that path); fall back to the
            // archive root for plates written otherwise.
            var configEntry = zip.GetEntry("Metadata/project_settings.config")
                ?? zip.GetEntry("project_settings.config");
            if (configEntry is null) return null;
            using var configReader = new StreamReader(configEntry.Open());
            using var doc = JsonDocument.Parse(configReader.ReadToEnd());
            if (!doc.RootElement.TryGetProperty("filament_map", out var mapEl) || mapEl.ValueKind != JsonValueKind.Array)
                return null;
            if (!doc.RootElement.TryGetProperty("physical_extruder_map", out var physEl) || physEl.ValueKind != JsonValueKind.Array)
                return null;
            var filamentMap = ParseIntArray(mapEl);
            var physMap = ParseIntArray(physEl);
            if (filamentMap is null || physMap is null) return null;
            if (filamentMap.Length < filamentCount) return null;
            if (physMap.Length < 2) return null;
            var result = new int[filamentCount];
            for (var i = 0; i < filamentCount; i++)
            {
                var logical = filamentMap[i]; // 1-based: 1 = left, 2 = right
                if (logical < 1 || logical > 2) return null;
                if (logical - 1 >= physMap.Length) return null;
                var physical = physMap[logical - 1];
                if (physical < 0 || physical > 1) return null;
                result[i] = physical;
            }
            return result;
        }
        catch (Exception ex)
        {
            Program.Log($"[Bambu] Nozzle mapping read failed ({ex.GetType().Name}: {ex.Message}); no nozzle_mapping will be sent.");
            return null;
        }
    }

    // Parse a JSON array whose elements are ints or int-valued
    // strings (Bambu Studio writes filament_map /
    // physical_extruder_map as string arrays, e.g. ["1","1"]).
    // Returns null if any element is neither.
    private static int[]? ParseIntArray(JsonElement el)
    {
        var list = new List<int>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n))
            {
                list.Add(n);
                continue;
            }
            if (item.ValueKind == JsonValueKind.String &&
                int.TryParse(item.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var s))
            {
                list.Add(s);
                continue;
            }
            return null;
        }
        return list.ToArray();
    }

    // H2 mapping (bambu-printer-mcp PR #15 / current main):
    // projLen = max(projectFilamentCount, baseMapping.length, 1);
    // baseMapping is the project-length array with the plate's
    // used positions set to the job's tray and the rest -1.
    // The bridge only ever sends single-slot jobs (the AMS gate
    // holds anything else before it reaches StartPrint), and the
    // tray has always been hardcoded to 0 — so the used positions
    // map to tray 0 here, preserving current behaviour. Wiring
    // the job's AMS slot through to the start payload is a
    // separate follow-up.
    private static (int[] Mapping, object[] Mapping2) BuildH2Mapping(int filamentCount, int[] usedPositions, bool dualNozzle = false)
    {
        var baseMapping = new int[Math.Max(filamentCount, 1)];
        for (var i = 0; i < baseMapping.Length; i++) baseMapping[i] = -1;
        foreach (var pos in usedPositions)
        {
            if (pos >= 0 && pos < baseMapping.Length) baseMapping[pos] = 0;
        }
        var projLen = Math.Max(Math.Max(filamentCount, baseMapping.Length), 1);
        var mapping = new int[projLen];
        for (var i = 0; i < projLen; i++)
        {
            var v = i < baseMapping.Length ? baseMapping[i] : -1;
            // Dual-nozzle flat form (bambu-printer-mcp
            // amsMappingDualNozzle / bridgeAmsMappingValue):
            // virtual trays 254/255 are -1 here — their real
            // routing lives in ams_mapping2 only. Unreachable
            // today (the tray is hardcoded to 0 above); the
            // guard exists so the future AMS-slot wiring gets
            // the dual form right.
            mapping[i] = (dualNozzle && (v == 254 || v == 255)) ? -1 : v;
        }
        // mapping2 is built from the PRE-rewrite tray values —
        // the reference's nativeAmsMapping2(mapping, dualNozzle)
        // takes the raw mapping, not the flat -1 form — so a
        // virtual tray still reaches {254,0} / {255,0} here
        // instead of collapsing to {255,255}.
        var mapping2 = new object[projLen];
        for (var i = 0; i < projLen; i++)
        {
            var v = i < baseMapping.Length ? baseMapping[i] : -1;
            mapping2[i] = H2Mapping2Entry(v, dualNozzle);
        }
        return (mapping, mapping2);
    }

    public async Task<(bool Ok, string Reason)> StartPrintAsync(string fileName, string jobKey, CancellationToken ct, string? localPath = null)
    {
        // Repair dashboard-mangled plate filenames (see
        // RepairPlateFileName): the dashboard sanitizes ".gcode.3mf"
        // to ".gcode_.3mf", and WordPress/Media dedup renames it to
        // ".gcode-<digits>.3mf"; Bambu printers reject both forms.
        var startFileName = RepairPlateFileName(fileName);
        if (!string.Equals(startFileName, fileName, StringComparison.Ordinal))
            Program.Log($"[Bambu] Repaired mangled plate filename '{fileName}' -> '{startFileName}' for start");
        // Community-documented start shape; the Worker verifies via
        // status polls that printing actually began, and fails the
        // job if it did not within two minutes.
        var isPlateFile = startFileName.EndsWith(".gcode.3mf", StringComparison.OrdinalIgnoreCase) || startFileName.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase);
        var startParam = isPlateFile ? "Metadata/plate_1.gcode" : startFileName;
        // Every Bambu start uses the H2-style project_file
        // payload (see the H2-ONLY note above) — there is no
        // legacy path anymore. The Worker verifies via status
        // polls that printing actually began, and fails the job
        // if it did not within two minutes.
        var isDualNozzle = IsDualNozzle;
        var print = new Dictionary<string, object>
        {
            ["sequence_id"] = "1",
            ["command"] = "project_file",
            ["param"] = startParam,
            ["file"] = startFileName,
            ["bed_type"] = "auto",
            ["use_ams"] = true,
        };
        // Submission id: bambu-printer-mcp's exactly —
        // String(Date.now() & 0x7fffffff), the same value for
        // project_id, task_id and subtask_id. Guard the
        // (vanishingly unlikely) zero so the ids are never "0".
        var rawId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & 0x7fffffff;
        var submissionId = (rawId == 0 ? 1 : rawId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (filamentCount, usedPositions, md5) = isPlateFile
            ? ReadPlateMetadata(localPath)
            : (1, new[] { 0 }, "");
        var (mapping, mapping2) = BuildH2Mapping(filamentCount, usedPositions, isDualNozzle);
        // nozzle_mapping is logged below when present; the
        // log line reads it.
        var nozzleLog = "";
        if (isDualNozzle)
        {
            // Dual-nozzle printers (X2D) need nozzle_mapping:
            // a flat int array, 3MF filament slot -> physical
            // nozzle, derived from the plate's own slicer
            // metadata (see ReadNozzleMapping). If it cannot
            // be derived, REFUSE the dispatch instead of
            // sending a payload the printer is likely to
            // reject with 0500-4002 — and never guess a value.
            // A non-plate file has no plate metadata to
            // derive from; Bambu prints only go as packaged
            // .gcode.3mf plates, so that case is refused too.
            int[]? nozzleMapping = null;
            if (isPlateFile)
                nozzleMapping = ReadNozzleMapping(localPath, filamentCount);
            if (nozzleMapping is null)
            {
                var reason = isPlateFile
                    ? $"Cannot determine nozzle_mapping for dual-nozzle printer '{Name}' from plate '{startFileName}': project_settings.config is missing or its filament_map / physical_extruder_map are unreadable. Re-slice the plate in Bambu Studio and retry."
                    : $"Cannot determine nozzle_mapping for dual-nozzle printer '{Name}': '{startFileName}' is not a packaged .gcode.3mf plate. Send a packaged plate instead.";
                Program.Log($"[Bambu] StartPrint refused: {reason}");
                return (false, reason);
            }
            print["nozzle_mapping"] = nozzleMapping;
            nozzleLog = $" nozzle_mapping=[{string.Join(",", nozzleMapping)}]";
        }
        print["url"] = "ftp:///cache/" + startFileName;
        print["md5"] = md5;
        // 0/1 int flags here (the driver sends ints,
        // not booleans). bed_leveling 1 + auto_bed_leveling 1
        // preserve the bridge's existing always-level intent as
        // a coherent pair; Studio's own default is (0, 2)
        // ("auto") if the owner ever wants that instead.
        // nozzle_offset_cali 2 is Studio's "auto" default for a
        // single-nozzle P2S. subtask_name drops the ".3mf", as
        // the driver does.
        print["timelapse"] = 0;
        print["bed_leveling"] = 1;
        print["auto_bed_leveling"] = 1;
        print["flow_cali"] = 0;
        print["vibration_cali"] = 0;
        print["layer_inspect"] = 0;
        print["cfg"] = "0";
        print["extrude_cali_flag"] = 0;
        print["extrude_cali_manual_mode"] = 0;
        print["nozzle_offset_cali"] = 2;
        print["subtask_name"] = Regex.Replace(startFileName, @"\.3mf$", "", RegexOptions.IgnoreCase);
        print["profile_id"] = "0";
        print["project_id"] = submissionId;
        print["task_id"] = submissionId;
        print["subtask_id"] = submissionId;
        print["ams_mapping"] = mapping;
        print["ams_mapping2"] = mapping2;
        var h2Label = isDualNozzle ? "X2D" : "P2S";
        Program.Log($"[Bambu] StartPrint H2-style ({h2Label}) submissionId={submissionId} filaments={filamentCount} mapping=[{string.Join(",", mapping)}]{nozzleLog} md5={(string.IsNullOrEmpty(md5) ? "(empty)" : "set")}");
        var command = new { print };
        // 2026.1105-push-log: concise StartPrint record (file / subtask_name / param).
        Program.Log($"[Bambu] StartPrint publish: file={startFileName} subtask_name={print["subtask_name"]} param={startParam}");
        Program.Log($"[Bambu] StartPrint payload: {JsonSerializer.Serialize(command)}");
        var startResult = await PublishCommandAsync(command, ct);
        Program.Log($"[Bambu] StartPrint publish result: ok={startResult.Ok} reason={startResult.Reason}");
        return startResult;
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
        // LEDCTRL-FIX (2026.1105): full community-documented
        // ledctrl form (Home Assistant X1/X1C MQTT thread,
        // Jan 2023, verbatim printer traffic; inferno2minus
        // bambulab.py gist). The earlier minimal form sent
        // only node + mode — a subset the printer may
        // ignore; the documented form carries the four
        // timing fields too. user_id "seems to not matter"
        // per the thread; the documented value is used so
        // the payload matches the proven form exactly.
        var command = new
        {
            system = new
            {
                sequence_id = "1",
                command = "ledctrl",
                led_node = "chamber_light",
                led_mode = on ? "on" : "off",
                led_on_time = 500,
                led_off_time = 500,
                loop_times = 0,
                interval_time = 0,
            },
            user_id = "123456789",
        };
        return PublishCommandAsync(command, ct);
    }

    /// <summary>GATE-LIGHT BUILD (2026.1105): read the
    /// chamber-light mode the printer itself reports, from
    /// the push report's lights_report. CORRECTED 2026.1105:
    /// lights_report lives under PRINT, not system —
    /// verbatim printer report (HA X1/X1C MQTT thread):
    /// {"print":{...,"lights_report":[{"node":
    /// "chamber_light","mode":"on"}],...}}. The original
    /// system-only read missed it entirely, which is why
    /// the 2026.1105 diagnosis ("system: present,
    /// lights_report: absent") could never confirm the
    /// light even if the printer was reporting it. Print
    /// is read first; system is kept as a fallback for
    /// firmware variants that report it there. Shape:
    /// array of {node, mode} objects. A plain object with
    /// a chamber_light property is also accepted, so a
    /// firmware variant does not silently read as "no
    /// report". Anything else (missing sections, wrong
    /// shape, any mode other than on/off) is null =
    /// unreported, never a guess.</summary>
    /// <summary>DIAG BUILD (2026.1105): one-line description
    /// of what a fresh report actually carries for
    /// lights_report, so a failed verification leaves
    /// evidence instead of silence. Describes BOTH the
    /// print and system sections, so one log line settles
    /// whether the field is under print, under system, or
    /// nowhere; shows the raw value truncated to 200 chars
    /// — never a whole report.</summary>
    private static string DescribeLightsReport(JsonElement root)
    {
        try
        {
            var printDesc = DescribeSectionLightsReport(root, "print");
            var systemDesc = DescribeSectionLightsReport(root, "system");
            return $"print: {printDesc}; system: {systemDesc}";
        }
        catch { return "lights_report: unreadable"; }
    }

    private static string DescribeSectionLightsReport(JsonElement root, string section)
    {
        if (!root.TryGetProperty(section, out var sec)) return $"{section}: absent";
        if (!sec.TryGetProperty("lights_report", out var report)) return $"{section}: present, lights_report: absent";
        var raw = report.GetRawText();
        if (raw.Length > 200) raw = raw.Substring(0, 200) + "…";
        var kind = report.ValueKind.ToString().ToLowerInvariant();
        var extra = report.ValueKind == JsonValueKind.Array ? $" len={report.GetArrayLength()}" : "";
        return $"lights_report: {kind}{extra} raw={raw}";
    }

    private static string? ParseLightsReport(JsonElement root)
    {
        try
        {
            // lights_report is reported under PRINT (see
            // doc comment above); system is a fallback only.
            return ParseSectionLightsReport(root, "print")
                ?? ParseSectionLightsReport(root, "system");
        }
        catch { return null; }
    }

    private static string? ParseSectionLightsReport(JsonElement root, string section)
    {
        try
        {
            if (!root.TryGetProperty(section, out var sec)) return null;
            if (!sec.TryGetProperty("lights_report", out var report)) return null;
            if (report.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in report.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    var node = entry.TryGetProperty("node", out var n) ? n.GetString() ?? "" : "";
                    if (!string.Equals(node.Trim(), "chamber_light", StringComparison.OrdinalIgnoreCase)) continue;
                    var mode = entry.TryGetProperty("mode", out var m) ? m.GetString() ?? "" : "";
                    return NormalizeLightMode(mode);
                }
                return null;
            }
            if (report.ValueKind == JsonValueKind.Object &&
                report.TryGetProperty("chamber_light", out var cl))
            {
                if (cl.ValueKind == JsonValueKind.String) return NormalizeLightMode(cl.GetString() ?? "");
                if (cl.ValueKind == JsonValueKind.Object && cl.TryGetProperty("mode", out var cm))
                    return NormalizeLightMode(cm.GetString() ?? "");
            }
            return null;
        }
        catch { return null; }
    }

    private static string? NormalizeLightMode(string mode)
    {
        var m = (mode ?? "").Trim().ToLowerInvariant();
        return m is "on" or "off" ? m : null;
    }

    /// <summary>GATE-LIGHT BUILD (2026.1105): turn the chamber
    /// light on AND verify the printer confirms it. A bare
    /// PublishCommand only proves the MQTT publish reached
    /// the broker — the old lights control treated that as
    /// "on", which is why the dashboard showed Light: On
    /// while the bed snapshots stayed dark. This publishes
    /// ledctrl chamber_light on, then fetches a fresh pushall
    /// report and reads lights_report for the chamber_light
    /// mode. Verified only when the printer itself reports
    /// mode "on". Logs the publish result and the observed
    /// mode on every call (calls are infrequent: lights
    /// controls and throttled pre-snapshot ensures).</summary>
    public async Task<(bool Verified, string ObservedMode, string Reason)> VerifyChamberLightOnAsync(CancellationToken ct)
    {
        var (ok, reason) = await SetChamberLedAsync(true, ct);
        Program.Log(ok
            ? $"[Bambu] Chamber light ledctrl publish sent to '{Name}'."
            : $"[Bambu] Chamber light ledctrl publish to '{Name}' failed: {reason}");
        if (!ok) return (false, _verifiedLightMode, reason);
        var report = await FetchReportAsync(ct);
        if (report is null)
        {
            // DIAG BUILD (2026.1105): the no-report branch
            // previously returned its reason silently; the
            // caller (pre-snapshot ensure) discards the
            // reason, so nothing in the log said why.
            Program.Log($"[Bambu] Chamber light verify on '{Name}': verification report request returned no report — not verified.");
            return (false, _verifiedLightMode, "Light command sent, but the printer did not answer the verification report request.");
        }
        try
        {
            using var doc = JsonDocument.Parse(report);
            var mode = ParseLightsReport(doc.RootElement);
            // DIAG BUILD (2026.1105): log what the report
            // actually carried, once per verify attempt.
            var desc = DescribeLightsReport(doc.RootElement);
            if (mode is null)
            {
                Program.Log($"[Bambu] Chamber light verify on '{Name}': {desc}; parsed mode: none — not verified.");
                return (false, _verifiedLightMode, "Light command sent, but the printer's report carries no lights_report, so the light state is unverified, not on.");
            }
            _verifiedLightMode = mode;
            _verifiedLightUtc = DateTime.UtcNow;
            Program.Log($"[Bambu] Chamber light on '{Name}': printer reports chamber_light mode '{mode}' ({desc}).");
            return (string.Equals(mode, "on", StringComparison.OrdinalIgnoreCase), mode, "");
        }
        catch (Exception ex)
        {
            // DIAG BUILD (2026.1105): same silent-return
            // problem as the no-report branch — log it.
            Program.Log($"[Bambu] Chamber light verify on '{Name}': verification report could not be read: {ex.Message} — not verified.");
            return (false, _verifiedLightMode, $"Light command sent, but the verification report could not be read: {ex.Message}");
        }
    }

    /// <summary>Throttled ensure for bed-view snapshots: make
    /// sure the chamber light is verified on before a
    /// snapshot grab, at most once every 10 minutes per
    /// printer while it is not already verified on. A
    /// snapshot grab must never wait on a light command that
    /// keeps failing — a failed ensure logs inside the
    /// verify call and returns false, and the caller still
    /// grabs the frame so a dark frame is visible in the log
    /// instead of no frame at all.</summary>
    public async Task<bool> EnsureChamberLightOnAsync(CancellationToken ct)
    {
        // Already verified on, recently: nothing to do.
        if (string.Equals(_verifiedLightMode, "on", StringComparison.OrdinalIgnoreCase) &&
            DateTime.UtcNow - _verifiedLightUtc < TimeSpan.FromMinutes(10))
            return true;
        // A failed ensure less than 10 minutes ago: do not
        // hammer the printer on every snapshot tick.
        if (DateTime.UtcNow - _lastLightEnsureUtc < TimeSpan.FromMinutes(10) && !_lastLightEnsureOk)
            return false;
        _lastLightEnsureUtc = DateTime.UtcNow;
        var (verified, _, _) = await VerifyChamberLightOnAsync(ct);
        _lastLightEnsureOk = verified;
        return verified;
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

    /// <summary>Reboot the printer via the Bambu MQTT system
    /// reboot command. Verified payload (bambulabs_api
    /// PrinterMQTTClient.reboot(), v2.6.4, BambuTools):
    /// {"system": {"command": "reboot"}} on
    /// device/{serial}/request — no sequence_id, no other
    /// fields, matching the verified form exactly. The
    /// publish only proves the command was sent, not that
    /// the printer rebooted; the printer's MQTT connection
    /// drops when it restarts, so the caller must expect a
    /// disconnect and allow ~30-60 s before treating the
    /// printer as down. P2S/X2D are unproven for this
    /// command (single source, no model-specific statement)
    /// — test on an idle printer first. Executed ONLY
    /// behind FarmPoller's live-state gate (idle/error
    /// only) — calling this method directly bypasses that
    /// gate, so only FarmPoller may call it.</summary>
    public Task<(bool Ok, string Reason)> RebootAsync(CancellationToken ct)
    {
        var command = new
        {
            system = new
            {
                command = "reboot",
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
