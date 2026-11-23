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
using MessageType = QWebChannel.QWebChannelMessageTypes;

using static WebChannelTests.TestObjects;

namespace WebChannelTests
{
    public class ConnectionClosedTests
    {
        const int NewCount = 7;
        // A full signature, which the channel checks against the methods of
        // the object
        const string UnknownMethod = "unknownMethod()";

        // An object that the server sends only in a signal, as the
        // description that a new QObject comes with
        const string NewObjectName = "entry";
        static JObject NewObject() => new JObject {
            ["methods"] = new JArray(),
            ["properties"] = new JArray(),
            ["signals"] = new JArray {
                new JArray("destroyed", DestroyedIndex),
            },
            ["enums"] = new JObject(),
        };

        static void OnSignal(object[] args) { }

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
                    ["type"] = (int) MessageType.Response,
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
                    ["type"] = (int) MessageType.PropertyUpdate,
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
         * @scenario A signal with an object that the channel does not know
         *           yet arrives after the connection closed, e.g. from the
         *           buffer of the transport
         * @given A channel whose connection closed
         * @when  The signal arrives with the new object as its argument
         * @then  The channel ignores it without an error, and does not
         *        register the object
         */
        [Fact]
        public void lateSignalWithNewObject_AfterConnectionClosed_isIgnored()
        {
            // GIVEN: A channel whose connection closed.
            var transport = new FakeTransport();
            var channel = Connected(transport);
            channel.ConnectionClosed();

            // WHEN: The signal with a new object arrives.
            var receive = Record.Exception(() => transport.Receive(
                new JObject {
                    ["type"] = (int) MessageType.Signal,
                    ["object"] = ObjectName,
                    ["signal"] = SignalIndex,
                    ["args"] = new JArray {
                        new JObject {
                            ["__QObject*__"] = true,
                            ["id"] = NewObjectName,
                            ["data"] = NewObject(),
                        },
                    },
                }));

            // THEN: No error, and no new object.
            Assert.Null(receive);
            Assert.False(channel.Objects.ContainsKey(NewObjectName));
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
         * @scenario A method is called after the connection closed, which
         *           the object does not have
         * @given A channel whose connection closed
         * @when  A method with a full signature that the object does not
         *        have is called
         * @then  The call reports the method as unknown and fails with
         *        MissingMethodException, as with an open connection
         */
        [Fact(Timeout = TestTimeout)]
        public async Task closedChannel_InvokeUnknownMethod_failsAsUnknown()
        {
            // GIVEN: A channel whose connection closed.
            var transport = new FakeTransport();
            var channel = Connected(transport);
            channel.ConnectionClosed();

            // WHEN: The unknown method is called.
            bool success;
            var call = channel.Objects[ObjectName]
                .InvokeAsync(UnknownMethod, out success);

            // THEN: It is unknown, and the call failed as unknown.
            Assert.False(success);
            await Assert.ThrowsAsync<MissingMethodException>(
                () => call.WaitAsync(TestContext.Current.CancellationToken));
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
                ["type"] = (int) MessageType.Response,
                ["id"] = transport.FailedMessage["id"],
                ["data"] = Answer,
            });

            // THEN: The send failed, and the callback got nothing.
            Assert.IsType<IOException>(send);
            Assert.Empty(answers);
        }

        /**
         * @scenario Sending a property write fails
         * @given A connected channel whose transport fails on the next send
         * @when  A property is written
         * @then  The write throws the error of the transport, and the
         *        property keeps the value that the server has
         */
        [Fact]
        public void failedSend_SetMember_keepsTheValue()
        {
            // GIVEN: A transport that fails on the next send.
            var transport = new FakeTransport();
            var channel = Connected(transport);
            var obj = channel.Objects[ObjectName];
            transport.FailNextSend = true;

            // WHEN: The property is written.
            var write = Record.Exception(
                () => obj.SetMember(PropertyName, NewCount));

            // THEN: It failed, and the property kept its value.
            Assert.IsType<IOException>(write);
            Assert.Equal(Count, Convert.ToInt32(obj.GetMember(PropertyName)));
        }

