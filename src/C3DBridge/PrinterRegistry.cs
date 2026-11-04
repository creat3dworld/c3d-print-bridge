// C3D Print Bridge — printer registry (routing only).
//
// The dashboard assigns every job to a printer BY NAME. This
// registry resolves that name to the adapter that can reach it.
// An unknown name is an error reported to the dashboard — the
// bridge NEVER substitutes another printer, never picks the "best"
// printer, and never reorders anything. Printer-picking is the
// dashboard's job, not this relay's.

using C3DBridge.Printers;

namespace C3DBridge;

public sealed class PrinterRegistry
{
    private readonly object _gate = new();
    private Dictionary<string, IPrinterAdapter> _byName;

    public PrinterRegistry(IEnumerable<PrinterConfig> printers)
    {
        _byName = Build(printers);
    }

    private static Dictionary<string, IPrinterAdapter> Build(IEnumerable<PrinterConfig> printers)
    {
        var byName = new Dictionary<string, IPrinterAdapter>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in printers)
        {
            IPrinterAdapter adapter = p.Type switch
            {
                "bambu" => new BambuAdapter(p),
                "snapmaker" => new SnapmakerAdapter(p),
                _ => new GenericAdapter(p),
            };
            byName[p.Name] = adapter;
        }
        return byName;
    }

    public IReadOnlyList<IPrinterAdapter> All
    {
        get { lock (_gate) { return _byName.Values.ToList(); } }
    }

    public IPrinterAdapter? Find(string name)
    {
        lock (_gate) { return _byName.TryGetValue(name, out var adapter) ? adapter : null; }
    }

    /// <summary>AUTO-START BUILD — swap the whole adapter
    /// set for a reloaded config (IP-change auto-detection
    /// only). Adapters are stateless except for one-shot
    /// discovery flags, so a rebuild is safe mid-run; the
    /// Worker keeps polling by printer NAME, and the
    /// statuses dictionary is keyed by name, so a rebuild
    /// never orphans a live printer row. Never called for
    /// any other reason.</summary>
    public void Reload(IEnumerable<PrinterConfig> printers)
    {
        var rebuilt = Build(printers);
        lock (_gate) { _byName = rebuilt; }
    }
}
