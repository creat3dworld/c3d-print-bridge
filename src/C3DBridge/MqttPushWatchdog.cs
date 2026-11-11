// C3D Print Bridge — MQTT push watchdog (2026.1109.2140,
// CONTRACT.md §Q.4).
//
// Marlena, Nov 10, 2026: the X2D's live status keeps
// flickering offline while it prints, and the dashboard
// loses sight of it. Root cause, proven from the bridge
// logs: the Bambu MQTT push subscription goes SILENT after
// the initial snapshot at connect — one push per printer,
// then nothing, no error, no recovery — and the dashboard
// shows the printer UNKNOWN/offline (Start disabled) even
// though the bridge heartbeat is fresh. A bridge restart
// restores one fresh snapshot.
//
// The 2139 build already DETECTS the silence (it reports
// mqtt_push_silent once per episode). This watchdog adds
// the missing half: it ACTIVELY RECOVERS the feed.
//
// What it does, per loop tick (after the status poll):
//   - Tracks the last push time per Bambu printer
//     (BambuAdapter.LastPushUtc) and the printer's last
//     known non-offline state (printing / idle / ...).
//   - Printing: a printing printer pushes progress every
//     poll (30 s), so 5 minutes of silence = 10
//     consecutive missed polls = an unambiguous stall —
//     and it leaves 5 minutes to recover before
//     MonitorActiveJobsAsync fails the job at 10 minutes
//     offline. Recovery starts at 5 minutes.
//   - Idle: a printer in standby may not answer new
//     connections at all (proven Nov 10: ports timed
//     out), so recovering earlier would only hammer a
//     sleeping printer. Recovery starts at 10 minutes,
//     aligned with the mqtt_push_silent report.
//   - A recovery attempt is one longer-wait pushall cycle
//     (BambuAdapter.AttemptPushRecoveryAsync, 20 s instead
//     of the routine 6 s): a printer waking from standby,
//     or a broker busy with the second bridge's session,
//     may answer slowly. Attempts back off (2 min, then
//     5 min, then every 10 min) and never flap.
//   - The mqtt_push_silent error report still fires at
//     10 minutes, once per episode, exactly as 2139 did
//     (the contract defines it as "no Bambu push for 10
//     minutes" — reporting it at 5 minutes while printing
//     would be untrue). Recovery is NOT a new error code:
//     the bridge must not name things the dashboard has
//     not defined, so a recovery is logged locally and
//     signalled to the dashboard through the resumed
//     status push (the farm card flips back to live).
//     If the dashboard later defines a recovery code,
//     reporting it is a one-line change here.
//   - IP changes are NOT probed here: that is the
//     IpReconciler's job (10-minute cycle, probes other
//     LAN IPs for this serial). After 3 failed recovery
//     attempts the watchdog logs that reconciliation is
//     the escalation path.
//
// What it NEVER does: it never restarts the bridge, never
// touches a running print, never substitutes a printer,
// never changes config, never sends a start command. It
// only re-establishes the status feed. A recovery attempt
// runs inline in the loop tick (sequential, like the
// status poll — it cannot overlap a poll from this
// bridge), and at most one attempt per printer per tick;
// the 20-second wait is the cap, and a tick with no
// silent printer costs one timestamp compare per Bambu
// printer (the same as the 2139 watch).

using C3DBridge.Printers;

namespace C3DBridge;

