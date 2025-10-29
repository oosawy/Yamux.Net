// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// Copyright (C) 2014 HashiCorp, Inc.
//
// Portions Copyright (C) 2025 Yuta Osawa
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace Yamux.Net;

// Frame is used to multiplex data over a session
internal readonly struct Frame
{
    public readonly byte MsgType;
    public readonly ushort Flags;
    public readonly uint StreamId;
    public readonly uint Length;
    public readonly ReadOnlyMemory<byte> Payload;

    public Frame(byte msgType, ushort flags, uint streamId, ReadOnlyMemory<byte> payload)
    {
        MsgType = msgType;
        Flags = flags;
        StreamId = streamId;
        Payload = payload;
        Length = (uint)payload.Length;
    }

    public Frame(byte msgType, ushort flags, uint streamId, uint length)
    {
        MsgType = msgType;
        Flags = flags;
        StreamId = streamId;
        Length = length;
        Payload = ReadOnlyMemory<byte>.Empty;
    }
}

public partial class YamuxSession : IAsyncDisposable
{
    private readonly Stream _connection;
    private readonly YamuxConfig _config;
    private readonly ILogger _logger;
    private readonly bool _isClient;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private readonly ConcurrentDictionary<uint, YamuxStream> _streams = new();
    private uint _nextStreamId;

    private readonly Channel<YamuxStream> _acceptCh;
    private readonly Channel<Frame> _sendCh = Channel.CreateUnbounded<Frame>(new UnboundedChannelOptions { SingleReader = true });

    private readonly SemaphoreSlim _synSemaphore;

    private readonly Task _recvLoopTask;
    private readonly Task _sendLoopTask;
    private readonly Task? _keepAliveTask;

    internal YamuxConfig Config => _config;

    internal ILogger Logger => _logger;

    private readonly ConcurrentDictionary<uint, (TaskCompletionSource<bool> Tcs, Stopwatch Stopwatch)> _pingsMap = new();

    private long _pings; // For tracking ping IDs
    private int _goAway; // 0 = active, 1 = local goaway, 2 = remote goaway

    private readonly object _disposeLock = new();
    private bool _isDisposed;
    private Exception? _shutdownError;
    private readonly object _shutdownErrorLock = new();

    public int NumStreams => _streams.Count;

    public bool IsDisposed => _isDisposed;

    private YamuxSession(Stream connection, YamuxConfig config, bool isClient)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _config.Verify();
        _logger = _config.LoggerFactory.CreateLogger<YamuxSession>();
        _isClient = isClient;

        _nextStreamId = isClient ? 1u : 2u;
        _acceptCh = Channel.CreateBounded<YamuxStream>(new BoundedChannelOptions(config.AcceptBacklog) { FullMode = BoundedChannelFullMode.DropWrite });
        _synSemaphore = new SemaphoreSlim(config.AcceptBacklog, config.AcceptBacklog);

