namespace StorageC;

public interface ISmbClient : IDisposable, IAsyncDisposable
{
    Stream OpenRead(string remotePath);
    Stream OpenWrite(string remotePath, SmbWriteOptions? options = null);
    SmbFile Read(string remotePath);
    SmbWriteResult CopyTo(string remotePath, Stream destination, CancellationToken cancellationToken = default);
    SmbWriteResult Write(string remotePath, byte[] source, SmbWriteOptions? options = null);
    SmbWriteResult Write(string remotePath, ReadOnlyMemory<byte> content, SmbWriteOptions? options = null);
    SmbWriteResult Write(string remotePath, Stream source, SmbWriteOptions? options = null, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(string remotePath, CancellationToken cancellationToken = default);
    Task<Stream> OpenWriteAsync(string remotePath, SmbWriteOptions? options = null, CancellationToken cancellationToken = default);
    Task<SmbFile> ReadAsync(string remotePath, CancellationToken cancellationToken = default);
    Task<SmbWriteResult> CopyToAsync(string remotePath, Stream destination, CancellationToken cancellationToken = default);
    Task<SmbWriteResult> WriteAsync(string remotePath, byte[] source, SmbWriteOptions? options = null, CancellationToken cancellationToken = default);
    Task<SmbWriteResult> WriteAsync(string remotePath, ReadOnlyMemory<byte> content, SmbWriteOptions? options = null, CancellationToken cancellationToken = default);
    Task<SmbWriteResult> WriteAsync(string remotePath, Stream source, SmbWriteOptions? options = null, CancellationToken cancellationToken = default);
}

public sealed class SmbClientOptions
{
    public required string Server { get; init; }
    public required string ShareName { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }
    public string? Domain { get; init; }
    public int Port { get; init; } = 445;
    public TimeSpan DialTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public long MaxReadBytes { get; init; } = 256L * 1024 * 1024;
    public int ChunkSizeBytes { get; init; } = 1024 * 1024;
    public bool ClearBuffersOnReturn { get; init; } = true;
    public bool RequireSigning { get; init; }
    public bool RequireEncryption { get; init; }
}

public sealed class SmbWriteOptions
{
    public bool Overwrite { get; init; }
    public bool ForceSync { get; init; }
    public string? MimeType { get; init; }
}

public sealed record SmbFile(string FileName, long FileSize, string MimeType, byte[] Content);
public sealed record SmbWriteResult(string FileName, long FileSize, string MimeType);

public sealed class SmbException : IOException
{
    public SmbException(string message, int nativeErrorCode = 0) : base(message) => NativeErrorCode = nativeErrorCode;
    public int NativeErrorCode { get; }
    public bool ConnectionReusable { get; init; }
}
