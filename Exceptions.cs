// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// Copyright (C) 2014 HashiCorp, Inc.
//
// Portions Copyright (C) 2025 Yuta Osawa
namespace Yamux.Net;

public class YamuxException : Exception
{
  public YamuxException(string message) : base(message)
  {
  }

  public YamuxException(string message, Exception innerException) : base(message, innerException)
  {
  }
}

public class InvalidVersionException : YamuxException
{
  public InvalidVersionException() : base("invalid protocol version")
  {
  }
}

public class InvalidMessageTypeException : YamuxException
{
  public InvalidMessageTypeException() : base("invalid message type")
  {
  }
}

public class SessionShutdownException : YamuxException
{
  public SessionShutdownException() : base("session shutdown")
  {
  }
}

public class StreamsExhaustedException : YamuxException
{
  public StreamsExhaustedException() : base("streams exhausted")
  {
  }
}

public class DuplicateStreamException : YamuxException
{
  public DuplicateStreamException() : base("duplicate stream initiated")
  {
  }
}

public class ReceiveWindowExceededException : YamuxException
{
  public ReceiveWindowExceededException() : base("receive window exceeded")
  {
  }
}

public class StreamClosedException : YamuxException
{
  public StreamClosedException() : base("stream closed")
  {
  }
}

public class RemoteGoAwayException : YamuxException
{
  public RemoteGoAwayException() : base("remote end is not accepting connections")
  {
  }
}

public class ConnectionResetException : YamuxException
{
  public ConnectionResetException() : base("connection reset")
  {
  }
}

public class ConnectionWriteTimeoutException : YamuxException
{
  public ConnectionWriteTimeoutException() : base("connection write timeout")
  {
  }
}

public class KeepAliveTimeoutException : YamuxException
{
  public KeepAliveTimeoutException() : base("keepalive timeout")
  {
  }
}
