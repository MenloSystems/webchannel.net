/*
 * This file is part of WebChannel.NET.
 * Copyright (C) 2026, Menlo Systems GmbH
 */

using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading.Tasks;

using Xunit;

using QWebChannel;

namespace WebChannelTests
{
    public class WebSocketTransportTests
    {
        // The longest that a test may take, in ms
        const int TestTimeout = 5000;
        // How long the server waits for the answer to its close
        static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(2);
        // Enough for a close frame
        const int ReceiveBufferSize = 1024;

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
    }
}
