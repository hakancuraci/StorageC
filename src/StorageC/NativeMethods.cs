using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StorageC;

internal static unsafe partial class NativeMethods
{
    internal const string LibraryName = "storagec_native";
    internal const int ErrorCapacity = 2048;

    static NativeMethods() => NativeLibrary.SetDllImportResolver(
        typeof(NativeMethods).Assembly,
        static (name, assembly, searchPath) =>
        {
            if (name != LibraryName) return IntPtr.Zero;
            string file = OperatingSystem.IsWindows() ? "storagec_native.dll" : "libstoragec_native.so";
            string rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
            string[] candidates =
            [
                Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", file),
                Path.Combine(AppContext.BaseDirectory, file)
            ];
            foreach (string candidate in candidates)
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out IntPtr handle)) return handle;
            return IntPtr.Zero;
        });

    [LibraryImport(LibraryName, EntryPoint = "sg_abi_version")]
    internal static partial int AbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "sg_client_create", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ClientCreate(string server, string share, string user, string password,
        string? domain, int port, int timeoutSeconds, int requireSigning, int requireEncryption,
        byte* error, nuint errorCapacity, out IntPtr client);

    [LibraryImport(LibraryName, EntryPoint = "sg_client_destroy")]
    internal static partial void ClientDestroy(IntPtr client);

    [LibraryImport(LibraryName, EntryPoint = "sg_open_read", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int OpenRead(SafeClientHandle client, string path, byte* error, nuint errorCapacity,
        out IntPtr file, out ulong fileSize);

    [LibraryImport(LibraryName, EntryPoint = "sg_open_write", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int OpenWrite(SafeClientHandle client, string path, int overwrite,
        byte* error, nuint errorCapacity, out IntPtr file);

    [LibraryImport(LibraryName, EntryPoint = "sg_file_read")]
    internal static partial int FileRead(SafeClientHandle client, SafeFileHandle file, byte* buffer,
        uint count, byte* error, nuint errorCapacity, out uint bytesRead);

    [LibraryImport(LibraryName, EntryPoint = "sg_file_write")]
    internal static partial int FileWrite(SafeClientHandle client, SafeFileHandle file, byte* buffer,
        uint count, byte* error, nuint errorCapacity, out uint bytesWritten);

    [LibraryImport(LibraryName, EntryPoint = "sg_file_flush")]
    internal static partial int FileFlush(SafeClientHandle client, SafeFileHandle file,
        byte* error, nuint errorCapacity);

    [LibraryImport(LibraryName, EntryPoint = "sg_file_close")]
    internal static partial int FileClose(IntPtr client, IntPtr file, byte* error, nuint errorCapacity);
}

internal sealed class SafeClientHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeClientHandle(IntPtr value) : base(true) => SetHandle(value);
    protected override bool ReleaseHandle() { NativeMethods.ClientDestroy(handle); return true; }
}

internal sealed unsafe class SafeFileHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly SafeClientHandle _client;
    private bool _clientRefAdded;

    internal SafeFileHandle(IntPtr value, SafeClientHandle client) : base(true)
    {
        _client = client;
        client.DangerousAddRef(ref _clientRefAdded);
        SetHandle(value);
    }

    protected override bool ReleaseHandle()
    {
        try { NativeMethods.FileClose(_client.DangerousGetHandle(), handle, null, 0); }
        finally { if (_clientRefAdded) _client.DangerousRelease(); }
        return true;
    }

    internal void CloseChecked(NativeClientCore core)
    {
        if (IsClosed || IsInvalid) return;
        IntPtr file = DangerousGetHandle();
        try
        {
            core.Invoke(client =>
            {
                Span<byte> error = stackalloc byte[NativeMethods.ErrorCapacity];
                fixed (byte* errorPtr = error)
                {
                    int rc = NativeMethods.FileClose(client.DangerousGetHandle(), file, errorPtr, (nuint)error.Length);
                    if (rc != 0) throw NativeClientCore.CreateException(rc, error);
                    return 0;
                }
            });
        }
        finally
        {
            SetHandleAsInvalid();
            if (_clientRefAdded)
            {
                _client.DangerousRelease();
                _clientRefAdded = false;
            }
            Dispose();
        }
    }
}
