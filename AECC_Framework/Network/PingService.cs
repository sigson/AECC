using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using AECC.Core.Logging;
using AECC.Extensions;

namespace AECC.Network
{
    /// <summary>
    /// Global ping configuration.
    /// </summary>
    public static class PingSettings
    {
        /// <summary>
        /// How often (ms) to send a Ping to each confirmed socket.
        /// Default: 2000ms (2 seconds).
        /// </summary>
        public static int IntervalMs = 2000;

        /// <summary>
        /// If a Pong has not been received within this many milliseconds
        /// after a Ping was sent, the socket is considered timed out.
        /// TimeoutMs = 0 disables timeout detection.
        /// Default: 10000ms (10 seconds).
        /// </summary>
        public static int TimeoutMs = 10000;
    }

    /// <summary>
    /// Manages periodic ping/pong measurements for all confirmed sockets.
    ///
    /// Protocol:
    ///   1. PingService sends a Ping message carrying the sender's UTC ticks.
    ///   2. The remote side echoes the same ticks back as a Pong.
    ///   3. On receiving the Pong, the service computes RTT = now − echoed_ticks.
    ///   4. RTT is written to ISocketAdapter.LatencyMs.
    ///
    /// Both sides run PingService: the server pings all client sessions,
    /// the client pings its server connection. Both directions update LatencyMs
    /// independently — the value on each side reflects *that side's* measured RTT.
    ///
    /// Timeout: if PingSentTicks != 0 and (now − PingSentTicks) > TimeoutMs,
    /// the OnPingTimeout callback fires. By default it logs a warning;
    /// business logic can subscribe to disconnect the socket.
    /// </summary>
    public class PingService : IDisposable
    {
        private TimerCompat _timer;

        /// <summary>
        /// Reference to the set of confirmed sockets to ping.
        /// On the server side, this is NetworkService.SocketsById.Values.
        /// On the client side, this is NetworkService.ClientSockets.Values.
        /// </summary>
        private readonly Func<ConcurrentDictionary<long, ISocketAdapter>> _socketsProvider;

        /// <summary>
        /// Low-level send: wraps bytes in the right frame for the socket's protocol.
        /// </summary>
        private readonly Action<ISocketAdapter, byte, byte[]> _sendSystemMessage;

        /// <summary>
        /// Fired when a socket exceeds PingSettings.TimeoutMs without a Pong.
        /// </summary>
        public event Action<ISocketAdapter> OnPingTimeout;

        public PingService(
            Func<ConcurrentDictionary<long, ISocketAdapter>> socketsProvider,
            Action<ISocketAdapter, byte, byte[]> sendSystemMessage)
        {
            _socketsProvider = socketsProvider;
            _sendSystemMessage = sendSystemMessage;
        }

        /// <summary>
        /// Start the periodic ping timer.
        /// </summary>
        public void Start()
        {
            _timer = new TimerCompat(PingSettings.IntervalMs, (sender, e) => PingTick(), loop: true, asyncRun: true);
            _timer.Start();
        }

        private void PingTick()
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            var sockets = _socketsProvider();

            foreach (var kvp in sockets)
            {
                var socket = kvp.Value;
                if (socket == null || !socket.IsConnected)
                    continue;

                // ── Timeout check ──
                if (PingSettings.TimeoutMs > 0 && socket.PingSentTicks != 0)
                {
                    long elapsed = (nowTicks - socket.PingSentTicks) / TimeSpan.TicksPerMillisecond;
                    if (elapsed > PingSettings.TimeoutMs)
                    {
                        NLogger.LogNetwork($"Ping timeout on socket {socket.Id} ({elapsed}ms)");
                        OnPingTimeout?.Invoke(socket);
                        socket.PingSentTicks = 0; // reset so we don't fire repeatedly
                        continue;
                    }
                }

                // ── Send Ping ──
                socket.PingSentTicks = nowTicks;
                var payload = BitConverter.GetBytes(nowTicks);
                try
                {
                    _sendSystemMessage(socket, MessageType.Ping, payload);
                }
                catch (Exception ex)
                {
                    NLogger.LogError($"Failed to send Ping to socket {socket.Id}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Handle an incoming Ping message — immediately reply with Pong echoing the payload.
        /// Called from NetworkService.HandleFramedMessage.
        /// </summary>
        public void HandlePing(ISocketAdapter socket, byte[] payload)
        {
            // Echo the sender's ticks back as Pong
            _sendSystemMessage(socket, MessageType.Pong, payload);
        }

        /// <summary>
        /// Handle an incoming Pong message — compute RTT and update the socket.
        /// Called from NetworkService.HandleFramedMessage.
        /// </summary>
        public void HandlePong(ISocketAdapter socket, byte[] payload)
        {
            if (payload.Length < 8) return;

            long sentTicks = BitConverter.ToInt64(payload, 0);
            long nowTicks = DateTime.UtcNow.Ticks;
            long rttTicks = nowTicks - sentTicks;

            int rttMs = (int)(rttTicks / TimeSpan.TicksPerMillisecond);
            if (rttMs < 0) rttMs = 0; // clock drift guard

            socket.LatencyMs = rttMs;
            socket.LastPingTicks = nowTicks;
            socket.PingSentTicks = 0; // no longer in-flight
        }

        public void Dispose()
        {
            _timer?.Stop();
            _timer?.Dispose();
            _timer = null;
        }
    }
}
