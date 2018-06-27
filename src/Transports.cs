/*
 * This file is part of WebChannel.NET.
 * Copyright (C) 2018, Menlo Systems GmbH
 * License: Dual-licensed under LGPLv3 and GPLv2+
 */

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
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

        public bool Connected { get { return connected; } }
        public TcpClient Client { get { return sock; } }

        public WebChannelTcpSocketTransport(string host, int port, Action<WebChannelTcpSocketTransport> connectedCallback) {
            sock = new TcpClient();
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
            sock.EndConnect(ar);

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
            stream.EndWrite(ar);
            writeLock.Release();
        }

        static bool IsSocketConnected(Socket s)
        {
            return !((s.Poll(1000, SelectMode.SelectRead) && (s.Available == 0)) || !s.Connected);
        }

        void DataAvailable(IAsyncResult ar)
        {
            var stream = sock.GetStream();

            int bytesRead = stream.EndRead(ar);

            if (!IsSocketConnected(sock.Client)) {
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
}
