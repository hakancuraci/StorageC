using System.Buffers;
using System.Text;
using System.Threading.Channels;

namespace StorageC;

internal sealed class NativeClientCore : IDisposable
{
    private readonly Channel<IWorkItem> _queue;
    private readonly Thread _thread;
    private SafeClientHandle? _handle;
    private int _references = 1;
    private int _accepting = 1;

    private NativeClientCore(SafeClientHandle handle)
    {
        _handle = handle;
        _queue = Channel.CreateBounded<IWorkItem>(new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _thread = new Thread(Run) { IsBackground = true, Name = "StorageC.libsmb2" };
        _thread.Start();
    }

    internal SafeClientHandle Handle => _handle ?? throw new ObjectDisposedException(nameof(NativeClientCore));

    internal static unsafe NativeClientCore Create(SmbClientOptions options)
    {
        Span<byte> error = stackalloc byte[NativeMethods.ErrorCapacity];
        fixed (byte* errorPtr = error)
        {
            int rc = NativeMethods.ClientCreate(BuildServer(options), options.ShareName, options.Username,
                options.Password, options.Domain, options.Port,
                checked((int)Math.Ceiling(options.DialTimeout.TotalSeconds)),
                options.RequireSigning ? 1 : 0, options.RequireEncryption ? 1 : 0,
                errorPtr, (nuint)error.Length, out IntPtr value);
            if (rc != 0 || value == IntPtr.Zero) throw CreateException(rc, error);
            return new NativeClientCore(new SafeClientHandle(value));
        }
    }

    internal NativeClientCore AddReference()
    {
        while (true)
        {
            int current = Volatile.Read(ref _references);
            ObjectDisposedException.ThrowIf(current == 0, this);
            if (Interlocked.CompareExchange(ref _references, current + 1, current) == current) return this;
        }
    }

    internal void Release()
    {
        int remaining = Interlocked.Decrement(ref _references);
        if (remaining < 0) throw new InvalidOperationException("Native client reference count underflow.");
        if (remaining != 0) return;
        Interlocked.Exchange(ref _accepting, 0);
        _queue.Writer.TryComplete();
        if (Thread.CurrentThread != _thread) _thread.Join();
    }

    public void Dispose() => Release();

    internal T Invoke<T>(Func<SafeClientHandle, T> callback) =>
        InvokeAsync(callback, CancellationToken.None).GetAwaiter().GetResult();

    internal ValueTask<T> InvokeAsync<T>(Func<SafeClientHandle, T> callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _accepting) == 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var item = new WorkItem<T>(callback, cancellationToken);
        if (_queue.Writer.TryWrite(item)) return new ValueTask<T>(item.Task);
        return EnqueueSlowAsync(item, cancellationToken);
    }

    private async ValueTask<T> EnqueueSlowAsync<T>(WorkItem<T> item, CancellationToken cancellationToken)
    {
        await _queue.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        return await item.Task.ConfigureAwait(false);
    }

    private void Run()
    {
        try
        {
            while (_queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                while (_queue.Reader.TryRead(out IWorkItem? item)) item.Execute(Handle);
        }
        finally
        {
            while (_queue.Reader.TryRead(out IWorkItem? item)) item.Fail(new ObjectDisposedException(nameof(NativeClientCore)));
            Interlocked.Exchange(ref _handle, null)?.Dispose();
        }
    }

    internal static SmbException CreateException(int rc, ReadOnlySpan<byte> error)
    {
        int terminator = error.IndexOf((byte)0);
        if (terminator < 0) terminator = error.Length;
        string message = terminator == 0 ? $"libsmb2 operation failed ({rc})." : Encoding.UTF8.GetString(error[..terminator]);
        return new SmbException(message, rc) { ConnectionReusable = IsReusableFileError(rc) };
    }

    private static bool IsReusableFileError(int rc) => rc is -2 or -13 or -17 or -20 or -21 or -22 or -28 or -39;

    private static string BuildServer(SmbClientOptions options)
    {
        if (options.Port == 445) return options.Server;
        return options.Server.Contains(':') && !options.Server.StartsWith('[')
            ? $"[{options.Server}]:{options.Port}" : $"{options.Server}:{options.Port}";
    }

    private interface IWorkItem
    {
        void Execute(SafeClientHandle handle);
        void Fail(Exception exception);
    }

    private sealed class WorkItem<T>(Func<SafeClientHandle, T> callback, CancellationToken cancellationToken) : IWorkItem
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<T> Task => _completion.Task;

        public void Execute(SafeClientHandle handle)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(cancellationToken);
                return;
            }
            try { _completion.TrySetResult(callback(handle)); }
            catch (Exception exception) { _completion.TrySetException(exception); }
        }

        public void Fail(Exception exception) => _completion.TrySetException(exception);
    }
}

internal sealed class PooledBuffer : IDisposable
{
    private byte[]? _array;
    private readonly bool _clear;
    internal PooledBuffer(int length, bool clear) { _array = ArrayPool<byte>.Shared.Rent(length); _clear = clear; }
    internal byte[] Array => _array ?? throw new ObjectDisposedException(nameof(PooledBuffer));
    internal Memory<byte> Memory => Array;
    public void Dispose()
    {
        byte[]? value = Interlocked.Exchange(ref _array, null);
        if (value is not null) ArrayPool<byte>.Shared.Return(value, _clear);
    }
}
