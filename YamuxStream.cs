// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// Copyright (C) 2014 HashiCorp, Inc.
//
// Portions Copyright (C) 2025 Yuta Osawa
using System.Buffers;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;

namespace Yamux.Net;

internal enum StreamState
{
    Init,
    SynSent,
    SynReceived,
    Established,
    LocalClose,
    RemoteClose,
    Closed,
    Reset,
}

public class YamuxStream : Stream, IAsyncDisposable
{
    private readonly YamuxSession _session;
    private readonly uint _streamId;
    private bool _isDisposed;

    private long _recvWindow;
    private long _sendWindow;
    private readonly SemaphoreSlim _sendWindowSemaphore = new(0);
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private readonly object _stateLock = new();
    private CancellationTokenSource? _closeTimeoutCts;
    private Task? _closeTimeoutTask;
    private StreamState _state;

    private readonly Pipe _pipe = new();

    private readonly TaskCompletionSource _ackTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);


    internal YamuxStream(YamuxSession session, uint streamId, StreamState state)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _streamId = streamId;
        _state = state;
        var initialWindowSize = (long)session.Config.MaxStreamWindowSize;
        _sendWindow = initialWindowSize;
        _recvWindow = initialWindowSize;
    }

    internal void SetState(StreamState state)
    {
        lock (_stateLock)
        {
            _state = state;
        }
    }

    internal async Task WaitForAckAsync(CancellationToken cancellationToken)
    {
        SetState(StreamState.SynSent);
        using var registration = cancellationToken.Register(() => _ackTcs.TrySetCanceled());
        await _ackTcs.Task.ConfigureAwait(false);
    }

    internal void AckReceived()
    {
        lock (_stateLock)
        {
            if (_state == StreamState.SynSent)
            {
                _state = StreamState.Established;
                _session.ReleaseSynSemaphore();
                _ackTcs.TrySetResult();
            }
        }
    }

    internal bool IsOutgoingUnacked
    {
        get
        {
            lock (_stateLock)
            {
                return _state == StreamState.SynSent;
            }
        }
    }

    public YamuxSession Session => _session;

    public uint StreamId => _streamId;

    #region Stream overrides

    public override bool CanRead => !_isDisposed;
    public override bool CanSeek => false;
    public override bool CanWrite => !_isDisposed;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override bool CanTimeout => true;

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer, offset, count).GetAwaiter().GetResult();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return await ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var readResult = await _pipe.Reader.ReadAsync(cancellationToken);
        if (readResult.IsCanceled || (readResult.IsCompleted && readResult.Buffer.IsEmpty))
            return 0;

        var bufferToRead = readResult.Buffer;

        var actualReadLength = (int)Math.Min(buffer.Length, bufferToRead.Length);
        var slice = bufferToRead.Slice(0, actualReadLength);
        slice.CopyTo(buffer.Span);

        _pipe.Reader.AdvanceTo(slice.End);

        var initialWindow = (long)_session.Config.MaxStreamWindowSize;
        var newRecvWindow = Interlocked.Read(ref _recvWindow);

        if (newRecvWindow < initialWindow / 2)
        {
            var delta = initialWindow - newRecvWindow;
            Interlocked.Add(ref _recvWindow, delta);
            await _session.SendWindowUpdateAsync(_streamId, (uint)delta).ConfigureAwait(false);
        }

        return actualReadLength;
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        await WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (!buffer.IsEmpty)
            {
                var granted = await AcquireSendWindowAsync(buffer.Length, cancellationToken);
                var chunk = buffer.Slice(0, granted);

                if (!_session.TrySendFrame(new Frame(YamuxConstants.FrameTypeData, 0, _streamId, chunk)))
                    throw new SessionShutdownException();

                buffer = buffer.Slice(granted);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async ValueTask<int> AcquireSendWindowAsync(int needed, CancellationToken ct)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _sendWindow);
            if (current > 0)
            {
                var grant = (int)Math.Min(Math.Min(current, needed), YamuxConstants.MaxFrameSize);
                if (Interlocked.CompareExchange(ref _sendWindow, current - grant, current) == current)
                {
                    return grant;
                }
                // Contention, loop again
            }
            else
            {
                await _sendWindowSemaphore.WaitAsync(ct).ConfigureAwait(false);
            }
        }
    }

    public override void Flush()
    {
        // No-op
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    #endregion

    public override async ValueTask DisposeAsync()
    {
        if (_closeTimeoutTask != null) await _closeTimeoutTask.ConfigureAwait(false);
        await CloseAsync().ConfigureAwait(false);
    }

    public async Task CloseAsync()
    {
        bool alreadyClosing;
        lock (_stateLock)
        {
            alreadyClosing = _state == StreamState.LocalClose || _state == StreamState.Closed || _state == StreamState.Reset;
            if (alreadyClosing) return;
            _state = StreamState.LocalClose;
        }

        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            SetDisposed();

            await _session.SendFinAsync(_streamId).ConfigureAwait(false);
            await _pipe.Writer.CompleteAsync().ConfigureAwait(false);

            var closeTimeout = _session.Config.StreamCloseTimeout;
            if (closeTimeout > TimeSpan.Zero)
            {
                _closeTimeoutCts = new CancellationTokenSource();
                _closeTimeoutTask = Task.Delay(closeTimeout, _closeTimeoutCts.Token).ContinueWith(async _ =>
                {
                    await ForceResetByTimeout().ConfigureAwait(false);
                }, TaskContinuationOptions.NotOnCanceled);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ForceResetByTimeout()
    {
        bool needsReset = false;
        lock (_stateLock)
        {
            if (_state == StreamState.LocalClose)
            {
                _state = StreamState.Reset;
                needsReset = true;
            }
        }

        if (needsReset)
        {
            _session.Logger.LogWarning("Stream {StreamId} did not close gracefully within timeout, sending RST.", _streamId);
            await _session.SendRstAsync(_streamId).ConfigureAwait(false);
            await _pipe.Writer.CompleteAsync(new StreamClosedException()).ConfigureAwait(false);
            await _pipe.Reader.CompleteAsync(new StreamClosedException()).ConfigureAwait(false);
            _session.RemoveStream(_streamId);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    internal bool TryConsumeRecvWindow(uint length)
    {
        if (length == 0) return true;

        long current = Interlocked.Read(ref _recvWindow);
        while (current >= length)
        {
            if (Interlocked.CompareExchange(ref _recvWindow, current - length, current) == current)
                return true;
            current = Interlocked.Read(ref _recvWindow);
        }
        return false;
    }

    internal async ValueTask ReceiveDataAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (data.Length == 0)
            return;

        await _pipe.Writer.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await _pipe.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask SessionClosing()
    {
        _state = StreamState.Reset;
        SetDisposed();
        await _pipe.Writer.CompleteAsync(new SessionShutdownException());
        await _pipe.Reader.CompleteAsync(new SessionShutdownException());
    }

    internal async Task RemoteFin()
    {
        bool doRemove = false;
        lock (_stateLock)
        {
            if (_state == StreamState.LocalClose)
            {
                _state = StreamState.Closed;
                doRemove = true;
                _closeTimeoutCts?.Cancel();
            }
            else
            {
                _state = StreamState.RemoteClose;
            }
        }

        if (doRemove)
        {
            _session.RemoveStream(_streamId);
        }
        await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
    }

    internal async Task RemoteRst()
    {
        SetState(StreamState.Reset);
        SetDisposed();
        await _pipe.Writer.CompleteAsync(new ConnectionResetException()).ConfigureAwait(false);
        await _pipe.Reader.CompleteAsync(new ConnectionResetException()).ConfigureAwait(false);
        _session.RemoveStream(_streamId);
    }

    internal void OnWindowUpdate(uint delta)
    {
        if (delta == 0) return;

        var prior = Interlocked.Add(ref _sendWindow, delta);
        if (prior <= delta) // It was 0 or negative before update
        {
            _sendWindowSemaphore.Release();
        }
    }

    private void SetDisposed()
    {
        _isDisposed = true;
        _ackTcs.TrySetCanceled();
        // Make sure any waiting writers are unblocked
        if (_sendWindowSemaphore.CurrentCount == 0) _sendWindowSemaphore.Release();
    }
}
