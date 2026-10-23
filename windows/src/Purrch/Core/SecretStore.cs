using System.Runtime.InteropServices;
using System.Text;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace Purrch.Core;

/// API keys in Windows Credential Manager (generic credentials named "Purrch:<name>"), never in plain files.
/// The Windows counterpart of the Mac app's keychain helper.
public static class SecretStore
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    private static string Target(string name) => $"Purrch:{name}";

    /// A key was saved or removed (with its name), so screens waiting on one can update.
    public static event Action<string>? Changed;

    /// The stored secret, or an empty string when there is none.
    public static string Get(string name)
    {
        if (!CredRead(Target(name), CRED_TYPE_GENERIC, 0, out var pointer)) return "";
        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return "";
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    /// Saves the secret; an empty value deletes it.
    public static void Set(string name, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            if (!CredDelete(Target(name), CRED_TYPE_GENERIC, 0) && Marshal.GetLastWin32Error() != ERROR_NOT_FOUND)
                Log.Write($"Credential Manager: could not delete {name} ({Marshal.GetLastWin32Error()})");
            Changed?.Invoke(name);
            return;
        }

        var bytes = Encoding.Unicode.GetBytes(value);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Target(name),
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = "Purrch",
            };
            if (!CredWrite(ref credential, 0))
                Log.Write($"Credential Manager: could not save {name} ({Marshal.GetLastWin32Error()})");
        }
        finally
        {
            // Don't leave the key lying around in unmanaged memory.
            for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(blob, i, 0);
            Marshal.FreeHGlobal(blob);
        }
        Changed?.Invoke(name);
    }
}
