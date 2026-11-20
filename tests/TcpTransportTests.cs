/*
 * This file is part of WebChannel.NET.
 * Copyright (C) 2026, Menlo Systems GmbH
 */

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

using Xunit;

using QWebChannel;

using static WebChannelTests.TestObjects;

namespace WebChannelTests
{
    public class TcpTransportTests
    {
        // A message of the server, which ends with a newline on TCP; its
        // content does not matter
        static readonly byte[] Message = Encoding.UTF8.GetBytes("{}\n");
        // Enough for anything that the transport could still send
        const int ReceiveBufferSize = 1024;

        /// <summary>
        /// A TCP server in the process for one connection, while this
        /// object lives
        /// </summary>
        sealed class TcpServer : IDisposable
        {
            readonly TcpListener listener =
                new TcpListener(IPAddress.Loopback, 0);

            public TcpServer()
            {
                listener.Start();
            }

            public int Port => ((IPEndPoint) listener.LocalEndpoint).Port;

            /// <summary>The connection of the next client</summary>
            public Task<TcpClient> Accept() => listener.AcceptTcpClientAsync();

            public void Dispose() => listener.Stop();
        }

        /**
         * @scenario The handler of a message throws, e.g. a signal callback
         * @given A transport connected to a server, with a message handler
         *        that throws
         * @when  The server sends a message
         * @then  The transport reports the exception and the end of its
         *        connection, and closes the socket
         */
        [Fact(Timeout = TestTimeout)]
        public async Task throwingHandler_TcpTransport_endsTheConnection()
        {
            // GIVEN: A transport with a handler that throws.
            using var server = new TcpServer();
            using var transport = new WebChannelTcpSocketTransport();
            var handlerError = new InvalidOperationException(HandlerFailed);
            transport.OnMessage += (sender, msg) => throw handlerError;
            var error = new TaskCompletionSource<object>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            transport.OnError += (sender, args) =>
                error.TrySetResult(args.ExceptionObject);
            var disconnected = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            transport.OnDisconnected += (sender, args) =>
                disconnected.TrySetResult();
            var accepting = server.Accept();
            transport.Connect(IPAddress.Loopback.ToString(), server.Port,
                              null);
            using var client = await accepting;
            var stream = client.GetStream();

            // WHEN: The server sends a message.
            var cancelled = TestContext.Current.CancellationToken;
            await stream.WriteAsync(Message, cancelled);

            // THEN: The exception and the end are reported, and the
            // server sees the end of the connection.
            Assert.Same(handlerError,
                        await error.Task.WaitAsync(ReportTimeout, cancelled));
            await disconnected.Task.WaitAsync(ReportTimeout, cancelled);
            var read = await stream.ReadAsync(new byte[ReceiveBufferSize],
                                              cancelled)
                .AsTask().WaitAsync(ReportTimeout, cancelled);
            Assert.Equal(0, read);
        }
    }
}
