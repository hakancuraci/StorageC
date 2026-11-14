using System.Buffers;

namespace StorageC;

public sealed class SmbClient : ISmbClient
{
    private NativeClientCore? _core;
    private readonly int _chunkSize;
    private readonly long _maxReadBytes;
    private readonly bool _clearBuffers;

    private SmbClient(NativeClientCore core, SmbClientOptions options)
    {
        _core = core;
        _chunkSize = options.ChunkSizeBytes;
        _maxReadBytes = Math.Min(options.MaxReadBytes, Array.MaxLength);
        _clearBuffers = options.ClearBuffersOnReturn;
    }

    public static ISmbClient Create(SmbClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        if (NativeMethods.AbiVersion() != 1) throw new NotSupportedException("storagec_native ABI 1 is required.");
        return new SmbClient(NativeClientCore.Create(options), options);
    }

    public Stream OpenRead(string remotePath) => OpenReadAsync(remotePath).GetAwaiter().GetResult();
    public Stream OpenWrite(string remotePath, SmbWriteOptions? options = null) =>
        OpenWriteAsync(remotePath, options).GetAwaiter().GetResult();

    public async Task<Stream> OpenReadAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        ValidatePath(remotePath);
        NativeClientCore core = GetCore();
        (SafeFileHandle File, ulong Size) result = await core.InvokeAsync(handle => OpenReadNative(handle, remotePath), cancellationToken).ConfigureAwait(false);
        core.AddReference();
        try
        {
            return new NativeSmbStream(core, result.File, true, false, GetFileName(remotePath),
                checked((long)result.Size), GetMimeType(remotePath, null));
        }
        catch { result.File.CloseChecked(core); core.Release(); throw; }
    }

    public async Task<Stream> OpenWriteAsync(string remotePath, SmbWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(remotePath);
        options ??= new SmbWriteOptions();
        NativeClientCore core = GetCore();
        SafeFileHandle file = await core.InvokeAsync(handle => OpenWriteNative(handle, remotePath, options.Overwrite), cancellationToken).ConfigureAwait(false);
        core.AddReference();
        try
        {
            return new NativeSmbStream(core, file, false, options.ForceSync, GetFileName(remotePath), 0,
                GetMimeType(remotePath, options.MimeType));
        }
        catch { file.CloseChecked(core); core.Release(); throw; }
    }

    public SmbFile Read(string remotePath) => ReadAsync(remotePath).GetAwaiter().GetResult();

    public async Task<SmbFile> ReadAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        await using var source = (NativeSmbStream)await OpenReadAsync(remotePath, cancellationToken).ConfigureAwait(false);
        if (source.FileSize > _maxReadBytes) throw ReadLimitExceeded();
        using var destination = new MemoryStream(checked((int)source.FileSize));
        using var rented = new PooledBuffer(_chunkSize, _clearBuffers);
        while (true)
        {
            int read = await source.ReadAsync(rented.Memory[.._chunkSize], cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (destination.Length > _maxReadBytes - read) throw ReadLimitExceeded();
            await destination.WriteAsync(rented.Memory[..read], cancellationToken).ConfigureAwait(false);
        }
        return new SmbFile(source.FileName, destination.Length, source.MimeType, destination.ToArray());
    }

    public SmbWriteResult CopyTo(string remotePath, Stream destination, CancellationToken cancellationToken = default) =>
        CopyToAsync(remotePath, destination, cancellationToken).GetAwaiter().GetResult();

    public async Task<SmbWriteResult> CopyToAsync(string remotePath, Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("Destination must be writable.", nameof(destination));
        await using var source = (NativeSmbStream)await OpenReadAsync(remotePath, cancellationToken).ConfigureAwait(false);
        using var rented = new PooledBuffer(_chunkSize, _clearBuffers);
        long transferred = 0;
        while (true)
        {
            int read = await source.ReadAsync(rented.Memory[.._chunkSize], cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            await destination.WriteAsync(rented.Memory[..read], cancellationToken).ConfigureAwait(false);
            transferred = checked(transferred + read);
        }
        return new SmbWriteResult(source.FileName, transferred, source.MimeType);
    }

    public SmbWriteResult Write(string remotePath, byte[] source, SmbWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Write(remotePath, source.AsMemory(), options);
    }

    public SmbWriteResult Write(string remotePath, ReadOnlyMemory<byte> content, SmbWriteOptions? options = null) =>
        WriteAsync(remotePath, content, options).GetAwaiter().GetResult();

    public SmbWriteResult Write(string remotePath, Stream source, SmbWriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        WriteAsync(remotePath, source, options, cancellationToken).GetAwaiter().GetResult();

    public Task<SmbWriteResult> WriteAsync(string remotePath, byte[] source, SmbWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return WriteAsync(remotePath, source.AsMemory(), options, cancellationToken);
    }

    public async Task<SmbWriteResult> WriteAsync(string remotePath, ReadOnlyMemory<byte> content,
        SmbWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        await using var destination = (NativeSmbStream)await OpenWriteAsync(remotePath, options, cancellationToken).ConfigureAwait(false);
        int offset = 0;
        while (offset < content.Length)
        {
            int count = Math.Min(_chunkSize, content.Length - offset);
            await destination.WriteAsync(content.Slice(offset, count), cancellationToken).ConfigureAwait(false);
            offset += count;
        }
        return new SmbWriteResult(destination.FileName, destination.BytesTransferred, destination.MimeType);
    }

    public async Task<SmbWriteResult> WriteAsync(string remotePath, Stream source, SmbWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("Source must be readable.", nameof(source));
        await using var destination = (NativeSmbStream)await OpenWriteAsync(remotePath, options, cancellationToken).ConfigureAwait(false);
        using var rented = new PooledBuffer(_chunkSize, _clearBuffers);
        while (true)
        {
            int read = await source.ReadAsync(rented.Memory[.._chunkSize], cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            await destination.WriteAsync(rented.Memory[..read], cancellationToken).ConfigureAwait(false);
        }
        return new SmbWriteResult(destination.FileName, destination.BytesTransferred, destination.MimeType);
    }

    public void Dispose() => Interlocked.Exchange(ref _core, null)?.Release();
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

    private NativeClientCore GetCore() => _core ?? throw new ObjectDisposedException(nameof(SmbClient));

    private static unsafe (SafeFileHandle File, ulong Size) OpenReadNative(SafeClientHandle handle, string path)
    {
        Span<byte> error = stackalloc byte[NativeMethods.ErrorCapacity];
        fixed (byte* errorPtr = error)
        {
            int rc = NativeMethods.OpenRead(handle, NormalizePath(path), errorPtr, (nuint)error.Length, out IntPtr file, out ulong size);
            if (rc != 0 || file == IntPtr.Zero) throw NativeClientCore.CreateException(rc, error);
            return (new SafeFileHandle(file, handle), size);
        }
    }

    private static unsafe SafeFileHandle OpenWriteNative(SafeClientHandle handle, string path, bool overwrite)
    {
        Span<byte> error = stackalloc byte[NativeMethods.ErrorCapacity];
        fixed (byte* errorPtr = error)
        {
            int rc = NativeMethods.OpenWrite(handle, NormalizePath(path), overwrite ? 1 : 0,
                errorPtr, (nuint)error.Length, out IntPtr file);
            if (rc != 0 || file == IntPtr.Zero) throw NativeClientCore.CreateException(rc, error);
            return new SafeFileHandle(file, handle);
        }
    }

    private SmbException ReadLimitExceeded() => new($"File exceeds MaxReadBytes ({_maxReadBytes}). Use streaming APIs.");
    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');
    private static string GetFileName(string path) => NormalizePath(path).Split('/')[^1];
    private static string GetMimeType(string path, string? explicitType) => !string.IsNullOrWhiteSpace(explicitType)
        ? explicitType : Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" => "application/json", ".pdf" => "application/pdf", ".txt" => "text/plain",
            ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".zip" => "application/zip",
            _ => "application/octet-stream"
        };

    private static void ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('\0')) throw new ArgumentException("Remote path cannot contain NUL.", nameof(path));
    }

    private static void Validate(SmbClientOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Server);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ShareName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Username);
        ArgumentNullException.ThrowIfNull(options.Password);
        if (options.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(options.Port));
        if (options.DialTimeout <= TimeSpan.Zero || options.DialTimeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(options.DialTimeout));
        if (options.MaxReadBytes <= 0 || options.MaxReadBytes > Array.MaxLength) throw new ArgumentOutOfRangeException(nameof(options.MaxReadBytes));
        if (options.ChunkSizeBytes is < 4096 or > 4 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(options.ChunkSizeBytes));
    }
}
