using System;
using System.Threading;
using System.Threading.Tasks;
using QWebChannel;

namespace chatclient
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.Error.WriteLine("This thread is {0}", Thread.CurrentThread.ManagedThreadId);
            using (var transport = new WebChannelWebSocketTransport()) {
                await transport.Connect(new Uri("ws://localhost:12345"));
                var channel = new QWebChannel.QWebChannel(transport);

                // Run the processing task in the background
                var backgroundProcessingTask = transport.ProcessMessagesAsync();
                backgroundProcessingTask.ContinueWith(t => Console.WriteLine(t.Exception),
                                                      TaskContinuationOptions.OnlyOnFaulted);
                
                await channel.IsConnected;
                Console.WriteLine("Connected.");

                dynamic chatserver = channel.Objects["chatserver"];

                string username = null;

                Func<Task<bool>> tryLogin = async () => {
                    Console.Write("Enter your name: ");
                    username = Console.ReadLine().Trim();
                    return await chatserver.login(username);
                };

                while (!await tryLogin()) {
                    Console.Error.WriteLine("Username already taken. Please enter a new one.");
                }
                Console.WriteLine("Successfully logged in as {0}!", username);

                ((Signal) chatserver.keepAlive).Connect(() => chatserver.keepAliveResponse(username));
                ((Signal) chatserver.newMessage).Connect(delegate (string time, string user, string message) {
                    Console.WriteLine("[{0}] {1}: {2}", time, user, message);
                });

                ((Signal) chatserver.userListChanged).OnEmission += (sender, args) => {
                    Console.WriteLine("User list: {0}", string.Join(", ", chatserver.userList)); 
                };

                while (true) {
                    var msg = await Console.In.ReadLineAsync();
                    await chatserver.sendMessage(username, msg);
                }
            }
        }
    }
}
