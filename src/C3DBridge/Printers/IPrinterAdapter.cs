// C3D Print Bridge — printer adapter contract.
//
// One adapter per printer TYPE (not per printer). Adapters do three
// things and nothing else:
//   1. Report live state (idle/printing/paused/error/offline) and
//      AMS material/color when the printer actually reports them.
//   2. Accept a file the dashboard already sliced, upload it to the
//      printer, and forward a start command the dashboard ordered.
//   3. Capture a still snapshot when a still endpoint is configured
//      or auto-discovered (2275, brand-local authenticated
//      endpoints only, JPEG-validated) and reachable —
//      otherwise report that none is available.
//
// Adapters NEVER pick jobs, order queues, slice, or decide anything
// about the business. Any failure is reported with its reason.

namespace C3DBridge.Printers;

public interface IPrinterAdapter
{
    string Name { get; }
    string Type { get; }
    string Ip { get; }

    /// <summary>Current live state. Never throws: on any failure it
    /// returns state "offline" with the reason in Detail.</summary>
    Task<PrinterStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>Upload an already-sliced file to the printer.
    /// Returns (ok, reason). A failure here never starts a print.</summary>
    Task<(bool Ok, string Reason)> UploadFileAsync(string localPath, string fileName, CancellationToken ct);

    /// <summary>Forward a start command for a file already uploaded.
    /// Returns (ok, reason). The caller (Worker) verifies via later
    /// status polls that the printer actually started printing.</summary>
    Task<(bool Ok, string Reason)> StartPrintAsync(string fileName, string jobKey, CancellationToken ct);

    /// <summary>Capture a still image when one is available, as JPEG
    /// bytes. Returns null when no still can be captured — never a
    /// placeholder, never a stock image.</summary>
    Task<byte[]?> TryGetSnapshotAsync(CancellationToken ct);

    /// <summary>Label for the snapshot source sent to the
    /// dashboard: "owner_snapshot_url", or a distinct
    /// "auto_discovered_*" value once (2275) auto-discovery
    /// has validated and saved a camera URL for this printer.</summary>
    string SnapshotSource { get; }

    /// <summary>Camera auto-discovery (2275). Runs at most once
    /// per adapter instance (once per Worker startup, which is
    /// also what happens when a printer is added via Configure,
    /// since Configure restarts the Worker). Returns the
    /// discovered snapshot URL, or null when the printer
    /// already has a URL set, when discovery is not supported
    /// for this brand, or when nothing valid was found.
    /// NEVER overwrites an owner-set URL, and NEVER returns a
    /// URL that did not validate as a real JPEG. The caller
    /// (Worker) persists a returned URL to the LOCAL bridge
    /// config only; no config or credential is ever sent to
    /// the dashboard — only the snapshot JPEG itself.</summary>
    Task<string?> TryAutoDiscoverSnapshotUrlAsync(CancellationToken ct);
}
