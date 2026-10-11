using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RvtMcp.Plugin.Chat
{
    /// <summary>
    /// Stores chat-provider API keys encrypted with Windows DPAPI (CurrentUser scope).
    /// Keys never touch disk in plain text and can only be decrypted by the same Windows user.
    /// File: %LOCALAPPDATA%\Bimwright\rvt-mcp\chat\&lt;provider&gt;.key
    /// </summary>
    public static class ChatSecretStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("rvt-mcp-chat-v1");

        public static string KeyDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Bimwright", "rvt-mcp", "chat");
            }
        }

        private static string PathFor(string provider)
        {
            return Path.Combine(KeyDir, provider.ToLowerInvariant() + ".key");
        }

        public static void Save(string provider, string secret)
        {
            if (string.IsNullOrWhiteSpace(secret)) { Delete(provider); return; }
            Directory.CreateDirectory(KeyDir);
            File.WriteAllBytes(PathFor(provider), Protect(Encoding.UTF8.GetBytes(secret.Trim())));
        }

        /// <summary>Returns the stored key, else the provider's environment variable, else null.</summary>
        public static string Load(string provider)
        {
            try
            {
                var p = PathFor(provider);
                if (File.Exists(p)) return Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(p)));
            }
            catch { /* corrupted or other user: fall through */ }
            var env = provider == "anthropic" ? "ANTHROPIC_API_KEY" : provider == "openai" ? "OPENAI_API_KEY" : null;
            return env == null ? null : Environment.GetEnvironmentVariable(env);
        }

        public static bool Has(string provider) { return !string.IsNullOrEmpty(Load(provider)); }

        public static void Delete(string provider)
        {
            try { var p = PathFor(provider); if (File.Exists(p)) File.Delete(p); } catch { }
        }

        // ---- DPAPI via crypt32 (no extra NuGet; works on net48, net8, net10) ----
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr, ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        private static byte[] Protect(byte[] data) { return Run(data, true); }
        private static byte[] Unprotect(byte[] data) { return Run(data, false); }

        private static byte[] Run(byte[] data, bool protect)
        {
            var input = ToBlob(data);
            var entropy = ToBlob(Entropy);
            var output = new DATA_BLOB();
            try
            {
                bool ok = protect
                    ? CryptProtectData(ref input, "rvt-mcp chat key", ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);
                if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                if (input.pbData != IntPtr.Zero) Marshal.FreeHGlobal(input.pbData);
                if (entropy.pbData != IntPtr.Zero) Marshal.FreeHGlobal(entropy.pbData);
                if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
            }
        }

        private static DATA_BLOB ToBlob(byte[] data)
        {
            var blob = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
            Marshal.Copy(data, 0, blob.pbData, data.Length);
            return blob;
        }
    }
}
