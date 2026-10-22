// C3D Print Bridge — generic adapter for brands outside Bambu/Snapmaker.
//
// A generic printer's config is saved and shown in the dashboard,
// but live status polling is not supported yet for unknown
// firmwares — the bridge never fakes a status. It reports
// offline with a clear reason, and upload/start are refused
// honestly instead of guessing a protocol.
using C3DBridge;

namespace C3DBridge.Printers;

public sealed class GenericAdapter : IPrinterAdapter
{
    private readonly PrinterConfig _cfg;
    public GenericAdapter(PrinterConfig cfg) { _cfg = cfg; Name = cfg.Name; Type = cfg.Type; Ip = cfg.Ip; }
    public string Name { get; }
    public string Type { get; }
    public string Ip { get; }
    public Task<PrinterStatus> GetStatusAsync(CancellationToken ct)
        => Task.FromResult(new PrinterStatus { Name = Name, Type = Type, Ip = Ip, State = "offline", Detail = $"Generic brand '{_cfg.Type}' — no live adapter yet, config saved." });
    public Task<(bool Ok, string Reason)> UploadFileAsync(string localPath, string fileName, CancellationToken ct)
        => Task.FromResult((false, $"Generic brand '{_cfg.Type}' — upload not supported yet."));
    public Task<(bool Ok, string Reason)> StartPrintAsync(string fileName, string jobKey, CancellationToken ct)
        => Task.FromResult((false, $"Generic brand '{_cfg.Type}' — start not supported yet."));
    public async Task<byte[]?> TryGetSnapshotAsync(CancellationToken ct)
    {
        // Same rule as every other adapter: a snapshot only
        // when the owner set a snapshot URL. No URL = no image,
        // never a guessed URL and never a placeholder.
        if (string.IsNullOrWhiteSpace(_cfg.SnapshotUrl)) return null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var bytes = await http.GetByteArrayAsync(_cfg.SnapshotUrl, ct);
            if (bytes.Length < 100 || bytes.Length > 2_000_000) return null;
            if (bytes[0] != 0xFF || bytes[1] != 0xD8) return null;
            return bytes;
        }
        catch { return null; }
    }
}
