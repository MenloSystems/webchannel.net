/*
 * This file is part of WebChannel.NET.
 * Copyright (C) 2026, Menlo Systems GmbH
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Newtonsoft.Json.Linq;
using Xunit;

using QWebChannel;

using Channel = QWebChannel.QWebChannel;

namespace WebChannelTests
{
    public class ConnectionClosedTests
    {
        // Fixture: one object of the server with a property, a signal and
        // a method
        const string ObjectName = "log";
        const string PropertyName = "count";
        const int Count = 6;
        const int NewCount = 7;
        // The index of the property, and of its change signal
        const int PropertyIndex = 0;
        const int ChangeSignalIndex = 1;
        const string SignalName = "newMessage";
        const string MethodName = "readLog";
        // A full signature, which the channel checks against the methods of
        // the object
        const string UnknownMethod = "unknownMethod()";
        // What the server answers to a call
        const string Answer = "first message";
        // The longest that an async test may take, in ms; a call that waits
        // forever fails the test instead of hanging
        const int TestTimeout = 5000;

        // The description of the object, as the server sends it at the init.
        // Signals and methods share their indexes, as in the meta object of
        // Qt. The change signal of the property has index 1; [1, 1] says
        // that its name is the name of the property with "Changed".
        static JObject Objects() => new JObject {
            [ObjectName] = new JObject {
                ["methods"] = new JArray { new JArray(MethodName, 3) },
                ["properties"] = new JArray {
                    new JArray(0, PropertyName, new JArray(1, 1), Count),
                },
                ["signals"] = new JArray {
                    new JArray("destroyed", 0),
                    new JArray(SignalName, 2),
                },
                ["enums"] = new JObject(),
            },
        };

        static void OnSignal(object[] args) { }

        // A channel on transport whose init the server answered
        static Channel Connected(FakeTransport transport)
        {
            var channel = new Channel(transport);
            transport.AnswerInit(Objects());
            return channel;
        }

        /**
         * @scenario The connection closes while a call waits for its answer
         * @given A connected channel, and a method call without an answer
         * @when  The owner of the transport says that the connection closed
         * @then  The call fails with ConnectionClosedException instead of
         *        waiting forever
         */
        [Fact(Timeout = TestTimeout)]
        public async Task pendingCall_ConnectionClosed_failsTheCall()
        {
            // GIVEN: A connected channel, and a call that waits.
            var transport = new FakeTransport();
            var channel = Connected(transport);
            var call = channel.Objects[ObjectName].InvokeAsync(MethodName);

            // WHEN: The connection closes.
            channel.ConnectionClosed();

            // THEN: The call fails.
            await Assert.ThrowsAsync<ConnectionClosedException>(
                () => call.WaitAsync(TestContext.Current.CancellationToken));
        }

        /**
         * @scenario The answer to a call arrives after the connection closed,
         *           e.g. from the buffer of the transport
         * @given A call that the close failed
         * @when  The answer of the server to the call arrives
         * @then  The channel ignores it, and the call stays failed
         */
        [Fact(Timeout = TestTimeout)]
        public async Task lateAnswer_AfterConnectionClosed_isIgnored()
        {
            // GIVEN: A call that the close failed.
            var transport = new FakeTransport();
            var channel = Connected(transport);
            var call = channel.Objects[ObjectName].InvokeAsync(MethodName);
            var id = transport.SentOfType(MessageType.InvokeMethod)
                .Single()["id"];
            channel.ConnectionClosed();

            // WHEN: The answer arrives.
            var receive = Record.Exception(() => transport.Receive(
                new JObject {
                    ["type"] = MessageType.Response,
                    ["id"] = id,
                    ["data"] = Answer,
                }));

            // THEN: No error, and the call failed with the close.
            Assert.Null(receive);
            await Assert.ThrowsAsync<ConnectionClosedException>(
                () => call.WaitAsync(TestContext.Current.CancellationToken));
        }

        /**
         * @scenario A property update arrives after the connection closed,
         *           e.g. from the buffer of the transport
         * @given A channel whose connection closed
         * @when  An update of a property arrives
         * @then  The channel handles it without an error, and sends no idle
         *        on the closed connection
         */
        [Fact]
        public void latePropertyUpdate_AfterConnectionClosed_sendsNothing()
        {
            // GIVEN: A channel whose connection closed.
            var transport = new FakeTransport();
            var channel = Connected(transport);
            var idles = transport.SentOfType(MessageType.Idle).Count();
            channel.ConnectionClosed();

            // WHEN: An update of the property arrives.
            var receive = Record.Exception(() => transport.Receive(
                new JObject {
                    ["type"] = MessageType.PropertyUpdate,
                    ["data"] = new JArray {
                        new JObject {
                            ["object"] = ObjectName,
                            ["signals"] = new JObject {
                                [$"{ChangeSignalIndex}"] = new JArray(),
                            },
                            ["properties"] = new JObject {
                                [$"{PropertyIndex}"] = NewCount,
                            },
                        },
                    },
                }));

            // THEN: No error, and no further idle.
            Assert.Null(receive);
            Assert.Equal(idles, transport.SentOfType(MessageType.Idle).Count());
        }

        /**
         * @scenario A method is called through dynamic after the connection
         *           closed, the usual way in C#
         * @given A channel whose connection closed
         * @when  A method of an object is called through dynamic
         * @then  The call fails with ConnectionClosedException, not with an
         *        error that the method does not exist
         */
        [Fact(Timeout = TestTimeout)]
        public async Task closedChannel_DynamicCall_failsWithConnectionClosed()
        {
            // GIVEN: A channel whose connection closed.
            var transport = new FakeTransport();
            var channel = Connected(transport);
            dynamic obj = channel.Objects[ObjectName];
            channel.ConnectionClosed();

            // WHEN: The method MethodName is called through dynamic.
            Task call = obj.readLog();

            // THEN: The call fails with the close.
            await Assert.ThrowsAsync<ConnectionClosedException>(
                () => call.WaitAsync(TestContext.Current.CancellationToken));
        }

        /**
         * @scenario A method is called that the object does not have
         * @given A connected channel
         * @when  A method with a full signature that the object does not
         *        have is called, and the connection closes later
         * @then  The call fails at once with MissingMethodException, which
         *        the close does not replace, and nothing is sent
         */
        [Fact(Timeout = TestTimeout)]
        public async Task unknownMethod_InvokeAsync_failsAtOnce()
        {
            // GIVEN: A connected channel.
            var transport = new FakeTransport();
            var channel = Connected(transport);

            // WHEN: The unknown method is called, and the connection closes.
            var call = channel.Objects[ObjectName].InvokeAsync(UnknownMethod);
            channel.ConnectionClosed();

            // THEN: The call failed as unknown, and sent nothing.
            await Assert.ThrowsAsync<MissingMethodException>(
                () => call.WaitAsync(TestContext.Current.CancellationToken));
            Assert.Empty(transport.SentOfType(MessageType.InvokeMethod));
        }

        /**
         * @scenario Sending a call fails, but an answer arrives anyway
         * @given A call with a callback whose send failed
         * @when  An answer to the id of the call arrives
         * @then  The callback is not called, as the call failed
         */
        [Fact]
        public void failedSend_Invoke_dropsTheCallback()
        {
            // GIVEN: A call whose send failed.
            var transport = new FakeTransport();
            var channel = Connected(transport);
            var answers = new List<object>();
            transport.FailNextSend = true;
            var send = Record.Exception(
                () => channel.Objects[ObjectName].Invoke(
                    MethodName, (Action<object>) answers.Add));

            // WHEN: An answer to the call arrives.
            transport.Receive(new JObject {
                ["type"] = MessageType.Response,
                ["id"] = transport.FailedMessage["id"],
                ["data"] = Answer,
            });

            // THEN: The send failed, and the callback got nothing.
            Assert.IsType<IOException>(send);
            Assert.Empty(answers);
        }

        /**
         * @scenario A method is called after the connection closed
         * @given A channel whose connection closed
         * @when  A method is called
         * @then  The call fails with ConnectionClosedException, and nothing
         *        is sent
         */
        [Fact(Timeout = TestTimeout)]
        public async Task closedChannel_InvokeAsync_failsAndSendsNothing()
        {
            // GIVEN: A channel whose connection closed.
            var transport = new FakeTransport();
            var channel = Connected(transport);
            channel.ConnectionClosed();

            // WHEN: A method is called.
            var call = channel.Objects[ObjectName].InvokeAsync(MethodName);

            // THEN: The call fails, and no call was sent.
            await Assert.ThrowsAsync<ConnectionClosedException>(
                () => call.WaitAsync(TestContext.Current.CancellationToken));
            Assert.Empty(transport.SentOfType(MessageType.InvokeMethod));
        }

        /**
         * @scenario The connection closes before the init is answered
         * @given A channel whose init has no answer yet
         * @when  The connection closes
         * @then  Waiting for the connection fails with
         *        ConnectionClosedException instead of waiting forever
         */
        [Fact(Timeout = TestTimeout)]
        public async Task unansweredInit_ConnectionClosed_failsIsConnected()
        {
            // GIVEN: A channel without an answer to its init.
            var channel = new Channel(new FakeTransport());

            // WHEN: The connection closes.
            channel.ConnectionClosed();

            // THEN: Waiting for the connection fails.
            await Assert.ThrowsAsync<ConnectionClosedException>(
                () => channel.IsConnected.WaitAsync(
                    TestContext.Current.CancellationToken));
        }

        /**
         * @scenario A property is written after the connection closed
         * @given A channel whose connection closed
         * @when  A property is written
         * @then  The write throws ConnectionClosedException, nothing is sent,
         *        and the property keeps its value
         */
        [Fact]
        public void closedChannel_SetMember_throwsAndKeepsTheValue()
        {
            // GIVEN: A channel whose connection closed.
            var transport = new FakeTransport();
            var channel = Connected(transport);
            var obj = channel.Objects[ObjectName];
            channel.ConnectionClosed();

            // WHEN: The property is written.
            var write = Record.Exception(
                () => obj.SetMember(PropertyName, NewCount));

            // THEN: It failed, sent nothing and kept the value.
            Assert.IsType<ConnectionClosedException>(write);
            Assert.Empty(transport.SentOfType(MessageType.SetProperty));
            Assert.Equal(Count, Convert.ToInt32(obj.GetMember(PropertyName)));
        }

        /**
         * @scenario A signal is connected after the connection closed
         * @given A channel whose connection closed
         * @when  A callback is connected to a signal of the server
         * @then  The connect throws ConnectionClosedException, and nothing
         *        is sent
         */
        [Fact]
        public void closedChannel_SignalConnect_throwsAndSendsNothing()
        {
            // GIVEN: A channel whose connection closed.
            var transport = new FakeTransport();
            var channel = Connected(transport);
            var signal = (Signal) channel.Objects[ObjectName]
                .GetMember(SignalName);
            channel.ConnectionClosed();

            // WHEN: A callback is connected.
            var connect = Record.Exception(
                () => signal.Connect((Action<object[]>) OnSignal));

            // THEN: It failed and sent nothing.
            Assert.IsType<ConnectionClosedException>(connect);
            Assert.Empty(transport.SentOfType(MessageType.ConnectToSignal));
        }

        /**
         * @scenario A signal is disconnected after the connection closed,
         *           e.g. in a finally block
         * @given A callback connected to a signal of the server, and a
         *        connection that closed since
         * @when  The callback is disconnected
         * @then  The disconnect neither throws nor sends anything, so that
         *        cleanup code works after the close
         */
        [Fact]
        public void closedChannel_SignalDisconnect_sendsNothing()
        {
            // GIVEN: A connected callback, and a connection that closed.
            var transport = new FakeTransport();
            var channel = Connected(transport);
            var signal = (Signal) channel.Objects[ObjectName]
                .GetMember(SignalName);
            Action<object[]> callback = OnSignal;
            signal.Connect(callback);
            channel.ConnectionClosed();

            // WHEN: The callback is disconnected.
            var disconnect = Record.Exception(
                () => signal.Disconnect(callback));

            // THEN: No error, and nothing sent.
            Assert.Null(disconnect);
            Assert.Empty(transport.SentOfType(
                MessageType.DisconnectFromSignal));
        }
    }
}
