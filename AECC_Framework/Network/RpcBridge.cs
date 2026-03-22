using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using StreamJsonRpc;
using AECC.Core.Logging;

namespace AECC.Network
{
    /// <summary>
    /// Bridges a StreamJsonRpc instance over an ISocketAdapter.
    ///
    /// This allows business logic to make typed RPC calls (request → response)
    /// alongside the fire-and-forget event pipeline.
    ///
    /// Wire protocol: RPC messages are sent/received with MessageType 0x20.
    /// The rest of the binary protocol (events, system handshake) is unaffected.
    ///
    /// Usage:
    ///   var bridge = new RpcBridge(socket, myRpcTargetObject);
    ///   bridge.Start();
    ///   var result = await bridge.Rpc.InvokeAsync&lt;int&gt;("Add", 1, 2);
    ///   bridge.Dispose();
    /// </summary>
    public class RpcBridge : IDisposable
    {
        public const byte RpcMessageType = 0x20;

        public JsonRpc Rpc { get; private set; }

        private readonly ISocketAdapter _socket;
        private readonly Pipe _incomingPipe = new();
        private readonly Pipe _outgoingPipe = new();
        private CancellationTokenSource _cts = new();
        private Task _pumpTask;

        /// <summary>
        /// Create an RPC bridge over a socket.
        /// </summary>
        /// <param name="socket">The underlying socket adapter.</param>
        /// <param name="rpcTarget">
        /// Optional local RPC target object. Methods on this object annotated with
        /// [JsonRpcMethod] will be callable from the remote side.
        /// </param>
        public RpcBridge(ISocketAdapter socket, object rpcTarget = null)
        {
            _socket = socket;

            var duplexPipe = new DuplexPipe(_incomingPipe.Reader, _outgoingPipe.Writer);

            // StreamJsonRpc with MessagePack formatter for efficient serialization
            var formatter = new MessagePackFormatter();
            var handler = new LengthHeaderMessageHandler(duplexPipe, formatter);
            Rpc = new JsonRpc(handler);

            if (rpcTarget != null)
                Rpc.AddLocalRpcTarget(rpcTarget);

            Rpc.TraceSource.Switch.Level = System.Diagnostics.SourceLevels.Warning;
        }

        /// <summary>
        /// Start the RPC bridge. Call after configuring targets.
        /// </summary>
        public void Start()
        {
            Rpc.StartListening();
            _pumpTask = PumpOutgoingAsync(_cts.Token);
        }

        /// <summary>
        /// Feed incoming RPC data (from the network) into the bridge.
        /// Called by NetworkService when it receives a message with RpcMessageType.
        /// </summary>
        public void FeedIncoming(byte[] data)
        {
            try
            {
                _incomingPipe.Writer.Write(data);
                _incomingPipe.Writer.FlushAsync().AsTask().Wait(100);
            }
            catch (Exception ex)
            {
                NLogger.LogError($"RpcBridge FeedIncoming error: {ex.Message}");
            }
        }

        /// <summary>
        /// Background task: reads from StreamJsonRpc's output and sends over the socket.
        /// </summary>
        private async Task PumpOutgoingAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var result = await _outgoingPipe.Reader.ReadAsync(ct);
                    var buffer = result.Buffer;

                    foreach (var segment in buffer)
                    {
                        byte[] payload = segment.ToArray();
                        byte[] frame;

                        if (_socket.Protocol == NetworkProtocol.TCP)
                            frame = StreamFrameAccumulator.Pack(RpcMessageType, payload);
                        else
                            frame = DatagramFrame.Pack(RpcMessageType, payload);

                        _socket.SendAsync(frame);
                    }

                    _outgoingPipe.Reader.AdvanceTo(buffer.End);

                    if (result.IsCompleted)
                        break;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                NLogger.LogError($"RpcBridge outgoing pump error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            Rpc?.Dispose();
            _incomingPipe.Writer.Complete();
            _outgoingPipe.Reader.Complete();
            _pumpTask?.Wait(1000);
            _cts.Dispose();
        }

        /// <summary>
        /// Simple IDuplexPipe implementation combining a reader and writer.
        /// </summary>
        private class DuplexPipe : IDuplexPipe
        {
            public PipeReader Input { get; }
            public PipeWriter Output { get; }

            public DuplexPipe(PipeReader input, PipeWriter output)
            {
                Input = input;
                Output = output;
            }
        }
    }
}
