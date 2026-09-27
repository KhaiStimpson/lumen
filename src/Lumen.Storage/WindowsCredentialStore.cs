using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lumen.Domain;

namespace Lumen.Storage;

/// <summary>Generic credentials in Windows Credential Manager, stored UTF-16 like <c>cmdkey</c> does (TDD §39A).</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsCredentialStore : ISecretStore
{
    private const int GenericCredential = 1;
    private const int PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public bool IsAvailable => true;

    public string? Read(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!CredRead(name, GenericCredential, 0, out var pointer))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorNotFound ? null : throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return credential.CredentialBlobSize == 0
                ? ""
                : Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / sizeof(char));
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Write(string name, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(secret);

        var target = Marshal.StringToHGlobalUni(name);
        var user = Marshal.StringToHGlobalUni(Environment.UserName);
        var blob = Marshal.StringToHGlobalUni(secret);
        try
        {
            var credential = new Credential
            {
                Type = GenericCredential,
                TargetName = target,
                UserName = user,
                CredentialBlob = blob,
                CredentialBlobSize = secret.Length * sizeof(char),
                Persist = PersistLocalMachine,
            };

            if (!CredWrite(credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            Clear(blob, secret.Length * sizeof(char));
            Marshal.FreeHGlobal(blob);
            Marshal.FreeHGlobal(user);
            Marshal.FreeHGlobal(target);
        }
    }

    public bool Delete(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (CredDelete(name, GenericCredential, 0))
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        return error == ErrorNotFound ? false : throw new Win32Exception(error);
    }

    private static void Clear(IntPtr buffer, int length)
    {
        for (var i = 0; i < length; i++)
        {
            Marshal.WriteByte(buffer, i, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, int type, int flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(in Credential credential, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, int type, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);
}

/// <summary>Used where no secure store exists yet: nothing is stored, so connections that need a secret stay off.</summary>
public sealed class UnavailableSecretStore : ISecretStore
{
    public bool IsAvailable => false;

    public string? Read(string name) => null;

    public void Write(string name, string secret) =>
        throw new PlatformNotSupportedException("No secure credential store is available on this platform yet.");

    public bool Delete(string name) => false;
}
