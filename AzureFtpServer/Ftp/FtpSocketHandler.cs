using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using AzureFtpServer.Extensions;
using AzureFtpServer.Ftp.FileSystem;
using AzureFtpServer.General;
using AzureFtpServer.Provider;
using AzureFtpServer.Security;

namespace AzureFtpServer.Ftp
{
    /// <summary>
    /// Contains the socket read functionality. Works on its own thread since all socket operation is blocking.
    /// </summary>
    internal class FtpSocketHandler
    {
        #region Member Variables

        private const int m_nBufferSize = 65536;
        private readonly IFileSystemClassFactory m_fileSystemClassFactory;
        private readonly int m_nId;
        private FtpConnectionObject m_theCommands;
        private TcpClient m_theSocket;
        private Thread m_theThread;
        private Thread m_theMonitorThread;
        private static DateTime m_lastActiveTime; // shared between threads
        private int m_maxIdleSeconds;

        #endregion

        #region Events

        #region Delegates

        public delegate void CloseHandler(FtpSocketHandler handler);

        #endregion

        public event CloseHandler Closed;

        #endregion

        #region Construction

        public FtpSocketHandler(IFileSystemClassFactory fileSystemClassFactory, int nId)
        {
            m_nId = nId;
            m_fileSystemClassFactory = fileSystemClassFactory;
        }

        #endregion

        #region Methods

        public void Start(TcpClient socket, System.Text.Encoding encoding, InvalidAttemptCounter invalidLoginCounter)
        {
            m_theSocket = socket;

            m_maxIdleSeconds = StorageProviderConfiguration.MaxIdleSeconds;
            RemoteEndPoint = m_theSocket.GetRemoteAddrSafelly();

            lock (lastActiveLock)
            {
                m_lastActiveTime = FtpServer.CurrentTime;
            }

            m_theCommands = new FtpConnectionObject(m_fileSystemClassFactory, m_nId, socket, invalidLoginCounter);
            m_theCommands.Encoding = encoding;

            m_theThread = new Thread(RunSafelly);
            m_theThread.Start();

            m_theMonitorThread = new Thread(MonitorSafelly);
            m_theMonitorThread.Start();
        }


        public TcpClient Socket => m_theSocket;
        private readonly CancellationTokenSource cancelationSource = new CancellationTokenSource();

        public void Stop()
        {
            cancelationSource.Cancel();

            m_theThread.Join();
            m_theSocket.CloseSafelly();
        }

        internal string RemoteEndPoint { get; private set; }

        private void RunSafelly()
        {
            try
            {
                ThreadRun();
            }
            catch (Exception e)
            {
                FtpServer.LogWrite($"socket handler thread for {RemoteEndPoint} failed: {e}");
            }
            finally
            {
                Closed?.Invoke(this);
            }
        }

        private void MonitorSafelly()
        {
            try
            {
                ThreadMonitor();
            }
            catch (Exception e)
            {
                FtpServer.LogWrite($"monitor thread for {RemoteEndPoint} failed: {e}");
            }
        }

        private readonly object lastActiveLock = new object();

        private void ThreadRun()
        {
            var abData = new byte[m_nBufferSize];

            try
            {
                int nReceived = 0;
                do
                {
                    var t = m_theSocket.GetStream().ReadAsync(abData, 0, m_nBufferSize);
                    t.Wait(cancelationSource.Token);

                    nReceived = t.Result;
                    lock (lastActiveLock)
                    {
                        m_lastActiveTime = FtpServer.CurrentTime;
                    }

                    if (nReceived > 0)
                    {
                        string[] commands = SplitCommands(abData, nReceived);
                        foreach (var command in commands)
                        {
                            m_theCommands.Process(command);
                        }
                    }
                } while (nReceived > 0);
            }
            catch (OperationCanceledException oce)
            {
                if (!cancelationSource.IsCancellationRequested)
                {
                    FtpServer.LogWrite(m_theCommands, $"operation was cancelled: {oce}");   
                }
            }
            catch (UserBlockedException ube)
            {
                FtpServer.LogWrite(ube.Message);
            }
            finally
            {
                FtpServerMessageHandler.SendMessage(m_nId, "Connection closed");
                m_theSocket.CloseSafelly();
                lock (lastActiveLock)
                {
                    //wake up monitor thread
                    //to exit it immediatelly
                    //othewise monitor threads can pile up
                    //when a lot of new connections is made 
                    //which can lead to memory issues
                    exitMonitorThread = true;
                    Monitor.Pulse(lastActiveLock);
                }
            }
        }

        private static readonly string[] NoCommands = new string[0];
        private static readonly char[] LineSeparators = new [] {'\r', '\n'};
        private string[] SplitCommands(byte[] data, int bytesRead)
        {
            if (bytesRead == 0)
            {
                return NoCommands;
            }

            //RFC 959 section 5.4 states that:
            //"The user should
            //wait for this initial primary success or failure response before
            //sending further commands."
            //however a buggy client may send multiple commands before server issues a response
            //we will try to handle this gracefully (though RFC does not require server to do so).
            string sMessage = m_theCommands.Encoding.GetString(data, 0, bytesRead);
            string[] commands = sMessage.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (commands.Length > 1)
            {
                FtpServer.LogWrite(m_theCommands, 
                    $"received multiple commands in one request: {string.Join(Environment.NewLine, commands)}");
            }

            return commands;
        }

        private bool exitMonitorThread;
        private void ThreadMonitor()
        {
            while (m_theThread.IsAlive)
            {
                DateTime currentTime = FtpServer.CurrentTime;
                DateTime lastActivityCopy;
                lock (lastActiveLock)
                {
                    if (exitMonitorThread)
                    {
                        return;
                    }

                    lastActivityCopy = m_lastActiveTime;
                }

                TimeSpan timeSpan = currentTime - m_lastActiveTime;
                // has been idle for a long time
                if ((timeSpan.TotalSeconds > m_maxIdleSeconds) && !m_theCommands.DataSocketOpen) 
                {
                    FtpServer.LogWrite($"closing connection {RemoteEndPoint} because of idle timeout; " +
                                       $"last activity time: {lastActivityCopy}");
                    SocketHelpers.Send(m_theSocket, 
                        $"426 No operations for {m_maxIdleSeconds}+ seconds. Bye!", m_theCommands.Encoding);
                    FtpServerMessageHandler.SendMessage(m_nId, "Connection closed for too long idle time.");
                    Stop();

                    return;
                }

                lock (lastActiveLock)
                {
                    Monitor.Wait(lastActiveLock, TimeSpan.FromSeconds(m_maxIdleSeconds / 2.0));
                }
            }
        }

        #endregion

        #region Properties

        public int Id
        {
            get { return m_nId; }
        }

        #endregion
    }
}