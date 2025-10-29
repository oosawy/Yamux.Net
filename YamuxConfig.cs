// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// Copyright (C) 2014 HashiCorp, Inc.
//
// Portions Copyright (C) 2025 Yuta Osawa
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Yamux.Net;

public class YamuxConfig
{
    public int AcceptBacklog { get; set; } = 256;
    public bool EnableKeepAlive { get; set; } = true;
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan ConnectionWriteTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public uint MaxStreamWindowSize { get; set; } = YamuxConstants.InitialStreamWindowSize;
    public TimeSpan StreamOpenTimeout { get; set; } = TimeSpan.FromSeconds(75);
    public TimeSpan StreamCloseTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public ILoggerFactory LoggerFactory { get; set; } = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder
            .AddConsole()
            .SetMinimumLevel(LogLevel.Error)
        );

    public static YamuxConfig Default => new();

    internal void Verify()
    {
        if (AcceptBacklog <= 0)
            throw new ArgumentException("AcceptBacklog must be positive.", nameof(AcceptBacklog));
        if (EnableKeepAlive && KeepAliveInterval <= TimeSpan.Zero)
            throw new ArgumentException("KeepAliveInterval must be positive.", nameof(KeepAliveInterval));
        if (MaxStreamWindowSize < YamuxConstants.InitialStreamWindowSize)
            throw new ArgumentException($"MaxStreamWindowSize must be larger than {YamuxConstants.InitialStreamWindowSize}.", nameof(MaxStreamWindowSize));
        if (LoggerFactory == null)
            throw new ArgumentNullException(nameof(LoggerFactory));
    }
}
