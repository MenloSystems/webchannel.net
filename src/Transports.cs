/*
 * This file is part of WebChannel.NET.
 * Copyright (C) 2018, Menlo Systems GmbH
 * License: Dual-licensed under LGPLv3 and GPLv2+
 */

using System;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections;

namespace QWebChannel
{
    public interface IWebChannelTransport
    {
        void Send(byte[] msg);
        event Action<object, byte[]> OnMessage;
    }

    public class DummyTransport : IWebChannelTransport
    {
        public void Send(byte[] msg)
        {
            Console.WriteLine("Would now send: " + Encoding.UTF8.GetString(msg));
        }

        // Disable "Not used" warning
        #pragma warning disable 67
        public event Action<object, byte[]> OnMessage;
        #pragma warning restore 67
    }

    public class WebChannelTcpSocketTransport : IWebChannelTransport, IDisposable
    {
        TcpClient sock;
        Semaphore writeLock = new Semaphore(1, 1);

        byte[] receiveBuffer = new byte[4096];
        byte[] buffer = new byte[0];

        bool connected = false;

        public event Action<object, byte[]> OnMessage;
        public event EventHandler OnDisconnected;
        public event EventHandler<UnhandledExceptionEventArgs> OnError;

        public bool Connected { get { return connected; } }
        public TcpClient Client { get { return sock; } }

        public WebChannelTcpSocketTransport(string host, int port, Action<WebChannelTcpSocketTransport> connectedCallback) {
            sock = new TcpClient();
            Connect(host, port, connectedCallback);
        }

        public WebChannelTcpSocketTransport() {
            sock = new TcpClient();
        }

        public void Connect(string host, int port, Action<WebChannelTcpSocketTransport> connectedCallback)
        {
            sock.BeginConnect(host, port, new AsyncCallback(ClientConnected), connectedCallback);
        }

        public void Dispose()
        {
            Disconnect();
            if (sock != null) {
                ((IDisposable) sock).Dispose();
            }
        }

        public void Disconnect()
        {
             if (sock != null && sock.Client != null) {
                sock.Client.Disconnect(false);
             }
        }

        void ClientConnected(IAsyncResult ar)
        {
            var callback = (Action<WebChannelTcpSocketTransport>) ar.AsyncState;
            try {
                sock.EndConnect(ar);
            } catch (Exception e) {
                Console.Error.WriteLine("An error occured while attempting to connect!");
                if (OnError != null) {
                    var args = new UnhandledExceptionEventArgs(e, false);
                    OnError(this, args);
                }
                return;
            }

            sock.GetStream().BeginRead(receiveBuffer, 0, receiveBuffer.Length, new AsyncCallback(DataAvailable), null);

            connected = true;

            if (callback != null) {
                callback(this);
            }
        }

        public void Send(string msg)
        {
            Send(Encoding.UTF8.GetBytes(msg));
        }

        public void Send(byte[] msg)
        {
            if (msg[msg.Length - 1] != Convert.ToByte('\n')) {
                byte[] newlineTerminatedMsg = new byte[msg.Length + 1];
                Buffer.BlockCopy(msg, 0, newlineTerminatedMsg, 0, msg.Length);
                newlineTerminatedMsg[newlineTerminatedMsg.Length - 1] = Convert.ToByte('\n');
                msg = newlineTerminatedMsg;
            }

            writeLock.WaitOne();
            var stream = sock.GetStream();
            stream.BeginWrite(msg, 0, msg.GetLength(0), new AsyncCallback(DataWritten), stream);
        }

        void DataWritten(IAsyncResult ar)
        {
            var stream = (NetworkStream) ar.AsyncState;
            try {
                stream.EndWrite(ar);
            } catch (Exception e) {
                Console.Error.WriteLine("An error occured while trying to write data!");
                if (OnError != null) {
                    var args = new UnhandledExceptionEventArgs(e, false);
                    OnError(this, args);
                }
            }
            writeLock.Release();
        }

        static bool IsSocketConnected(Socket s)
        {
            return !((s.Poll(1000, SelectMode.SelectRead) && (s.Available == 0)) || !s.Connected);
        }

