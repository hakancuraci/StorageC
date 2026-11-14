using System.Collections.Concurrent;
using System.Diagnostics;

namespace StorageC;

public sealed class SmbClientPoolOptions
{
    public int MaxClients { get; init; } = 8;
    public int MaxPendingAcquires { get; init; } = 32;
    public TimeSpan AcquireTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);
}

public sealed class SmbClientPoolFullException() : InvalidOperationException("The SMB client pool acquisition queue is full.");

public sealed record SmbClientPoolStatistics(int ActiveLeases, int PendingAcquires, int IdleClients,
    long ClientsCreated, long ClientsDiscarded, long AcquireTimeouts, long RejectedAcquires, long CleanupFailures);

public sealed class PooledSmbClient : ISmbClient
{
    private readonly SmbClientOptions _clientOptions;
    private readonly SmbClientPoolOptions _poolOptions;
    private readonly SemaphoreSlim _capacity;
    private readonly ConcurrentQueue<IdleEntry> _idle = new();
    private int _active;
    private int _pending;
    private long _created;
    private long _discarded;
    private long _timeouts;
    private long _rejected;
    private long _cleanupFailures;
    private int _disposed;

    private PooledSmbClient(SmbClientOptions clientOptions, SmbClientPoolOptions poolOptions)
    {
        _clientOptions = clientOptions;
        _poolOptions = poolOptions;
        _capacity = new SemaphoreSlim(poolOptions.MaxClients, poolOptions.MaxClients);
    }

    public static PooledSmbClient Create(SmbClientOptions clientOptions, SmbClientPoolOptions? poolOptions = null)
    {
        ArgumentNullException.ThrowIfNull(clientOptions);
        poolOptions ??= new SmbClientPoolOptions();
        if (poolOptions.MaxClients <= 0) throw new ArgumentOutOfRangeException(nameof(poolOptions.MaxClients));
        if (poolOptions.MaxPendingAcquires < 0) throw new ArgumentOutOfRangeException(nameof(poolOptions.MaxPendingAcquires));
        if (poolOptions.AcquireTimeout <= TimeSpan.Zero || poolOptions.AcquireTimeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(poolOptions.AcquireTimeout));
        if (poolOptions.IdleTimeout <= TimeSpan.Zero || poolOptions.IdleTimeout.TotalMilliseconds > uint.MaxValue - 1) throw new ArgumentOutOfRangeException(nameof(poolOptions.IdleTimeout));
        return new PooledSmbClient(clientOptions, poolOptions);
    }

    public SmbClientPoolStatistics GetStatistics() => new(Volatile.Read(ref _active), Volatile.Read(ref _pending),
        _idle.Count, Volatile.Read(ref _created), Volatile.Read(ref _discarded), Volatile.Read(ref _timeouts),
        Volatile.Read(ref _rejected), Volatile.Read(ref _cleanupFailures));

    public Stream OpenRead(string remotePath) => OpenReadAsync(remotePath).GetAwaiter().GetResult();
    public Stream OpenWrite(string remotePath, SmbWriteOptions? options = null) => OpenWriteAsync(remotePath, options).GetAwaiter().GetResult();

