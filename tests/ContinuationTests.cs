/*
 * This file is part of WebChannel.NET.
 * Copyright (C) 2026, Menlo Systems GmbH
 */

using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json.Linq;
using Xunit;

using Channel = QWebChannel.QWebChannel;
using MessageType = QWebChannel.QWebChannelMessageTypes;

using static WebChannelTests.TestObjects;

namespace WebChannelTests
{
    /// <summary>
    /// The code after an await of the channel must not run on the thread
    /// that receives, while it holds the lock of the channel: code that
    /// waits there for another answer would block its own receive
    /// </summary>
    /// The tests continue synchronously where the task allows it, as await
    /// does without a SynchronizationContext.
    public class ContinuationTests
    {
        // Whether the code that continues after task holds the lock of
        // channel
        static Task<bool> ContinuesWithTheLock(Task task, Channel channel) =>
            task.ContinueWith(
                _ => Monitor.IsEntered(channel.lockObject),
                TaskContinuationOptions.ExecuteSynchronously);

        /**
         * @scenario The answer to a call arrives
         * @given A connected channel, and code that continues after the
         *        call
         * @when  The answer of the server arrives
         * @then  The code continues without the lock of the channel
         */
        [Fact(Timeout = TestTimeout)]
        public async Task answeredCall_ContinuesWithoutTheChannelLock()
        {
            // GIVEN: Code that continues after a call.
            var transport = new FakeTransport();
            var channel = Connected(transport);
            var call = channel.Objects[ObjectName].InvokeAsync(MethodName);
            var withTheLock = ContinuesWithTheLock(call, channel);

            // WHEN: The answer arrives.
            transport.Receive(new JObject {
                ["type"] = (int) MessageType.Response,
                ["id"] = transport.SentOfType(MessageType.InvokeMethod)
                    .Single()["id"],
                ["data"] = Answer,
            });

            // THEN: The code continued without the lock.
            Assert.False(await withTheLock.WaitAsync(
                TestContext.Current.CancellationToken));
        }

        /**
         * @scenario The answer to the init arrives
         * @given A channel without an answer to its init, and code that
         *        continues after the connect
         * @when  The answer of the server to the init arrives
         * @then  The code continues without the lock of the channel
         */
        [Fact(Timeout = TestTimeout)]
        public async Task answeredInit_ContinuesWithoutTheChannelLock()
        {
            // GIVEN: Code that continues after the connect.
            var transport = new FakeTransport();
            var channel = new Channel(transport);
            var withTheLock = ContinuesWithTheLock(
                channel.IsConnected, channel);

            // WHEN: The answer to the init arrives.
            transport.AnswerInit(Objects());

            // THEN: The code continued without the lock.
            Assert.False(await withTheLock.WaitAsync(
                TestContext.Current.CancellationToken));
        }
    }
}
