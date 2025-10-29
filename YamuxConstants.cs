// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// Copyright (C) 2014 HashiCorp, Inc.
//
// Portions Copyright (C) 2025 Yuta Osawa
namespace Yamux.Net;

internal static class YamuxConstants
{
    internal const byte ProtocolVersion = 0;

    // Message types
    internal const byte FrameTypeData = 0;
    internal const byte FrameTypeWindowUpdate = 1;
    internal const byte FrameTypePing = 2;
    internal const byte FrameTypeGoAway = 3;

    // Flags
    internal const ushort FrameFlagSyn = 1;
    internal const ushort FrameFlagAck = 2;
    internal const ushort FrameFlagFin = 4;
    internal const ushort FrameFlagRst = 8;

    // GoAway codes
    internal const uint GoAwayCodeNormal = 0;
    internal const uint GoAwayCodeProtocolError = 1;
    internal const uint GoAwayCodeInternalError = 2;

    internal const uint InitialStreamWindowSize = 256 * 1024;

    internal const int HeaderSize = 12;
    internal const int MaxFrameSize = 32768;
}
