using System.Runtime.InteropServices;
using StorageC;

string root = Path.Combine(Path.GetTempPath(), "storagec-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    byte[] payload = new byte[2 * 1024 * 1024 + 731];
    new Random(42).NextBytes(payload);

    await using (ISmbClient client = CreateClient(root))
    {
        SmbWriteResult written = await client.WriteAsync("nested/payload.bin", payload,
            new SmbWriteOptions { Overwrite = true, ForceSync = true });
        Equal(payload.Length, written.FileSize, "written size");

        SmbFile read = await client.ReadAsync("nested/payload.bin");
        True(payload.AsSpan().SequenceEqual(read.Content), "round-trip payload");

        using var destination = new MemoryStream();
        SmbWriteResult copied = await client.CopyToAsync("nested/payload.bin", destination);
        Equal(payload.Length, copied.FileSize, "copied size");
        True(payload.AsSpan().SequenceEqual(destination.ToArray()), "streamed payload");
    }

    ISmbClient owner = CreateClient(root);
    Stream retained = await owner.OpenReadAsync("nested/payload.bin");
    owner.Dispose();
    byte[] first = new byte[4096];
    Equal(first.Length, await retained.ReadAsync(first), "stream survives owner dispose");
    await retained.DisposeAsync();

    await using (var pool = PooledSmbClient.Create(new SmbClientOptions
    {
        Server = "fixture", ShareName = root, Username = "fixture", Password = "fixture",
        ChunkSizeBytes = 128 * 1024, MaxReadBytes = 16 * 1024 * 1024
    }, new SmbClientPoolOptions { MaxClients = 2, MaxPendingAcquires = 8, AcquireTimeout = TimeSpan.FromSeconds(2) }))
    {
        Task<SmbFile>[] reads = Enumerable.Range(0, 8)
            .Select(_ => pool.ReadAsync("nested/payload.bin"))
            .ToArray();
        await Task.WhenAll(reads);
        True(reads.All(task => payload.AsSpan().SequenceEqual(task.Result.Content)), "pooled concurrent payloads");
        Equal(2, pool.GetStatistics().ClientsCreated, "bounded pool clients");
    }

    for (int i = 0; i < 200; i++)
    {
        await using ISmbClient client = CreateClient(root);
        await using Stream stream = await client.OpenReadAsync("nested/payload.bin");
        byte[] scratch = new byte[8192];
        _ = await stream.ReadAsync(scratch);
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    Equal(0, TestNative.LiveFiles(), "native file handles");
    Equal(0, TestNative.LiveClients(), "native client handles");
    Console.WriteLine("PASS: interop, ownership, streaming and native handle stress checks");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static ISmbClient CreateClient(string root) => SmbClient.Create(new SmbClientOptions
{
    Server = "fixture",
    ShareName = root,
    Username = "fixture",
    Password = "fixture",
    ChunkSizeBytes = 256 * 1024,
    MaxReadBytes = 16 * 1024 * 1024
});

static void True(bool value, string name)
{
    if (!value) throw new InvalidOperationException($"Assertion failed: {name}");
}

static void Equal(long expected, long actual, string name)
{
    if (expected != actual) throw new InvalidOperationException($"Assertion failed: {name}; expected {expected}, actual {actual}");
}

internal static partial class TestNative
{
    [LibraryImport("storagec_native", EntryPoint = "sg_test_live_clients")]
    internal static partial int LiveClients();
    [LibraryImport("storagec_native", EntryPoint = "sg_test_live_files")]
    internal static partial int LiveFiles();
}
