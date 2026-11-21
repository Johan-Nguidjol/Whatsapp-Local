using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ChatLocal
{
    /// <summary>Relays frames from the process that owns the physical webcam to other ChatLocal profiles on this PC.</summary>
    internal sealed class SharedCameraRelay : IDisposable
    {
        private const string PipeName = "ChatLocal.CameraFrames.v1";
        private const int MaxFrameBytes = 32 * 1024 * 1024;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ConcurrentDictionary<int, Channel<CameraFrame>> _clients = new ConcurrentDictionary<int, Channel<CameraFrame>>();
        private readonly NamedPipeClientStream _client;
        private int _nextClientId;
        private bool _disposed;

        public event Action<byte[], int, int> FrameReceived;
        public event Action ConnectionEnded;

        private sealed class CameraFrame
        {
            public byte[] Jpeg { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
        }

        private SharedCameraRelay() { }

        private SharedCameraRelay(NamedPipeClientStream client)
        {
            _client = client;
            _ = Task.Run(ReadFramesAsync);
        }

        public static SharedCameraRelay StartHost()
        {
            var relay = new SharedCameraRelay();
            _ = Task.Run(relay.AcceptClientsAsync);
            return relay;
        }

        public static async Task<SharedCameraRelay> ConnectToHostAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                var client = new NamedPipeClientStream(".", PipeName, PipeDirection.In, PipeOptions.Asynchronous);
                try
                {
                    int remaining = Math.Max(100, (int)Math.Min(500, (deadline - DateTime.UtcNow).TotalMilliseconds));
                    await client.ConnectAsync(remaining, cancellationToken).ConfigureAwait(false);
                    return new SharedCameraRelay(client);
                }
                catch (OperationCanceledException)
                {
                    client.Dispose();
                    throw;
                }
                catch
                {
                    client.Dispose();
                    await Task.Delay(180, cancellationToken).ConfigureAwait(false);
                }
            }
            return null;
        }

        public void Publish(byte[] jpeg, int width, int height)
        {
            if (_disposed || jpeg == null || jpeg.Length == 0 || jpeg.Length > MaxFrameBytes || width <= 0 || height <= 0) return;
            var frame = new CameraFrame { Jpeg = jpeg, Width = width, Height = height };
            foreach (var channel in _clients.Values)
                channel.Writer.TryWrite(frame);
        }

        private async Task AcceptClientsAsync()
        {
            while (!_disposed && !_cts.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(PipeName, PipeDirection.Out,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 64 * 1024, 64 * 1024);
                    await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                    int id = Interlocked.Increment(ref _nextClientId);
                    var channel = Channel.CreateBounded<CameraFrame>(new BoundedChannelOptions(1)
                    {
                        FullMode = BoundedChannelFullMode.DropOldest,
                        SingleReader = true,
                        SingleWriter = false
                    });
                    _clients[id] = channel;
                    var connectedServer = server;
                    server = null;
                    _ = Task.Run(() => WriteFramesAsync(id, connectedServer, channel));
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { await Task.Delay(150).ConfigureAwait(false); }
                finally { server?.Dispose(); }
            }
        }

        private async Task WriteFramesAsync(int id, NamedPipeServerStream pipe, Channel<CameraFrame> channel)
        {
            try
            {
                await foreach (var frame in channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                {
                    int length = frame.Jpeg.Length;
                    if (length <= 0 || length > MaxFrameBytes) continue;
                    byte[] header = new byte[16];
                    Buffer.BlockCopy(BitConverter.GetBytes(frame.Width), 0, header, 0, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(frame.Height), 0, header, 4, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(1), 0, header, 8, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(length), 0, header, 12, 4);
                    await pipe.WriteAsync(header, 0, header.Length, _cts.Token).ConfigureAwait(false);
                    await pipe.WriteAsync(frame.Jpeg, 0, length, _cts.Token).ConfigureAwait(false);
                }
            }
            catch { }
            finally
            {
                _clients.TryRemove(id, out _);
                channel.Writer.TryComplete();
                pipe.Dispose();
            }
        }

        private async Task ReadFramesAsync()
        {
            try
            {
                var header = new byte[16];
                while (!_cts.IsCancellationRequested)
                {
                    await _client.ReadExactlyAsync(header.AsMemory(), _cts.Token).ConfigureAwait(false);
                    int width = BitConverter.ToInt32(header, 0);
                    int height = BitConverter.ToInt32(header, 4);
                    int length = BitConverter.ToInt32(header, 12);
                    if (width <= 0 || height <= 0 || width > 8192 || height > 8192
                        || BitConverter.ToInt32(header, 8) != 1 || length <= 0 || length > MaxFrameBytes)
                        throw new InvalidDataException("Trame caméra partagée invalide.");

                    var jpeg = new byte[length];
                    await _client.ReadExactlyAsync(jpeg.AsMemory(), _cts.Token).ConfigureAwait(false);
                    FrameReceived?.Invoke(jpeg, width, height);
                }
            }
            catch { }
            finally
            {
                if (!_disposed) ConnectionEnded?.Invoke();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            try { _client?.Dispose(); } catch { }
            foreach (var channel in _clients.Values) channel.Writer.TryComplete();
        }
    }
}
