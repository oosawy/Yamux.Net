# Yamux.Net

A .NET port of [hashicorp/yamux](https://github.com/hashicorp/yamux), a
multiplexing library for Golang.

This library provides a .NET implementation of the Yamux protocol, allowing
multiple logical streams to be multiplexed over a single underlying connection
(such as a TCP connection). It offers an asynchronous API using `async/await`
and is highly configurable.

For more information about Yamux, please refer to the documentation of the
[original Go project](https://github.com/hashicorp/yamux).

## Installation

```bash
dotnet add package Yamux.Net
```

## Usage

### Creating a Session

**Server:**

```csharp
using System.Net;
using System.Net.Sockets;
using Yamux.Net;

var listener = new TcpListener(IPAddress.Any, 8080);
listener.Start();
var client = await listener.AcceptTcpClientAsync();

await using var session = YamuxSession.CreateServer(
    client.GetStream(),
    YamuxConfig.Default
);
```

**Client:**

```csharp
using System.Net.Sockets;
using Yamux.Net;

var client = new TcpClient();
await client.ConnectAsync("127.0.0.1", 8080);

await using var session = YamuxSession.CreateClient(
    client.GetStream(),
    YamuxConfig.Default
);
```

### Opening a Stream (Client)

```csharp
using System.Text;

var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var cancellationToken = cts.Token;

await using var stream = await session.OpenAsync(cancellationToken);

// Write data
var message = "Hello from client!";
var buffer = Encoding.UTF8.GetBytes(message);
await stream.WriteAsync(buffer, cancellationToken);

// Read response
var responseBuffer = new byte[1024];
var bytesRead = await stream.ReadAsync(responseBuffer, cancellationToken);
var response = Encoding.UTF8.GetString(responseBuffer, 0, bytesRead);
Console.WriteLine($"Received from server: {response}");
```

### Accepting a Stream (Server)

```csharp
using System.Text;

var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var cancellationToken = cts.Token;

try
{
    while (!cancellationToken.IsCancellationRequested)
    {
        await using var stream = await session.AcceptAsync(cancellationToken);
        Console.WriteLine($"Accepted new stream: {stream.StreamId}");

        // Handle the stream in a separate task to avoid blocking the accept loop
        _ = Task.Run(async () =>
        {
            // Read data
            var buffer = new byte[1024];
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
            var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            Console.WriteLine($"Received from client: {message}");

            // Write response
            var response = "Hello from server!";
            var responseBuffer = Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(responseBuffer, cancellationToken);
        }, cancellationToken);
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Accept loop cancelled.");
}
```

## Configuration

You can customize the behavior of the session by providing a `YamuxConfig`
object.

```csharp
using Microsoft.Extensions.Logging;

var myLoggerFactory = LoggerFactory.Create(builder => builder.AddConsole());

var config = new YamuxConfig
{
    AcceptBacklog = 256,
    EnableKeepAlive = true,
    KeepAliveInterval = TimeSpan.FromSeconds(30),
    ConnectionWriteTimeout = TimeSpan.FromSeconds(10),
    MaxStreamWindowSize = 1024 * 1024, // 1MB
    StreamOpenTimeout = TimeSpan.FromSeconds(75),
    StreamCloseTimeout = TimeSpan.FromMinutes(5),
    LoggerFactory = myLoggerFactory
};

var session = YamuxSession.CreateClient(stream, config);
```

## API Reference

Only some methods/properties are listed. Please refer to the source code for
details.

### YamuxSession: IAsyncDisposable

- `static YamuxSession CreateClient(Stream connection, YamuxConfig? config = null)`
- `static YamuxSession CreateServer(Stream connection, YamuxConfig? config = null)`
- `Task<YamuxStream> AcceptAsync(CancellationToken cancellationToken = default)`
- `Task<YamuxStream> OpenAsync(CancellationToken cancellationToken = default)`
- `Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default)`
- `Task GoAwayAsync()`
- `Task CloseAsync()`
- `ValueTask DisposeAsync()`

Properties:

- `int NumStreams { get; }`
- `bool IsDisposed { get; }`

### YamuxStream: Stream, IAsyncDisposable

- `Task<int> ReadAsync(Memory<byte>, CancellationToken cancellationToken)`
- `Task WriteAsync(Memory<byte>, CancellationToken cancellationToken)`
- `Task CloseAsync()`
- `ValueTask DisposeAsync()`

Properties:

- `YamuxSession Session { get; }`
- `uint StreamId { get; }`
- `bool CanRead { get; }`
- `bool CanWrite { get; }`
- `bool CanTimeout { get; }`

### YamuxConfig

- `int AcceptBacklog { get; set; }`
- `bool EnableKeepAlive { get; set; }`
- `TimeSpan KeepAliveInterval { get; set; }`
- `TimeSpan ConnectionWriteTimeout { get; set; }`
- `uint MaxStreamWindowSize { get; set; }`
- `TimeSpan StreamOpenTimeout { get; set; }`
- `TimeSpan StreamCloseTimeout { get; set; }`
- `ILoggerFactory LoggerFactory { get; set; }`

## License

This project is licensed under the Mozilla Public License 2.0 (MPL-2.0).

Based on [HashiCorp's Yamux](https://github.com/hashicorp/yamux).