/// <summary>MQTT push watchdog (2026.1109.2140): detects a
/// silent Bambu push feed and actively recovers it, on top
/// of the existing fresh-connect status polling. Owned by
/// Worker; runs once per loop tick, after the status poll.
/// Single-threaded (the worker loop is sequential), so no
/// locking: the per-printer episode state below is only
/// ever touched from CheckAsync.</summary>
public sealed class MqttPushWatchdog
{
    // No silence is declared until the bridge has been up
    // this long: a fresh bridge needs its first poll cycle
    // to establish LastPushUtc. Matches the 2139 watch and
    // the self-test skip window.
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(10);
    // CONTRACT.md §Q.4: mqtt_push_silent means "no Bambu
    // push for 10 minutes". The report fires at this age,
    // once per episode, whatever the printer was doing.
    private static readonly TimeSpan SilenceReportAfter = TimeSpan.FromMinutes(10);
    // Printing: 5 minutes = 10 consecutive missed 30 s
    // polls, an unambiguous stall, with 5 minutes left to
    // recover before the job is failed at 10 minutes
    // offline (MonitorActiveJobsAsync).
    private static readonly TimeSpan RecoveryAfterPrinting = TimeSpan.FromMinutes(5);
    // Idle: 10 minutes. A printer in standby may not
    // answer new connections at all; recovering earlier
    // would only hammer a sleeping printer.
    private static readonly TimeSpan RecoveryAfterIdle = TimeSpan.FromMinutes(10);

    private readonly ErrorReporter _errors;

    private sealed class Episode
    {
        // Last state the printer actually reported (never
        // "offline": a silent printer reports offline,
        // which says nothing about what it was doing when
        // the feed died). "" = never reported yet.
        public string LastKnownState = "";
        // When pushes stopped (the last push, or bridge
        // start if there never was one). MinValue = no
        // episode open.
        public DateTime SilenceSinceUtc = DateTime.MinValue;
        public bool SilenceReported;
        public bool IdentitySkipLogged;
        public int FailedRecoveryAttempts;
        public DateTime NextRecoveryAttemptUtc = DateTime.MinValue;
    }

    private readonly Dictionary<string, Episode> _episodes = new(StringComparer.OrdinalIgnoreCase);

    public MqttPushWatchdog(ErrorReporter errors)
    {
        _errors = errors;
    }

