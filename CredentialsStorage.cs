using System;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;

namespace WKMusic;

public class CredentialsStorage
{
    private const string CredentialName = "WKMusic/SpotifyCredentials";

    public sealed record SpotifyCredentials(string ClientId, string ClientSecret);

    public void Save(string clientId, string clientSecret)
    {
        var json = JsonConvert.SerializeObject(new { ClientId = clientId, ClientSecret = clientSecret });
        var blob = Encoding.UTF8.GetBytes(json);

        var cred = new NativeMethods.CREDENTIAL
        {
            Type = NativeMethods.CRED_TYPE_GENERIC,
            TargetName = CredentialName,
            CredentialBlob = Marshal.AllocHGlobal(blob.Length),
            CredentialBlobSize = (uint)blob.Length,
            Persist = NativeMethods.CRED_PERSIST_LOCAL_MACHINE,
            UserName = "wkmusic",
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

    public SpotifyCredentials? Load()
    {
        if (!NativeMethods.CredRead(CredentialName, NativeMethods.CRED_TYPE_GENERIC, 0, out var ptr))
            return null;

        try
        {
            var cred = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(ptr);
            var blob = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, blob, 0, blob.Length);

            var obj = JsonConvert.DeserializeAnonymousType(
                Encoding.UTF8.GetString(blob),
                new { ClientId = "", ClientSecret = "" });

            if (obj is null || string.IsNullOrWhiteSpace(obj.ClientId)) return null;
            return new SpotifyCredentials(obj.ClientId, obj.ClientSecret);
        }
        catch
        {
            Clear();
            return null;
        }
        finally
        {
            NativeMethods.CredFree(ptr);
        }
    }

    public void Clear() =>
        NativeMethods.CredDelete(CredentialName, NativeMethods.CRED_TYPE_GENERIC, 0);

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