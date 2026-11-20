/*
 * This file is part of WebChannel.NET.
 * Copyright (C) 2026, Menlo Systems GmbH
 */

using System;
using System.IO;

using Newtonsoft.Json.Linq;
using Xunit;

using Channel = QWebChannel.QWebChannel;
using MessageType = QWebChannel.QWebChannelMessageTypes;

using static WebChannelTests.TestObjects;

namespace WebChannelTests
{
    /// <summary>
    /// The tests that redirect Console.Error, which is global to the
    /// process: no other test runs at the same time and writes into it
    /// </summary>
    [CollectionDefinition(DisableParallelization = true)]
    public class ConsoleErrorCollection { }

    [Collection(typeof(ConsoleErrorCollection))]
    public sealed class InvalidMessageTests : IDisposable
    {
        // No request got this id: the channel numbers its requests from
        // 0, and has only sent its init
        const int UnknownId = 1;
        // The start of the line that the channel writes for the response
        const string UnhandledResponse = "Unhandled response";

        readonly TextWriter consoleError = Console.Error;
        readonly StringWriter error = new StringWriter();

        public InvalidMessageTests()
        {
            Console.SetError(error);
        }

        public void Dispose()
        {
            Console.SetError(consoleError);
        }

        /**
         * @scenario The server answers a request that the channel did not
         *           send, e.g. because of a bug of the server
         * @given A connected channel
         * @when  A response with an id that no request got arrives
         * @then  The channel writes it to Console.Error, as other invalid
         *        messages, and does not throw
         */
        [Fact]
        public void responseWithUnknownId_isReported()
        {
            // GIVEN: A connected channel.
            var transport = new FakeTransport();
            var channel = new Channel(transport);
            transport.AnswerInit(new JObject());

            // WHEN: A response with an unknown id arrives.
            var receive = Record.Exception(() => transport.Receive(
                new JObject {
                    ["type"] = (int) MessageType.Response,
                    ["id"] = UnknownId,
                    ["data"] = Answer,
                }));

            // THEN: No error, and the response is reported.
            Assert.Null(receive);
            Assert.StartsWith(UnhandledResponse, error.ToString());
        }
    }
}