    public async Task<Stream> OpenReadAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        Lease lease = await RentAsync(cancellationToken).ConfigureAwait(false);
        try { return new PooledStream(await lease.Client.OpenReadAsync(remotePath, cancellationToken).ConfigureAwait(false), lease); }
        catch (Exception exception) { lease.Return(IsBroken(exception)); throw; }
    }

    public async Task<Stream> OpenWriteAsync(string remotePath, SmbWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Lease lease = await RentAsync(cancellationToken).ConfigureAwait(false);
        try { return new PooledStream(await lease.Client.OpenWriteAsync(remotePath, options, cancellationToken).ConfigureAwait(false), lease); }
        catch (Exception exception) { lease.Return(IsBroken(exception)); throw; }
    }

    public SmbFile Read(string remotePath) => Use(client => client.Read(remotePath));
    public SmbWriteResult CopyTo(string remotePath, Stream destination, CancellationToken cancellationToken = default) =>
        Use(client => client.CopyTo(remotePath, destination, cancellationToken));
    public SmbWriteResult Write(string remotePath, byte[] source, SmbWriteOptions? options = null) =>
        Use(client => client.Write(remotePath, source, options));
    public SmbWriteResult Write(string remotePath, ReadOnlyMemory<byte> content, SmbWriteOptions? options = null) =>
        Use(client => client.Write(remotePath, content, options));
    public SmbWriteResult Write(string remotePath, Stream source, SmbWriteOptions? options = null,
        CancellationToken cancellationToken = default) => Use(client => client.Write(remotePath, source, options, cancellationToken));

    public Task<SmbFile> ReadAsync(string remotePath, CancellationToken cancellationToken = default) =>
        UseAsync(client => client.ReadAsync(remotePath, cancellationToken), cancellationToken);
    public Task<SmbWriteResult> CopyToAsync(string remotePath, Stream destination, CancellationToken cancellationToken = default) =>
        UseAsync(client => client.CopyToAsync(remotePath, destination, cancellationToken), cancellationToken);
    public Task<SmbWriteResult> WriteAsync(string remotePath, byte[] source, SmbWriteOptions? options = null,
        CancellationToken cancellationToken = default) => UseAsync(client => client.WriteAsync(remotePath, source, options, cancellationToken), cancellationToken);
    public Task<SmbWriteResult> WriteAsync(string remotePath, ReadOnlyMemory<byte> content, SmbWriteOptions? options = null,
        CancellationToken cancellationToken = default) => UseAsync(client => client.WriteAsync(remotePath, content, options, cancellationToken), cancellationToken);
    public Task<SmbWriteResult> WriteAsync(string remotePath, Stream source, SmbWriteOptions? options = null,
        CancellationToken cancellationToken = default) => UseAsync(client => client.WriteAsync(remotePath, source, options, cancellationToken), cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        while (_idle.TryDequeue(out IdleEntry entry)) entry.Client.Dispose();
    }

    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

    private T Use<T>(Func<SmbClient, T> operation)
    {
        Lease lease = RentAsync(CancellationToken.None).GetAwaiter().GetResult();
        bool broken = false;
        try { return operation(lease.Client); }
        catch (Exception exception) { broken = IsBroken(exception); throw; }
        finally { lease.Return(broken); }
    }

    private async Task<T> UseAsync<T>(Func<SmbClient, Task<T>> operation, CancellationToken cancellationToken)
    {
        Lease lease = await RentAsync(cancellationToken).ConfigureAwait(false);
        bool broken = false;
        try { return await operation(lease.Client).ConfigureAwait(false); }
        catch (Exception exception) { broken = IsBroken(exception); throw; }
        finally { lease.Return(broken); }
    }

    private async ValueTask<Lease> RentAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_capacity.Wait(0))
        {
            long pending = Interlocked.Increment(ref _pending);
            if (pending > _poolOptions.MaxPendingAcquires)
            {
                Interlocked.Decrement(ref _pending);
                Interlocked.Increment(ref _rejected);
                throw new SmbClientPoolFullException();
            }
            try
            {
                if (!await _capacity.WaitAsync(_poolOptions.AcquireTimeout, cancellationToken).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref _timeouts);
                    throw new TimeoutException("Timed out waiting for an SMB client.");
                }
            }
            finally { Interlocked.Decrement(ref _pending); }
        }

        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            SmbClient? client = null;
            while (_idle.TryDequeue(out IdleEntry entry))
            {
                if (Stopwatch.GetElapsedTime(entry.ReturnedTimestamp) <= _poolOptions.IdleTimeout)
                {
                    client = entry.Client;
                    break;
                }
                try { entry.Client.Dispose(); }
                catch { Interlocked.Increment(ref _cleanupFailures); }
            }
            if (client is null)
            {
                client = (SmbClient)SmbClient.Create(_clientOptions);
                Interlocked.Increment(ref _created);
            }
            Interlocked.Increment(ref _active);
            return new Lease(this, client);
        }
        catch { _capacity.Release(); throw; }
    }

    private void Return(SmbClient client, bool broken)
    {
        if (broken || Volatile.Read(ref _disposed) != 0)
        {
            client.Dispose();
            if (broken) Interlocked.Increment(ref _discarded);
        }
        else _idle.Enqueue(new IdleEntry(client, Stopwatch.GetTimestamp()));
        Interlocked.Decrement(ref _active);
        _capacity.Release();
    }

    private static bool IsBroken(Exception exception) => exception is SmbException smb && !smb.ConnectionReusable;

    private sealed class Lease(PooledSmbClient owner, SmbClient client)
    {
        private PooledSmbClient? _owner = owner;
        internal SmbClient Client { get; } = client;
        internal void Return(bool broken) => Interlocked.Exchange(ref _owner, null)?.Return(Client, broken);
    }

    private readonly record struct IdleEntry(SmbClient Client, long ReturnedTimestamp);

    private sealed class PooledStream(Stream inner, Lease lease) : Stream
    {
        private Stream? _inner = inner;
        private Lease? _lease = lease;
        private bool _broken;
        public override bool CanRead => Get().CanRead;
        public override bool CanSeek => Get().CanSeek;
        public override bool CanWrite => Get().CanWrite;
        public override long Length => Get().Length;
        public override long Position { get => Get().Position; set => Get().Position = value; }
        public override void Flush() => Guard(Get().Flush);
        public override Task FlushAsync(CancellationToken cancellationToken) => GuardAsync(() => Get().FlushAsync(cancellationToken));
        public override int Read(byte[] buffer, int offset, int count) => Guard(() => Get().Read(buffer, offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => GuardValueAsync(() => Get().ReadAsync(buffer, cancellationToken));
        public override void Write(byte[] buffer, int offset, int count) => Guard(() => Get().Write(buffer, offset, count));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => GuardValueAsync(() => Get().WriteAsync(buffer, cancellationToken));
        public override long Seek(long offset, SeekOrigin origin) => Get().Seek(offset, origin);
        public override void SetLength(long value) => Get().SetLength(value);
        protected override void Dispose(bool disposing)
        {
            Stream? value = Interlocked.Exchange(ref _inner, null);
            if (value is null) return;
            try { if (disposing) value.Dispose(); }
            catch (Exception exception) { _broken |= IsBroken(exception); throw; }
            finally { Interlocked.Exchange(ref _lease, null)?.Return(_broken); }
        }
        public override async ValueTask DisposeAsync()
        {
            Stream? value = Interlocked.Exchange(ref _inner, null);
            if (value is null) return;
            try { await value.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { _broken |= IsBroken(exception); throw; }
            finally { Interlocked.Exchange(ref _lease, null)?.Return(_broken); }
            GC.SuppressFinalize(this);
        }
        private Stream Get() => _inner ?? throw new ObjectDisposedException(nameof(PooledStream));
        private void Guard(Action action) { try { action(); } catch (Exception exception) { _broken |= IsBroken(exception); throw; } }
        private T Guard<T>(Func<T> action) { try { return action(); } catch (Exception exception) { _broken |= IsBroken(exception); throw; } }
        private async Task GuardAsync(Func<Task> action) { try { await action().ConfigureAwait(false); } catch (Exception exception) { _broken |= IsBroken(exception); throw; } }
        private async ValueTask GuardValueAsync(Func<ValueTask> action) { try { await action().ConfigureAwait(false); } catch (Exception exception) { _broken |= IsBroken(exception); throw; } }
        private async ValueTask<T> GuardValueAsync<T>(Func<ValueTask<T>> action) { try { return await action().ConfigureAwait(false); } catch (Exception exception) { _broken |= IsBroken(exception); throw; } }
    }
}