        _recvLoopTask = Task.Run(RecvLoop);
        _sendLoopTask = Task.Run(SendLoop);
        if (config.EnableKeepAlive)
        {
            _keepAliveTask = Task.Run(KeepAliveLoop);
        }
    }

    public static YamuxSession CreateClient(Stream connection, YamuxConfig? config = null)
    {
        return new YamuxSession(connection, config ?? YamuxConfig.Default, isClient: true);
    }

    public static YamuxSession CreateServer(Stream connection, YamuxConfig? config = null)
    {
        return new YamuxSession(connection, config ?? YamuxConfig.Default, isClient: false);
    }

    private async Task RecvLoop()
    {
        var headerBuffer = new byte[YamuxConstants.HeaderSize];
        var cancellationToken = _cancellationTokenSource.Token;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _connection.ReadExactlyAsync(headerBuffer, cancellationToken).ConfigureAwait(false);

                var header = new YamuxHeader(headerBuffer);

                if (header.Version != YamuxConstants.ProtocolVersion)
                {
                    _logger.LogError("Invalid protocol version: {Version}", header.Version);
                    throw new InvalidVersionException();
                }

                switch (header.MsgType)
                {
                    case YamuxConstants.FrameTypeData:
                        await HandleDataFrame(header.StreamId, header.Length, cancellationToken).ConfigureAwait(false);
                        break;
                    case YamuxConstants.FrameTypeWindowUpdate:
                        await HandleWindowUpdateFrame(header.StreamId, header.Flags, header.Length, cancellationToken).ConfigureAwait(false);
                        break;
                    case YamuxConstants.FrameTypePing:
                        HandlePingFrame(header.StreamId, header.Flags, header.Length);
                        break;
                    case YamuxConstants.FrameTypeGoAway:
                        await HandleGoAwayFrame(header.Length).ConfigureAwait(false);
                        break;
                    default:
                        _logger.LogError("Invalid message type: {MsgType}", header.MsgType);
                        if (header.Length > 0)
                        {
                            await _connection.CopyBytesToAsync(Stream.Null, header.Length, cancellationToken).ConfigureAwait(false);
                        }
                        throw new InvalidMessageTypeException();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation during shutdown
        }
        catch (EndOfStreamException ex)
        {
            // Connection closed - only log if not already shutting down
            if (!_isDisposed)
            {
                ExitErr(ex);
            }
        }
        catch (IOException ex)
        {
            // Network error - only log if not a normal close
            if (!_isDisposed && !ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(ex, "Failed to read from connection");
                ExitErr(ex);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in receive loop");
            ExitErr(ex);
        }
    }

    internal bool TrySendFrame(Frame frame)
    {
        return _sendCh.Writer.TryWrite(frame);
    }

    internal Task SendWindowUpdateAsync(uint streamId, uint delta)
    {
        if (TrySendFrame(new Frame(YamuxConstants.FrameTypeWindowUpdate, 0, streamId, delta)))
        {
            return Task.CompletedTask;
        }
        return Task.FromException(new SessionShutdownException());
    }

    private async Task HandleDataFrame(uint streamId, uint length, CancellationToken cancellationToken)
    {
        if (!_streams.TryGetValue(streamId, out var stream))
        {
            if (length > 0)
            {
                _logger.LogWarning("Discarding data for unknown stream: {StreamId}", streamId);
                await _connection.CopyBytesToAsync(Stream.Null, length, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if (!stream.TryConsumeRecvWindow(length))
        {
            _logger.LogError("Stream {StreamId} received data larger than window. Length: {Length}", streamId, length);
            await SendGoAway(YamuxConstants.GoAwayCodeProtocolError).ConfigureAwait(false);
            throw new ReceiveWindowExceededException();
        }

        if (length > 0)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent((int)length);
            try
            {
                await _connection.ReadExactlyAsync(buffer.AsMemory(0, (int)length), cancellationToken).ConfigureAwait(false);
                await stream.ReceiveDataAsync(buffer.AsMemory(0, (int)length), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private async Task HandleWindowUpdateFrame(uint streamId, ushort flags, uint length, CancellationToken cancellationToken)
    {
        if ((flags & YamuxConstants.FrameFlagSyn) != 0)
        {
            if (Volatile.Read(ref _goAway) != 0)
            {
                _sendCh.Writer.TryWrite(new Frame(YamuxConstants.FrameTypeWindowUpdate, YamuxConstants.FrameFlagRst, streamId, 0));
                return;
            }

            var isClientStream = (streamId % 2) == 1;
            if (_isClient == isClientStream)
            {
                _logger.LogError("Received SYN for stream {StreamId} with invalid ID parity.", streamId);
                await SendGoAway(YamuxConstants.GoAwayCodeProtocolError).ConfigureAwait(false);
                return;
            }

            var stream = new YamuxStream(this, streamId, StreamState.SynReceived);
            if (!_streams.TryAdd(streamId, stream))
            {
                _logger.LogWarning("Received SYN for duplicate stream {StreamId}.", streamId);
                _sendCh.Writer.TryWrite(new Frame(YamuxConstants.FrameTypeWindowUpdate, YamuxConstants.FrameFlagRst, streamId, 0));
                return;
            }

            stream.OnWindowUpdate(length);

            _sendCh.Writer.TryWrite(new Frame(YamuxConstants.FrameTypeWindowUpdate, YamuxConstants.FrameFlagAck, streamId, Config.MaxStreamWindowSize));

            stream.SetState(StreamState.Established);
            if (!_acceptCh.Writer.TryWrite(stream))
            {
                _logger.LogWarning("Accept channel is full, dropping new stream {StreamId}.", streamId);
                RemoveStream(streamId);
                _sendCh.Writer.TryWrite(new Frame(YamuxConstants.FrameTypeWindowUpdate, YamuxConstants.FrameFlagRst, streamId, 0));
            }
            return;
        }

        if ((flags & YamuxConstants.FrameFlagAck) != 0)
        {
            if (_streams.TryGetValue(streamId, out var stream))
            {
                stream.AckReceived();
                stream.OnWindowUpdate(length);
            }
            return;
        }

        if ((flags & YamuxConstants.FrameFlagFin) != 0)
        {
            if (_streams.TryGetValue(streamId, out var streamFin))
            {
                await streamFin.RemoteFin().ConfigureAwait(false);
            }
            return;
        }
        if ((flags & YamuxConstants.FrameFlagRst) != 0)
        {
            if (_streams.TryGetValue(streamId, out var streamRst))
            {
                await streamRst.RemoteRst().ConfigureAwait(false);
            }
            return;
        }

        if (_streams.TryGetValue(streamId, out var streamToUpdate))
        {
            streamToUpdate.OnWindowUpdate(length);
        }
    }

    private void HandlePingFrame(uint streamId, ushort flags, uint pingId)
    {
        if (streamId != 0) return;

        if ((flags & YamuxConstants.FrameFlagSyn) != 0)
        {
            _sendCh.Writer.TryWrite(new Frame(YamuxConstants.FrameTypePing, YamuxConstants.FrameFlagAck, 0, pingId));
        }
        else if ((flags & YamuxConstants.FrameFlagAck) != 0)
        {
            if (_pingsMap.TryGetValue(pingId, out var ping))
            {
                ping.Tcs.TrySetResult(true);
            }
        }
    }

    private async Task HandleGoAwayFrame(uint code)
    {
        _logger.LogInformation("GoAway received. Code: {Code}", code);

        Interlocked.CompareExchange(ref _goAway, 2, 0);
        _acceptCh.Writer.TryComplete();

        if (code != YamuxConstants.GoAwayCodeNormal)
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task SendLoop()
    {
        var cancellationToken = _cancellationTokenSource.Token;
        var headerBuffer = new byte[YamuxConstants.HeaderSize];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await _sendCh.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

                var header = new YamuxHeader(headerBuffer);
                header.Encode(frame.MsgType, frame.Flags, frame.StreamId, frame.Length);

                using var timeoutCts = new CancellationTokenSource(_config.ConnectionWriteTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                try
                {
                    await _connection.WriteAsync(headerBuffer, linkedCts.Token).ConfigureAwait(false);
                    if (!frame.Payload.IsEmpty)
                    {
                        await _connection.WriteAsync(frame.Payload, linkedCts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogError("Connection write timeout");
                        throw new ConnectionWriteTimeoutException();
                    }
                    throw;
                }
                catch (IOException ex)
                {
                    _logger.LogError(ex, "Failed to write to connection");
                    throw;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in send loop");
            ExitErr(ex);
        }
    }

    public async Task<YamuxStream> AcceptAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _goAway) != 0) throw new RemoteGoAwayException();

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, cancellationToken);
            return await _acceptCh.Reader.ReadAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            if (_isDisposed) throw new SessionShutdownException();
            throw new RemoteGoAwayException();
        }
        catch (OperationCanceledException)
        {
            if (_isDisposed) throw new SessionShutdownException();
            if (Volatile.Read(ref _goAway) != 0) throw new RemoteGoAwayException();
            throw;
        }
    }

    public async Task<YamuxStream> OpenAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _goAway) != 0)
        {
            throw new RemoteGoAwayException();
        }

        await _synSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = AllocateStream();

            if (!_streams.TryAdd(stream.StreamId, stream))
            {
                throw new DuplicateStreamException(); // Should not happen
            }

            try
            {
                _sendCh.Writer.TryWrite(new Frame(YamuxConstants.FrameTypeWindowUpdate, YamuxConstants.FrameFlagSyn, stream.StreamId, Config.MaxStreamWindowSize));

                var ackTask = stream.WaitForAckAsync(cancellationToken);

                if (Config.StreamOpenTimeout > TimeSpan.Zero)
                {
                    using var timeoutCts = new CancellationTokenSource(Config.StreamOpenTimeout);
                    var timeoutTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);

                    var completedTask = await Task.WhenAny(ackTask, timeoutTask).ConfigureAwait(false);

                    if (completedTask == timeoutTask)
                    {
                        _logger.LogError("Stream open timed out for stream {StreamId}. Closing session.", stream.StreamId);
                        await DisposeAsync().ConfigureAwait(false);
                        throw new TimeoutException("Stream open timed out, session closed.");
                    }
                }

                await ackTask.ConfigureAwait(false);
            }
            catch
            {
                RemoveStream(stream.StreamId);
                throw;
            }

            return stream;
        }
        catch
        {
            _synSemaphore.Release();
            throw;
        }
    }

    private YamuxStream AllocateStream()
    {
        var streamId = Interlocked.Add(ref _nextStreamId, 2) - 2;
        if (streamId >= uint.MaxValue - 1)
        {
            throw new StreamsExhaustedException();
        }
        return new YamuxStream(this, streamId, StreamState.Init);
    }

    internal void ReleaseSynSemaphore() => _synSemaphore.Release();

    internal void RemoveStream(uint streamId)
    {
        if (_streams.TryRemove(streamId, out var stream))
        {
            if (stream.IsOutgoingUnacked)
            {
                _synSemaphore.Release();
            }
        }
    }

    internal Task SendFinAsync(uint streamId)
    {
        return TrySendFrame(new Frame(YamuxConstants.FrameTypeWindowUpdate, YamuxConstants.FrameFlagFin, streamId, 0))
            ? Task.CompletedTask
            : Task.FromException(new SessionShutdownException());
    }

    internal Task SendRstAsync(uint streamId)
    {
        return TrySendFrame(new Frame(YamuxConstants.FrameTypeWindowUpdate, YamuxConstants.FrameFlagRst, streamId, 0))
            ? Task.CompletedTask
            : Task.FromException(new SessionShutdownException());
    }



    public async Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var pingId = (uint)Interlocked.Increment(ref _pings);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();
        _pingsMap[pingId] = (tcs, stopwatch);

        if (!_sendCh.Writer.TryWrite(new Frame(YamuxConstants.FrameTypePing, YamuxConstants.FrameFlagSyn, 0, pingId)))
        {
            _pingsMap.TryRemove(pingId, out _);
            throw new SessionShutdownException();
        }

        using var timeoutCts = new CancellationTokenSource(_config.ConnectionWriteTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, timeoutCts.Token, cancellationToken);
        using var registration = linkedCts.Token.Register(() => tcs.TrySetCanceled());

        try
        {
            await tcs.Task.ConfigureAwait(false);
            if (_pingsMap.TryRemove(pingId, out var ping))
            {
                ping.Stopwatch.Stop();
                return ping.Stopwatch.Elapsed;
            }
            throw new InvalidOperationException("Ping data not found after completion.");
        }
        catch
        {
            _pingsMap.TryRemove(pingId, out _);
            throw;
        }
    }

    public Task GoAwayAsync()
    {
        return SendGoAway(YamuxConstants.GoAwayCodeNormal);
    }

    private Task SendGoAway(uint code)
    {
        ThrowIfDisposed();

        if (Interlocked.CompareExchange(ref _goAway, 1, 0) != 0)
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation("Sending GoAway with code {Code}.", code);
        _sendCh.Writer.TryWrite(new Frame(YamuxConstants.FrameTypeGoAway, 0, 0, code));
        _acceptCh.Writer.TryComplete();

        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        return DisposeAsync().AsTask();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
        }

        // Also set goaway flag to prevent race conditions
        Interlocked.CompareExchange(ref _goAway, 1, 0);

        _cancellationTokenSource.Cancel();
        _acceptCh.Writer.TryComplete();
        _sendCh.Writer.TryComplete();

        // Close all streams
        foreach (var stream in _streams.Values)
        {
            await stream.SessionClosing();
        }
        _streams.Clear();

        // Complete any pending pings
        foreach (var ping in _pingsMap.Values)
        {
            ping.Tcs.TrySetCanceled();
        }
        _pingsMap.Clear();

        var tasks = new List<Task> { _recvLoopTask, _sendLoopTask };
        if (_keepAliveTask != null)
            tasks.Add(_keepAliveTask);

        await Task.WhenAll(tasks).ConfigureAwait(false);

        _cancellationTokenSource.Dispose();
    }

    protected void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    private async Task KeepAliveLoop()
    {
        var cancellationToken = _cancellationTokenSource.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_config.KeepAliveInterval, cancellationToken).ConfigureAwait(false);

                try
                {
                    var roundtripTime = await PingAsync(cancellationToken).ConfigureAwait(false);
                    _logger.LogDebug("Keep-alive ping successful. Roundtrip: {RoundtripTime}ms", roundtripTime.TotalMilliseconds);
                }
                catch (SessionShutdownException)
                {
                    // Session is shutting down, exit gracefully
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Keep-alive ping failed");
                    ExitErr(new KeepAliveTimeoutException());
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in keep-alive loop");
            ExitErr(ex);
        }
    }

    // ExitErr is used to handle an error that is causing the session to terminate
    private void ExitErr(Exception error)
    {
        lock (_shutdownErrorLock)
        {
            _shutdownError ??= error;
        }

        // Initiate shutdown without blocking
        _ = Task.Run(async () => await DisposeAsync().ConfigureAwait(false));
    }
}