    /// <summary>Run once per worker-loop tick, after the
    /// status poll, with the tick's live statuses and the
    /// bridge start time. Detects silence, reports
    /// mqtt_push_silent once per episode (at 10 minutes,
    /// as 2139 did), and attempts recovery with backoff.
    /// Never throws: a failed error report is logged by
    /// the ErrorReporter, and a failed recovery attempt
    /// just schedules the next one.</summary>
    public async Task CheckAsync(IReadOnlyList<IPrinterAdapter> adapters, IReadOnlyDictionary<string, PrinterStatus> statuses, DateTime bridgeStartedUtc, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        foreach (var adapter in adapters)
        {
            if (adapter is not BambuAdapter bambu) continue;
            // BRIDGE PRIORITY (2026.1111.2142): a deferred
            // printer is deliberately not connected to, so
            // its silence is not silence — never report
            // mqtt_push_silent for it and never attempt
            // recovery. Any episode left open from before
            // the printer became deferred is closed quietly
            // (no report, no log spam on every tick).
            if (bambu.Deferred)
            {
                var openEpisode = GetEpisode(bambu.Name);
                if (openEpisode.SilenceSinceUtc != DateTime.MinValue)
                    ResetEpisode(openEpisode);
                continue;
            }
            var episode = GetEpisode(bambu.Name);

            // Remember the last state the printer actually
            // reported, before the silence check — so the
            // first tick after the startup grace already
            // knows whether the printer was printing.
            if (statuses.TryGetValue(bambu.Name, out var live) &&
                !string.Equals(live.State, "offline", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(live.State))
                episode.LastKnownState = live.State;

            if (now - bridgeStartedUtc < StartupGrace) continue;

            var lastPush = bambu.LastPushUtc == DateTime.MinValue ? bridgeStartedUtc : bambu.LastPushUtc;
            var silenceAge = now - lastPush;
            var printing = string.Equals(episode.LastKnownState, "printing", StringComparison.OrdinalIgnoreCase);
            var recoveryAfter = printing ? RecoveryAfterPrinting : RecoveryAfterIdle;

            if (silenceAge < recoveryAfter)
            {
                // Pushes resumed (or never went silent long
                // enough to matter): close the episode. Log
                // the recovery only if one was open.
                if (episode.SilenceSinceUtc != DateTime.MinValue)
                {
                    Program.Log($"[mqtt-watchdog] Pushes resumed on '{bambu.Name}' after {(int)(now - episode.SilenceSinceUtc).TotalMinutes} min silent.");
                    ResetEpisode(episode);
                }
                continue;
            }

            if (episode.SilenceSinceUtc == DateTime.MinValue)
                episode.SilenceSinceUtc = lastPush;

            // The silence report: at 10 minutes, once per
            // episode, exactly as 2139 did. (Recovery may
            // already have started at 5 minutes while
            // printing — the report still waits for the
            // contract's 10 minutes.)
            if (!episode.SilenceReported && silenceAge >= SilenceReportAfter)
            {
                episode.SilenceReported = true;
                await _errors.ReportAsync("mqtt_push_silent", "error", "mqtt",
                    $"No MQTT push from Bambu printer '{bambu.Name}' for over 10 minutes.",
                    "The bridge has received no Bambu push report from this printer in the last 10 minutes; the dashboard may show it UNKNOWN. The MQTT push watchdog is attempting recovery (longer-wait pushall, with backoff); the printer's status push will show live again if it recovers.",
                    "", "", ct);
                Program.Log($"[mqtt-watchdog] Reported mqtt_push_silent for '{bambu.Name}' (no push for {(int)silenceAge.TotalMinutes} min).");
            }

            // Recovery: a printer with no MQTT identity can
            // never push — skip attempts for this episode
            // (the dashboard already says so).
            if (!bambu.HasMqttIdentity)
            {
                if (!episode.IdentitySkipLogged)
                {
                    episode.IdentitySkipLogged = true;
                    Program.Log($"[mqtt-watchdog] Cannot recover '{bambu.Name}': no serial/access code in config — skipping recovery attempts for this episode.");
                }
                continue;
            }

            if (now < episode.NextRecoveryAttemptUtc) continue;

            var attemptNo = episode.FailedRecoveryAttempts + 1;
            Program.Log($"[mqtt-watchdog] Silence on '{bambu.Name}' ({(int)silenceAge.TotalMinutes} min old, last known {episode.LastKnownState}): recovery attempt #{attemptNo} (20s pushall).");
            var recovered = await bambu.AttemptPushRecoveryAsync(ct);
            if (recovered)
            {
                Program.Log($"[mqtt-watchdog] Recovered push from '{bambu.Name}' after {(int)silenceAge.TotalMinutes} min silence (attempt #{attemptNo}). The next status poll will report the printer live again.");
                episode.FailedRecoveryAttempts = 0;
                episode.NextRecoveryAttemptUtc = DateTime.MinValue;
            }
            else
            {
                episode.FailedRecoveryAttempts = attemptNo;
                var delay = BackoffDelay(attemptNo);
                episode.NextRecoveryAttemptUtc = now + delay;
                var line = $"[mqtt-watchdog] Recovery attempt #{attemptNo} for '{bambu.Name}' failed (no push in 20s); next attempt at {episode.NextRecoveryAttemptUtc:HH:mm:ss} UTC (backoff {delay}).";
                if (attemptNo == 3)
                    line += " If this printer moved IP, the 10-minute IP reconciliation cycle is the escalation path — it probes other LAN IPs for this serial.";
                Program.Log(line);
            }
        }
    }

    private Episode GetEpisode(string name)
    {
        if (!_episodes.TryGetValue(name, out var episode))
        {
            episode = new Episode();
            _episodes[name] = episode;
        }
        return episode;
    }

    private static void ResetEpisode(Episode episode)
    {
        episode.SilenceSinceUtc = DateTime.MinValue;
        episode.SilenceReported = false;
        episode.IdentitySkipLogged = false;
        episode.FailedRecoveryAttempts = 0;
        episode.NextRecoveryAttemptUtc = DateTime.MinValue;
    }

    // Back off between recovery attempts so a silent
    // printer (or a busy broker) is never hammered: 2
    // minutes after the first failure, 5 after the
    // second, then every 10 minutes.
    private static TimeSpan BackoffDelay(int failedAttempts) => failedAttempts switch
    {
        1 => TimeSpan.FromMinutes(2),
        2 => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(10),
    };
}
