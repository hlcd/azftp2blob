using System;
using System.Configuration;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace AzureFtpServer.Tests
{
    /// <summary>
    /// Minimal ftp client talking to the server started by the tests (control connection only)
    /// </summary>
    internal sealed class FtpTestClient : IDisposable
    {
        private readonly TcpClient _client;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        private FtpTestClient(TcpClient client)
        {
            _client = client;
            _reader = new StreamReader(client.GetStream());
            _writer = new StreamWriter(client.GetStream()) { AutoFlush = true, NewLine = "\r\n" };
            _reader.ReadLine(); // greeting
        }

        public static FtpTestClient Login(string user)
        {
            var client = new FtpTestClient(Connect());
            client.Send($"USER {user}");
            client.Send("PASS secret");
            return client;
        }

        /// <returns>reply line, null when server closed the connection</returns>
        public string Send(string command)
        {
            _writer.WriteLine(command);
            return _reader.ReadLine();
        }

        public void Dispose()
        {
            _client.Close();
        }

        private static TcpClient Connect()
        {
            int port = int.Parse(ConfigurationManager.AppSettings["FTP"]);

            // server starts listening on its own thread, give it a moment
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return new TcpClient("127.0.0.1", port);
                }
                catch (SocketException) when (attempt < 50)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }
}
