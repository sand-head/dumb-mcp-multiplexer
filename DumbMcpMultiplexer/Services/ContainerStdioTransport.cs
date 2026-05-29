using Docker.DotNet;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DumbMcpMultiplexer.Services;

public sealed class ContainerStdioTransport : IClientTransport, IAsyncDisposable
{
    private readonly MultiplexedStream _stream;
    private readonly StreamClientTransport _streamClientTransport;

    public ContainerStdioTransport(MultiplexedStream stream, string name, ILoggerFactory? loggerFactory = null)
    {
        _stream = stream;
        _streamClientTransport = new StreamClientTransport(
            new ContainerStdinStream(stream),
            new ContainerStdoutStream(stream),
            loggerFactory);
        Name = name;
    }

    public string Name { get; }

    public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
        _streamClientTransport.ConnectAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        _stream.CloseWrite();
        _stream.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class ContainerStdinStream(MultiplexedStream stream) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Synchronous writes are not supported.");

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            stream.WriteAsync(buffer, offset, count, cancellationToken);
    }

    private sealed class ContainerStdoutStream(MultiplexedStream stream) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Synchronous reads are not supported.");

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            while (true)
            {
                var readResult = await stream.ReadOutputAsync(buffer, offset, count, cancellationToken);
                if (readResult.EOF)
                {
                    return 0;
                }

                if (readResult.Count == 0)
                {
                    continue;
                }

                if (readResult.Target == MultiplexedStream.TargetStream.StandardOut)
                {
                    return readResult.Count;
                }
            }
        }
    }
}