        void DataAvailable(IAsyncResult ar)
        {
            NetworkStream stream = null;
            int bytesRead = 0;

            try {
                stream = sock.GetStream();
                bytesRead = stream.EndRead(ar);
            } catch (Exception e) {
                Console.Error.WriteLine("An error occured while trying to receive data!");
                if (OnError != null) {
                    var args = new UnhandledExceptionEventArgs(e, false);
                    OnError(this, args);
                }
            }

            bool nowDisconnected = false;
            try {
                if (!IsSocketConnected(sock.Client) || stream == null) {
                    nowDisconnected = true;
                }
            } catch (ObjectDisposedException) {
                nowDisconnected = true;
            }

            if (nowDisconnected) {
                if (connected) {
                    connected = false;
                    if (OnDisconnected != null) {
                        OnDisconnected(this, new EventArgs());
                    }
                }

                return;
            }

            int oldSize = buffer.Length;
            Array.Resize(ref buffer, buffer.Length + bytesRead);
            Buffer.BlockCopy(receiveBuffer, 0, buffer, oldSize, bytesRead);

            TryReadMessages();

            stream.BeginRead(receiveBuffer, 0, receiveBuffer.Length, new AsyncCallback(DataAvailable), null);
        }

        void TryReadMessages()
        {
            int idx;

            while ((idx = Array.IndexOf<byte>(buffer, Convert.ToByte('\n'))) >= 0) {
                int len = idx + 1;

                byte[] message = new byte[len];
                Buffer.BlockCopy(buffer, 0, message, 0, len);
                Buffer.BlockCopy(buffer, len, buffer, 0, buffer.Length - len);
                Array.Resize(ref buffer, buffer.Length - len);

                if (OnMessage != null) {
                    OnMessage(this, message);
                }
            }
        }
    }

    public class WebChannelWebSocketTransport : IWebChannelTransport, IDisposable
    {
        ClientWebSocket sock;

        public event Action<object, byte[]> OnMessage;
        public event EventHandler OnDisconnected;
        public event EventHandler<UnhandledExceptionEventArgs> OnError;

        public bool Connected { get { return sock.State == WebSocketState.Open; } }
        public ClientWebSocket ClientWebSocket { get { return sock; } }

        public WebChannelWebSocketTransport() {
            sock = new ClientWebSocket();
        }

        public async Task Connect(Uri uri)
        {
            await sock.ConnectAsync(uri, CancellationToken.None);
        }

        // https://stackoverflow.com/a/23784968 - because .NET WebSockets don't
        // do message handling.

        private struct WebSocketMessage
        {
            public WebSocketReceiveResult result;
            public byte[] data;
        }
        async Task<WebSocketMessage> ReceiveMessage()
        {
            ArraySegment<Byte> buffer = new ArraySegment<byte>(new Byte[8192]);
            WebSocketMessage msg = new WebSocketMessage();

            using (var ms = new MemoryStream()) {
                do {
                    try {
                        msg.result = await sock.ReceiveAsync(buffer, CancellationToken.None);
                    } catch(WebSocketException e) {
                        if (OnError != null) {
                            OnError(this, new UnhandledExceptionEventArgs(e, false));
                        }
                        await Disconnect(e.ToString());
                        return msg;
                    }
                    if (msg.result.MessageType == WebSocketMessageType.Close) {
                        await Disconnect(msg.result.CloseStatusDescription);
                        return msg;
                    }
                    ms.Write(buffer.Array, buffer.Offset, msg.result.Count);
                }
                while (!msg.result.EndOfMessage);

                ms.Seek(0, SeekOrigin.Begin);

                msg.data = ms.GetBuffer();
                return msg;
            }
        }

        void ProcessMessage(WebSocketMessage msg)
        {
            if (msg.result != null && msg.result.MessageType == WebSocketMessageType.Text) {
                OnMessage(this, msg.data);
            }
        }

        Task<WebSocketMessage> recvMsgTask = Task.FromResult(new WebSocketMessage());

        public void Pump()
        {
            if (recvMsgTask.IsCompleted) {
                ProcessMessage(recvMsgTask.Result);
                recvMsgTask = ReceiveMessage();
            }
        }

        public async Task ProcessNextMessageAsync()
        {
            await recvMsgTask;
            Pump();
        }

        public async Task ProcessMessagesAsync()
        {
            while (Connected) {
                await ProcessNextMessageAsync();
            }
        }

        public void Dispose()
        {
            Disconnect().Wait();
            if (sock != null) {
                ((IDisposable) sock).Dispose();
            }
        }

        public async Task Disconnect()
        {
            await Disconnect("Closed by Disconnect()");
        }

        async Task Disconnect(string reason)
        {
            if (sock != null) {
                if (sock.State == WebSocketState.Open || sock.State == WebSocketState.Connecting) {
                    await sock.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
                }
                if (OnDisconnected != null) {
                    OnDisconnected(this, new EventArgs());
                }
            }
        }


        public void Send(string msg)
        {
            Send(Encoding.UTF8.GetBytes(msg));
        }

        public void Send(byte[] msg)
        {
            sock.SendAsync(new ArraySegment<byte>(msg), WebSocketMessageType.Text, true, CancellationToken.None).Wait();
        }
    }
}
