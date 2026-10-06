using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AzureFtpServer.Ftp;
using AzureFtpServer.General;

namespace AzureFtpServer.FtpCommands
{
    /// <summary>
    /// Base class for all ftp command handlers.
    /// </summary>
    public class FtpCommandHandler
    {
        #region Member Variables

        private readonly string m_sCommand;
        private readonly FtpConnectionObject m_theConnectionObject;

        // reply code -> count, for commands which are logged as a summary (see FtpServer.LogWrite);
        // handlers are created per connection, so these are numbers for a single connection
        private readonly SortedDictionary<int, int> m_summarizedReplies = new SortedDictionary<int, int>();
        private DateTime m_summaryStart;

        #endregion

        #region Construction

        public FtpCommandHandler(string sCommand, FtpConnectionObject connectionObject)
        {
            m_sCommand = sCommand;
            m_theConnectionObject = connectionObject;
        }

        #endregion

        #region Properties

        public string Command => m_sCommand;

        public FtpConnectionObject ConnectionObject => m_theConnectionObject;

        public virtual bool CanLogCommandArg => true;

        #endregion

        #region Methods

        public void Process(string sMessage)
        {
            if (CanLogCommandArg)
            {
                FtpServer.LogWrite(this, $"received: {sMessage}", -1, 0);
            }
            else
            {
                FtpServer.LogWrite(this, "received", -1, 0);
            }
            var sw = new Stopwatch();
            sw.Start();

            try
            {
                FtpResponse reply = OnProcess(sMessage);
                sw.Stop();
                FtpServer.LogWrite(this, reply.Message, reply.Code, sw.ElapsedMilliseconds);

                SendMessage(reply.ToString());
            }
            catch (FtpCommandException commandEx)
            {
                sw.Stop();
                FtpServer.LogWrite(this, commandEx.MessageToClient, 500, sw.ElapsedMilliseconds);
                //send message to client, then rethrow
                SendMessage(commandEx.MessageToClient);
                throw;
            }
        }

        protected virtual FtpResponse OnProcess(string sMessage)
        {
            Debug.Assert(false, "FtpCommandHandler::OnProcess base called");
            return null;
        }

        internal void CountSummarizedReply(int retCode)
        {
            if (m_summarizedReplies.Count == 0)
            {
                m_summaryStart = FtpServer.CurrentTime;
            }

            m_summarizedReplies.TryGetValue(retCode, out int count);
            m_summarizedReplies[retCode] = count + 1;
        }

        /// <summary>
        /// e.g. "MDTM summary: 734 commands since 07:41:39, replies: 213 x4, 550 x730"
        /// </summary>
        /// <returns>null if nothing was counted</returns>
        internal string GetLogSummary()
        {
            if (m_summarizedReplies.Count == 0)
            {
                return null;
            }

            string replies = string.Join(", ", m_summarizedReplies.Select(r => $"{r.Key} x{r.Value}"));
            return $"{Command} summary: {m_summarizedReplies.Values.Sum()} commands since {m_summaryStart:HH:mm:ss}, " +
                   $"replies: {replies}";
        }

//        protected string GetMessage(int nReturnCode, string sMessage)
//        {
//            return $"{nReturnCode} {sMessage}\r\n";
//        }

        /// <summary>
        /// Get the full path of sPath, will use m_theConnectionObject.CurrentDirectory
        /// </summary>
        /// <param name="sPath">path name, foler or file</param>
        /// <returns></returns>
        protected string GetPath(string sPath)
        {
            string current = m_theConnectionObject.CurrentDirectory;

            if (sPath.Length == 0)
            {
                return current;
            }

            // sPath is an absolute path
            if (sPath[0] == '/')
                return sPath;
            // sPath is a relative path
            // Notice: current's last char is '/'
            else
            {
                if (sPath.StartsWith(@"./")) // support "./", not support "../"
                    return GetPath(sPath.Remove(0, 2));
                return current + sPath;
            }
        }

        /// <summary>
        /// Get the parent directory of CurrentDirectory
        /// </summary>
        /// <returns>null if current is root dir</returns>
        protected string GetParentDir()
        {
            string current = m_theConnectionObject.CurrentDirectory;
            
            // current is root dir
            if (current == @"/")
                return null;

            // remove the last child directory
            int lastDirectoryDelim = current.Remove(current.Length - 1).LastIndexOf('/');
            if (lastDirectoryDelim > -1) // must be true
                current = current.Substring(0, lastDirectoryDelim + 1);
            else
            {
                FtpServerMessageHandler.SendMessage(ConnectionObject.Id, string.Format("Invalid current dir:{0}", current));
                return null;
            }

            return current;
        }

        private void SendMessage(string sMessage)
        {
            if (sMessage.Length == 0)
            {
                return;
            }

            int nEndIndex = sMessage.IndexOf('\r');

            if (nEndIndex < 0)
            {
                FtpServerMessageHandler.SendMessage(m_theConnectionObject.Id, sMessage);
            }
            else
            {
                FtpServerMessageHandler.SendMessage(m_theConnectionObject.Id, sMessage.Substring(0, nEndIndex));
            }

            SocketHelpers.Send(ConnectionObject.Socket, sMessage, ConnectionObject.Encoding);
        }

        #endregion
    }
}