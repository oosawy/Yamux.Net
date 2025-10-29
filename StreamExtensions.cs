// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// Copyright (C) 2014 HashiCorp, Inc.
//
// Portions Copyright (C) 2025 Yuta Osawa
using System.Buffers;

namespace Yamux.Net;

internal static class StreamExtensions
{
  public static async Task CopyBytesToAsync(this Stream source, Stream destination, long count, CancellationToken cancellationToken)
  {
    var buffer = ArrayPool<byte>.Shared.Rent(81920);
    try
    {
      long remaining = count;
      while (remaining > 0)
      {
        int toRead = (int)Math.Min(remaining, buffer.Length);
        int bytesRead = await source.ReadAsync(buffer, 0, toRead, cancellationToken).ConfigureAwait(false);
        if (bytesRead == 0)
          throw new EndOfStreamException("Connection closed unexpectedly.");

        if (destination != Stream.Null)
          await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);

        remaining -= bytesRead;
      }
    }
    finally
    {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }
}
