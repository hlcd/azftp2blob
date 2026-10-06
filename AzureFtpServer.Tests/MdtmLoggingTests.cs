using System;
using System.Configuration;
using System.IO;
using System.Linq;
using AzureFtpServer.Ftp;
using FluentAssertions;
using NUnit.Framework;

namespace AzureFtpServer.Tests
{
    /// <summary>
    /// MDTM is sent by some clients for every listed entry, so its "received" line is logged
    /// only when the reply line doesn't identify the request on its own (#46143).
    /// Each test logs in as a different user, so its log entries can be told apart.
    /// </summary>
    public class MdtmLoggingTests
    {
        private static readonly string LogDir = ConfigurationManager.AppSettings["LogPath"];

        private FtpServer _server;

        [OneTimeSetUp]
        public void StartServer()
        {
            if (Directory.Exists(LogDir))
            {
                Directory.Delete(LogDir, true);
            }

            _server = new FtpServer(new FakeFileSystemFactory());
            _server.Start();
        }

        [OneTimeTearDown]
        public void StopServer()
        {
            _server.Stop();
        }

        [Test]
        public void Mdtm_ReplyContainsArgument_OnlyReplyIsLogged()
        {
            using (var client = FtpTestClient.Login("mdtm-directory"))
            {
                client.Send("MDTM 20260920073405_4066004857640")
                    .Should().Be("550 File doesn't exist (/20260920073405_4066004857640)");
            }

            LoggedEntries("mdtm-directory", "MDTM")
                .Should().Equal("550 File doesn't exist (/20260920073405_4066004857640)");
        }

        [Test]
        public void Mdtm_ReplyWithoutArgument_ReceivedIsLoggedBeforeReply()
        {
            using (var client = FtpTestClient.Login("mdtm-file"))
            {
                client.Send("MDTM 9783989551008.xml").Should().StartWith("213 ");
            }

            string[] entries = LoggedEntries("mdtm-file", "MDTM");
            entries.Should().HaveCount(2);
            entries[0].Should().Be("-1 received: 9783989551008.xml");
            entries[1].Should().StartWith("213 ");
        }

        [Test]
        public void Mdtm_ReplyContainsOnlyResolvedPath_ReceivedIsLogged()
        {
            using (var client = FtpTestClient.Login("mdtm-relative"))
            {
                client.Send("MDTM ./missing").Should().Be("550 File doesn't exist (/missing)");
            }

            LoggedEntries("mdtm-relative", "MDTM")
                .Should().Equal("-1 received: ./missing", "550 File doesn't exist (/missing)");
        }

        [Test]
        public void Mdtm_ProcessingFails_ReceivedIsLogged()
        {
            using (var client = FtpTestClient.Login("mdtm-failure"))
            {
                client.Send("MDTM fail.xml").Should().BeNull("connection is closed after unexpected error");
            }

            LoggedEntries("mdtm-failure", "MDTM").Should().Equal("-1 received: fail.xml");
        }

        [Test]
        public void OtherCommand_ReceivedIsLoggedBeforeReply()
        {
            using (var client = FtpTestClient.Login("other-command"))
            {
                client.Send("NOOP").Should().StartWith("200");
            }

            LoggedEntries("other-command", "NOOP").Should().Equal("-1 received: ", "200 ");
        }

        /// <summary>
        /// Command log lines of given user and command, as "reply-code message" (-1 for received line), e.g.
        /// "2026-09-21 10:39:20 78.35.43.98:55620 zebralution 13.95.230.219 MDTM 550 5 File doesn't exist (/x)"
        /// gives "550 File doesn't exist (/x)"
        /// </summary>
        private static string[] LoggedEntries(string user, string command)
        {
            return Directory.GetFiles(LogDir, "ftplog_*.log")
                .SelectMany(ReadLines)
                .Select(line => line.Split(new[] { ' ' }, 9))
                .Where(f => f.Length == 9 && f[3] == user && f[5] == command)
                .Select(f => $"{f[6]} {f[8]}")
                .ToArray();
        }

        private static string[] ReadLines(string file)
        {
            // server may still be writing to the file
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            }
        }
    }
}
