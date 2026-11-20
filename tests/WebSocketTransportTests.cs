/*
 * This file is part of WebChannel.NET.
 * Copyright (C) 2026, Menlo Systems GmbH
 */

using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;

using Xunit;

using QWebChannel;

using static WebChannelTests.TestObjects;

namespace WebChannelTests
{
    public class WebSocketTransportTests
    {
        // How long the server waits for the answer to its close
        static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(2);
        // Enough for a close frame
        const int ReceiveBufferSize = 1024;
        // A message of the server; its content does not matter
        static readonly byte[] Message = Encoding.UTF8.GetBytes("{}");

        /// <summary>
        /// A WebSocket server in the process for one connection, while this
        /// object lives
        /// </summary>
        sealed class WebSocketServer : IDisposable
        {
            readonly HttpListener listener = new HttpListener();

            public WebSocketServer()
            {
                var port = FreePort();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();
                Url = new Uri($"ws://127.0.0.1:{port}/");
            }

            public Uri Url { get; }

            /// <summary>The WebSocket of the next client</summary>
            public async Task<WebSocket> Accept()
            {
                var context = await listener.GetContextAsync();
                var accepted = await context.AcceptWebSocketAsync(null);
                return accepted.WebSocket;
            }

            public void Dispose() => listener.Close();

            // A port on which nothing listens
            static int FreePort()
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var port = ((IPEndPoint) probe.LocalEndpoint).Port;
                probe.Stop();
                return port;
            }
        }

        /**
         * @scenario The server closes the connection
         * @given A transport that receives the messages of a server
         * @when  The server closes the WebSocket
         * @then  The transport answers with its own close frame, so that the
         *        close handshake completes and the server does not wait
         */
        [Fact(Timeout = TestTimeout)]
        public async Task serverClose_WebSocketTransport_answersTheClose()
        {
            // GIVEN: A transport that receives from a server.
            using var server = new WebSocketServer();
            using var transport = new WebChannelWebSocketTransport();
            var accepting = server.Accept();
            await transport.Connect(server.Url);
            var socket = await accepting;
            transport.ProcessMessages();

            // WHEN: The server closes the WebSocket.
            var cancelled = TestContext.Current.CancellationToken;
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,
                                          "", cancelled);

            // THEN: The transport answers with its close frame.
            var answer = await socket.ReceiveAsync(
                new ArraySegment<byte>(new byte[ReceiveBufferSize]),
                cancelled).WaitAsync(AnswerTimeout, cancelled);
            Assert.Equal(WebSocketMessageType.Close, answer.MessageType);
        }

        /**
         * @scenario The WebSocket of the client is aborted while it receives
         * @given A transport that receives the messages of a server
         * @when  The WebSocket of the transport is aborted
         * @then  The transport reports the end of its connection
         */
        [Fact(Timeout = TestTimeout)]
        public async Task abortedSocket_WebSocketTransport_reportsDisconnect()
        {
            // GIVEN: A transport that receives from a server.
            using var server = new WebSocketServer();
            using var transport = new WebChannelWebSocketTransport();
            var disconnected = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            transport.OnDisconnected += (sender, args) =>
                disconnected.TrySetResult();
            var accepting = server.Accept();
            await transport.Connect(server.Url);
            await accepting;
            transport.ProcessMessages();

            // WHEN: The WebSocket of the transport is aborted.
            transport.ClientWebSocket.Abort();

            // THEN: The transport reports the end.
            await disconnected.Task.WaitAsync(ReportTimeout,
                TestContext.Current.CancellationToken);
        }

        /**
         * @scenario The handler of a message throws, e.g. a signal callback
         * @given A transport that receives the messages of a server, with a
         *        message handler that throws
         * @when  The server sends a message
         * @then  The transport reports the exception and the end of its
         *        connection, and closes the WebSocket
         */
        [Fact(Timeout = TestTimeout)]
        public async Task throwingHandler_WebSocketTransport_endsTheConnection()
        {
            // GIVEN: A transport with a handler that throws.
            using var server = new WebSocketServer();
            using var transport = new WebChannelWebSocketTransport();
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
            await transport.Connect(server.Url);
            var socket = await accepting;
            transport.ProcessMessages();

            // WHEN: The server sends a message.
            var cancelled = TestContext.Current.CancellationToken;
            await socket.SendAsync(new ArraySegment<byte>(Message),
                                   WebSocketMessageType.Text, true,
                                   cancelled);

            // THEN: The exception and the end are reported, and the
            // server gets the close.
            Assert.Same(handlerError,
                        await error.Task.WaitAsync(ReportTimeout, cancelled));
            await disconnected.Task.WaitAsync(ReportTimeout, cancelled);
            var close = await socket.ReceiveAsync(
                new ArraySegment<byte>(new byte[ReceiveBufferSize]),
                cancelled).WaitAsync(ReportTimeout, cancelled);
            Assert.Equal(WebSocketMessageType.Close, close.MessageType);
        }
    }
}
