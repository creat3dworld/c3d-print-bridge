using System.Runtime.InteropServices;

namespace C3DBridge;

// Encrypt secrets at rest on Windows with DPAPI (current user only).
// The file on disk then holds ciphertext, not the plain key/password,
// and only the same Windows user account can decrypt it. On non-Windows
// systems, or if DPAPI fails, values are stored plain (existing
// behaviour) with the file restricted to the owner, never silently
// lost. Plain values already on disk are read as-is and re-encrypted
// on the next save.
public static class SecureStore
{
    private const string Prefix = "dpapi:";

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, out string? pszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return plain ?? "";
        if (!OperatingSystem.IsWindows()) return plain;
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(plain);
            var inBlob = ToBlob(bytes);
            try
            {
                if (!CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x01, out var outBlob))
                    return plain;
                try { return Prefix + Convert.ToBase64String(FromBlob(outBlob)); }
                finally { LocalFree(outBlob.pbData); }
            }
            finally { Marshal.FreeHGlobal(inBlob.pbData); }
        }
        catch { return plain; }
    }

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;
        if (!OperatingSystem.IsWindows()) return stored;
        try
        {
            var bytes = Convert.FromBase64String(stored[Prefix.Length..]);
            var inBlob = ToBlob(bytes);
            try
            {
                if (!CryptUnprotectData(ref inBlob, out _, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x01, out var outBlob))
                    return stored;
                try { return System.Text.Encoding.UTF8.GetString(FromBlob(outBlob)); }
                finally { LocalFree(outBlob.pbData); }
            }
            finally { Marshal.FreeHGlobal(inBlob.pbData); }
        }
        catch { return stored; }
    }

    private static DataBlob ToBlob(byte[] bytes)
    {
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return new DataBlob { cbData = bytes.Length, pbData = ptr };
    }

    private static byte[] FromBlob(DataBlob blob)
    {
        var bytes = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, bytes, 0, blob.cbData);
        return bytes;
    }
}
