using System.Runtime.InteropServices;

int abiVersion = NativeMethods.AbiVersion();
if (abiVersion != 1)
    throw new InvalidOperationException($"Expected native ABI 1, received {abiVersion}.");

Console.WriteLine($"PASS: loaded StorageC native ABI {abiVersion}");

internal static partial class NativeMethods
{
    [LibraryImport("storagec_native", EntryPoint = "sg_abi_version")]
    internal static partial int AbiVersion();
}
