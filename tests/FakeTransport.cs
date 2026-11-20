/*
 * This file is part of WebChannel.NET.
 * Copyright (C) 2026, Menlo Systems GmbH
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using QWebChannel;

using MessageType = QWebChannel.QWebChannelMessageTypes;

namespace WebChannelTests
{
    /// <summary>
    /// A transport in the process: it keeps what the channel sends, and
    /// hands the channel what a test lets the server answer
    /// </summary>
    sealed class FakeTransport : IWebChannelClosingTransport
    {
        readonly List<JObject> sent = new List<JObject>();

        public event Action<object, byte[]> OnMessage;
        public event EventHandler OnDisconnected;

        /// <summary>Ends the connection, as the server does</summary>
        public void Disconnect()
        {
            OnDisconnected?.Invoke(this, EventArgs.Empty);
        }

        public void Send(byte[] msg)
        {
            var message = JObject.Parse(Encoding.UTF8.GetString(msg));
            if (CloseOnNextSend) {
                CloseOnNextSend = false;
                Disconnect();
                throw new IOException("The connection closed");
            }
            if (FailNextSend) {
                FailNextSend = false;
                FailedMessage = message;
                throw new IOException("Sending failed");
            }
            sent.Add(message);
        }

        /// <summary>
        /// Whether the next send throws IOException, as a broken transport
        /// does
        /// </summary>
        public bool FailNextSend { get; set; }

        /// <summary>
        /// Whether the next send finds the connection ended: it reports the
        /// end first and then throws IOException, as a transport does that
        /// notices the end while sending
        /// </summary>
        public bool CloseOnNextSend { get; set; }

        /// <summary>The message whose send failed</summary>
        public JObject FailedMessage { get; private set; }

        /// <summary>The messages that the channel sent, in order</summary>
        public IReadOnlyList<JObject> Sent => sent;

        /// <summary>The sent messages of type type</summary>
        public IEnumerable<JObject> SentOfType(MessageType type) =>
            sent.Where(message => (int) message["type"] == (int) type);

        /// <summary>Hands the channel a message of the server</summary>
        public void Receive(JObject message)
        {
            OnMessage?.Invoke(this, Encoding.UTF8.GetBytes(
                message.ToString(Formatting.None)));
        }

        /// <summary>
        /// Answers the init of the channel with objects, which map the name
        /// of each object to its description
        /// </summary>
        public void AnswerInit(JObject objects)
        {
            var init = SentOfType(MessageType.Init).Single();
            Receive(new JObject {
                ["type"] = (int) MessageType.Response,
                ["id"] = init["id"],
                ["data"] = objects,
            });
        }
    }
}
