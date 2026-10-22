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
    private readonly Dictionary<string, IPrinterAdapter> _byName;

    public PrinterRegistry(IEnumerable<PrinterConfig> printers)
    {
        _byName = new Dictionary<string, IPrinterAdapter>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in printers)
        {
            IPrinterAdapter adapter = p.Type switch
            {
                "bambu" => new BambuAdapter(p),
                "snapmaker" => new SnapmakerAdapter(p),
                _ => new GenericAdapter(p),
            };
            _byName[p.Name] = adapter;
        }
    }

    public IReadOnlyList<IPrinterAdapter> All => _byName.Values.ToList();

    public IPrinterAdapter? Find(string name) =>
        _byName.TryGetValue(name, out var adapter) ? adapter : null;
}
