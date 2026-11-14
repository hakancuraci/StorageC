using System.Buffers;

namespace StorageC;

internal sealed class NativeSmbStream : Stream
{
    private NativeClientCore? _core;
    private SafeFileHandle? _file;
    private readonly bool _readable;
    private readonly bool _forceSync;
    private long _transferred;
    private int _disposed;

    internal NativeSmbStream(NativeClientCore core, SafeFileHandle file, bool readable, bool forceSync,
        string fileName, long fileSize, string mimeType)
    {
        _core = core;
        _file = file;
        _readable = readable;
        _forceSync = forceSync;
        FileName = fileName;
        FileSize = fileSize;
        MimeType = mimeType;
    }

    ~NativeSmbStream() => Dispose(false);

    internal string FileName { get; }
    internal long FileSize { get; }
    internal string MimeType { get; }
    internal long BytesTransferred => Interlocked.Read(ref _transferred);
    public override bool CanRead => _readable && Volatile.Read(ref _disposed) == 0;
    public override bool CanSeek => false;
    public override bool CanWrite => !_readable && Volatile.Read(ref _disposed) == 0;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        return Read(buffer.AsMemory(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        byte[] temporary = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            int count = Read(temporary.AsMemory(0, buffer.Length));
            temporary.AsSpan(0, count).CopyTo(buffer);
            return count;
        }
        finally { ArrayPool<byte>.Shared.Return(temporary, clearArray: true); }
    }

    private int Read(Memory<byte> memory) => ReadAsyncCore(memory, CancellationToken.None).GetAwaiter().GetResult();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureReadable();
        if (buffer.Length == 0) return ValueTask.FromResult(0);
        return ReadAsyncCore(buffer, cancellationToken);
    }

    private ValueTask<int> ReadAsyncCore(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        EnsureReadable();
        NativeClientCore core = GetCore();
        SafeFileHandle file = GetFile();
        return core.InvokeAsync(handle =>
        {
            int read = ReadNative(handle, file, buffer);
            Interlocked.Add(ref _transferred, read);
            return read;
        }, cancellationToken);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        byte[] temporary = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            buffer.CopyTo(temporary);
            WriteAsyncCore(temporary.AsMemory(0, buffer.Length), CancellationToken.None).GetAwaiter().GetResult();
        }
        finally { ArrayPool<byte>.Shared.Return(temporary, clearArray: true); }
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        if (buffer.Length == 0) return ValueTask.CompletedTask;
        return WriteAsyncCore(buffer, cancellationToken);
    }

    private async ValueTask WriteAsyncCore(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        EnsureWritable();
        NativeClientCore core = GetCore();
        SafeFileHandle file = GetFile();
        int offset = 0;
        while (offset < buffer.Length)
        {
            ReadOnlyMemory<byte> slice = buffer[offset..];
            int written = await core.InvokeAsync(handle => WriteNative(handle, file, slice), cancellationToken)
                .ConfigureAwait(false);
            offset += written;
            Interlocked.Add(ref _transferred, written);
        }
    }

    public override void Flush() => FlushAsyncCore(CancellationToken.None).GetAwaiter().GetResult();
    public override Task FlushAsync(CancellationToken cancellationToken) => FlushAsyncCore(cancellationToken).AsTask();

    private ValueTask FlushAsyncCore(CancellationToken cancellationToken)
    {
        EnsureNotDisposed();
        NativeClientCore core = GetCore();
        SafeFileHandle file = GetFile();
        return core.InvokeAsync(handle => FlushNative(handle, file), cancellationToken).AsVoid();
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Exception? failure = null;
        NativeClientCore? core = _core;
        SafeFileHandle? file = _file;
        if (disposing && _forceSync && core is not null && file is not null)
        {
            try
            {
                core.Invoke(handle => FlushNative(handle, file));
            }
            catch (Exception exception) { failure = exception; }
        }
        try
        {
            if (core is not null && file is not null) file.CloseChecked(core);
            else file?.Dispose();
        }
        catch (Exception exception) { failure ??= exception; }
        Interlocked.Exchange(ref _file, null);
        Interlocked.Exchange(ref _core, null)?.Release();
        if (disposing) GC.SuppressFinalize(this);
        base.Dispose(disposing);
        if (disposing && failure is not null) throw failure;
    }

    public override ValueTask DisposeAsync() { Dispose(true); return ValueTask.CompletedTask; }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private NativeClientCore GetCore() => _core ?? throw new ObjectDisposedException(nameof(NativeSmbStream));
    private SafeFileHandle GetFile() => _file ?? throw new ObjectDisposedException(nameof(NativeSmbStream));
    private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    private void EnsureReadable() { EnsureNotDisposed(); if (!_readable) throw new NotSupportedException("Stream is not readable."); }
    private void EnsureWritable() { EnsureNotDisposed(); if (_readable) throw new NotSupportedException("Stream is not writable."); }
    private static void ValidateBuffer(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count) throw new ArgumentException("Offset and count exceed the buffer.");
    }

    private static unsafe int ReadNative(SafeClientHandle handle, SafeFileHandle file, Memory<byte> buffer)
    {
        using MemoryHandle pin = buffer.Pin();
        Span<byte> error = stackalloc byte[NativeMethods.ErrorCapacity];
        fixed (byte* errorPtr = error)
        {
            int rc = NativeMethods.FileRead(handle, file, (byte*)pin.Pointer, checked((uint)buffer.Length),
                errorPtr, (nuint)error.Length, out uint read);
            if (rc != 0) throw NativeClientCore.CreateException(rc, error);
            return checked((int)read);
        }
    }

    private static unsafe int WriteNative(SafeClientHandle handle, SafeFileHandle file, ReadOnlyMemory<byte> buffer)
    {
        using MemoryHandle pin = buffer.Pin();
        Span<byte> error = stackalloc byte[NativeMethods.ErrorCapacity];
        fixed (byte* errorPtr = error)
        {
            int rc = NativeMethods.FileWrite(handle, file, (byte*)pin.Pointer, checked((uint)buffer.Length),
                errorPtr, (nuint)error.Length, out uint written);
            if (rc != 0) throw NativeClientCore.CreateException(rc, error);
            if (written == 0) throw new IOException("libsmb2 returned a zero-byte write.");
            return checked((int)written);
        }
    }

    private static unsafe int FlushNative(SafeClientHandle handle, SafeFileHandle file)
    {
        Span<byte> error = stackalloc byte[NativeMethods.ErrorCapacity];
        fixed (byte* errorPtr = error)
        {
            int rc = NativeMethods.FileFlush(handle, file, errorPtr, (nuint)error.Length);
            if (rc != 0) throw NativeClientCore.CreateException(rc, error);
            return 0;
        }
    }
}

internal static class ValueTaskExtensions
{
    internal static async ValueTask AsVoid<T>(this ValueTask<T> task) => _ = await task.ConfigureAwait(false);
}
