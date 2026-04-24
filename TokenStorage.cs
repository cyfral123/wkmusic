using System;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;

namespace WKMusic;

public class TokenStorage(string credentialName = "WKMusic/SpotifyToken")
{
    private readonly string CredentialName = credentialName;

    public void Save(SpotifyToken token)
    {
        var json = JsonConvert.SerializeObject(new StoredToken(
            token.AccessToken, token.RefreshToken, token.ExpiresIn, token.ObtainedAt));

        var blob = Encoding.UTF8.GetBytes(json);

        var cred = new NativeMethods.CREDENTIAL
        {
            Type = NativeMethods.CRED_TYPE_GENERIC,
            TargetName = CredentialName,
            CredentialBlob = Marshal.AllocHGlobal(blob.Length),
            CredentialBlobSize = (uint)blob.Length,
            Persist = NativeMethods.CRED_PERSIST_LOCAL_MACHINE,
            UserName = "spotify",
        };

        Marshal.Copy(blob, 0, cred.CredentialBlob, blob.Length);

        try
        {
            if (!NativeMethods.CredWrite(ref cred, 0))
                throw new Exception($"CredWrite failed: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.FreeHGlobal(cred.CredentialBlob);
        }
    }

    public SpotifyToken? Load()
    {
        if (!NativeMethods.CredRead(CredentialName, NativeMethods.CRED_TYPE_GENERIC, 0, out var credPtr))
            return null;

        try
        {
            var cred = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(credPtr);
            var blob = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, blob, 0, blob.Length);

            var json = Encoding.UTF8.GetString(blob);
            var stored = JsonConvert.DeserializeObject<StoredToken>(json);
            if (stored is null) return null;

            return new SpotifyToken(stored.AccessToken, stored.RefreshToken, stored.ExpiresIn, stored.ObtainedAt);
        }
        catch
        {
            Clear();
            return null;
        }
        finally
        {
            NativeMethods.CredFree(credPtr);
        }
    }

    public void Clear() =>
        NativeMethods.CredDelete(CredentialName, NativeMethods.CRED_TYPE_GENERIC, 0);

    private class StoredToken
    {
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public int ExpiresIn { get; set; }
        public DateTime ObtainedAt { get; set; }

        public StoredToken()
        {
        }

        public StoredToken(string access, string refresh, int expiresIn, DateTime obtainedAt)
        {
            AccessToken = access;
            RefreshToken = refresh;
            ExpiresIn = expiresIn;
            ObtainedAt = obtainedAt;
        }
    }

    private static class NativeMethods
    {
        public const uint CRED_TYPE_GENERIC = 1;
        public const uint CRED_PERSIST_LOCAL_MACHINE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string? Comment;
            public long LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string? UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll")]
        public static extern void CredFree(IntPtr buffer);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        public static extern bool CredDelete(string target, uint type, uint flags);
    }
}