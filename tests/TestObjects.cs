/*
 * This file is part of WebChannel.NET.
 * Copyright (C) 2026, Menlo Systems GmbH
 */

using System;

using Newtonsoft.Json.Linq;

using Channel = QWebChannel.QWebChannel;

namespace WebChannelTests
{
    /// <summary>
    /// The fixture of the tests: one object of the server with a property,
    /// a signal and a method
    /// </summary>
    static class TestObjects
    {
        internal const string ObjectName = "log";
        internal const string PropertyName = "count";
        internal const int Count = 6;
        internal const string SignalName = "newMessage";
        internal const string MethodName = "readLog";
        // The indexes of the members, as the meta object of Qt numbers
        // them: signals and methods share one list, the properties have
        // their own
        internal const int DestroyedIndex = 0;
        internal const int ChangeSignalIndex = 1;
        internal const int SignalIndex = 2;
        internal const int MethodIndex = 3;
        internal const int PropertyIndex = 0;
        // Says that the change signal of the property has the usual name,
        // the name of the property with "Changed"
        internal const int UsualChangeSignalName = 1;
        // What the server answers to a call
        internal const string Answer = "first message";
        // The longest that an async test may take, in ms; a call that waits
        // forever fails the test instead of hanging
        internal const int TestTimeout = 5000;
        // The text of the exception of a message handler that throws
        internal const string HandlerFailed = "The message handler failed";
        // How long a test waits for a transport to report an event
        internal static readonly TimeSpan ReportTimeout =
            TimeSpan.FromSeconds(2);

        // The description of the object, as the server sends it at the init
        internal static JObject Objects() => new JObject {
            [ObjectName] = new JObject {
                ["methods"] = new JArray {
                    new JArray(MethodName, MethodIndex),
                },
                ["properties"] = new JArray {
                    new JArray(PropertyIndex, PropertyName,
                               new JArray(UsualChangeSignalName,
                                          ChangeSignalIndex),
                               Count),
                },
                ["signals"] = new JArray {
                    new JArray("destroyed", DestroyedIndex),
                    new JArray(SignalName, SignalIndex),
                },
                ["enums"] = new JObject(),
            },
        };

        // A channel on transport whose init the server answered
        internal static Channel Connected(FakeTransport transport)
        {
            var channel = new Channel(transport);
            transport.AnswerInit(Objects());
            return channel;
        }
    }
}