        /**
         * @scenario The connection closes while a call is sent
         * @given A connected channel whose transport finds the connection
         *        ended on the next send
         * @when  A method is called
         * @then  The call returns its task, which fails with
         *        ConnectionClosedException instead of the error of the
         *        transport
         */
        [Fact(Timeout = TestTimeout)]
        public async Task closeWhileSending_InvokeAsync_failsTheCall()
        {
            // GIVEN: A transport that finds the end on the next send.
            var transport = new FakeTransport();
            var channel = Connected(transport);
            transport.CloseOnNextSend = true;

            // WHEN: A method is called.
            Task<object> call = null;
            var invoke = Record.Exception(() => {
                call = channel.Objects[ObjectName].InvokeAsync(MethodName);
            });

            // THEN: No error at the call, and its task fails with the close.
            Assert.Null(invoke);
            await Assert.ThrowsAsync<ConnectionClosedException>(
                () => call.WaitAsync(TestContext.Current.CancellationToken));
        }

        /**
         * @scenario The connection closes while a property write is sent
         * @given A connected channel whose transport finds the connection
         *        ended on the next send
         * @when  A property is written
         * @then  The write throws ConnectionClosedException instead of the
         *        error of the transport, and the property keeps its value
         */
        [Fact]
        public void closeWhileSending_SetMember_throwsConnectionClosed()
        {
            // GIVEN: A transport that finds the end on the next send.
            var transport = new FakeTransport();
            var channel = Connected(transport);
            var obj = channel.Objects[ObjectName];
            transport.CloseOnNextSend = true;

            // WHEN: The property is written.
            var write = Record.Exception(
                () => obj.SetMember(PropertyName, NewCount));

            // THEN: It failed with the close, and the property kept its
            // value.
            Assert.IsType<ConnectionClosedException>(write);
            Assert.Equal(Count, Convert.ToInt32(obj.GetMember(PropertyName)));
        }

        /**
         * @scenario The transport reports the end of its connection
         * @given A connected channel on a transport that reports its end,
         *        and a method call without an answer
         * @when  The connection of the transport ends
         * @then  The call fails with ConnectionClosedException, without a
         *        call of ConnectionClosed() by the owner
         */
        [Fact(Timeout = TestTimeout)]
        public async Task pendingCall_TransportDisconnects_failsTheCall()
        {
            // GIVEN: A connected channel, and a call that waits.
            var transport = new FakeTransport();
            var channel = Connected(transport);
            var call = channel.Objects[ObjectName].InvokeAsync(MethodName);

            // WHEN: The connection of the transport ends.
            transport.Disconnect();

            // THEN: The call fails.
            await Assert.ThrowsAsync<ConnectionClosedException>(
                () => call.WaitAsync(TestContext.Current.CancellationToken));
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
         * @scenario The change signal of a property is connected after the
         *           connection closed
         * @given A channel whose connection closed
         * @when  A callback is connected to the change signal of a property,
         *        which needs no request to the server
         * @then  The connect throws ConnectionClosedException as for other
         *        signals, as the callback could never be called
         */
        [Fact]
        public void closedChannel_ChangeSignalConnect_throws()
        {
            // GIVEN: A channel whose connection closed.
            var transport = new FakeTransport();
            var channel = Connected(transport);
            var signal = (Signal) channel.Objects[ObjectName]
                .GetMember(PropertyName + "Changed");
            channel.ConnectionClosed();

            // WHEN: A callback is connected.
            var connect = Record.Exception(
                () => signal.Connect((Action<object[]>) OnSignal));

            // THEN: It failed.
            Assert.IsType<ConnectionClosedException>(connect);
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

        /**
         * @scenario The connection closes while a signal is disconnected
         * @given A callback connected to a signal of the server, and a
         *        transport that finds the connection ended on the next send
         * @when  The callback is disconnected
         * @then  The disconnect does not throw, so that cleanup code works
         *        also if the close comes during the disconnect
         */
        [Fact]
        public void closeWhileSending_SignalDisconnect_doesNotThrow()
        {
            // GIVEN: A connected callback, and a transport that finds the
            // end on the next send.
            var transport = new FakeTransport();
            var channel = Connected(transport);
            var signal = (Signal) channel.Objects[ObjectName]
                .GetMember(SignalName);
            Action<object[]> callback = OnSignal;
            signal.Connect(callback);
            transport.CloseOnNextSend = true;

            // WHEN: The callback is disconnected.
            var disconnect = Record.Exception(
                () => signal.Disconnect(callback));

            // THEN: No error.
            Assert.Null(disconnect);
        }
    }
}
