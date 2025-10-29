// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// Copyright (C) 2014 HashiCorp, Inc.
//
// Portions Copyright (C) 2025 Yuta Osawa
using System.Buffers.Binary;

namespace Yamux.Net;

internal readonly ref struct YamuxHeader
{
    private readonly Span<byte> _buffer;

    public YamuxHeader(Span<byte> buffer)
    {
        if (buffer.Length < YamuxConstants.HeaderSize)
            throw new ArgumentException("Buffer is too small for a Yamux header.", nameof(buffer));
        _buffer = buffer;
    }

    public byte Version => _buffer[0];
    public byte MsgType => _buffer[1];
    public ushort Flags => BinaryPrimitives.ReadUInt16BigEndian(_buffer.Slice(2, 2));
    public uint StreamId => BinaryPrimitives.ReadUInt32BigEndian(_buffer.Slice(4, 4));
    public uint Length => BinaryPrimitives.ReadUInt32BigEndian(_buffer.Slice(8, 4));

    public void Encode(byte msgType, ushort flags, uint streamId, uint length)
    {
        _buffer[0] = YamuxConstants.ProtocolVersion;
        _buffer[1] = msgType;
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.Slice(2, 2), flags);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.Slice(4, 4), streamId);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.Slice(8, 4), length);
    }

    public override string ToString()
    {
        return $"Version: {Version}, Type: {MsgType}, Flags: {Flags}, StreamId: {StreamId}, Length: {Length}";
    }
}
