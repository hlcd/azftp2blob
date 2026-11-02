using System;
using System.Runtime.CompilerServices;
using AzureFtpServer.General;

namespace AzureFtpServer.Provider
{
    public enum Modes
    {
        Live,
        Debug
    }

    public class StorageProviderConfiguration
    {
        
        public static string FtpAccount
        {
            get
            {
                return AppSettings.Get("FtpAccount");
            }
        }

        public static Modes Mode
        {
            get
            {
                return (Modes)Enum.Parse(typeof(Provider.Modes), AppSettings.Get("Mode"));
            }
        }

        public static bool QueueNotification
        {
            get
            {
                return bool.Parse(AppSettings.Get("QueueNotification"));
            }
        }

        public static int MaxIdleSeconds
        {
            get
            {
                return int.Parse(AppSettings.Get("MaxIdleSeconds"));
            }
        }

        public static string FtpServerHost
        {
            get
            {
                string buf = AppSettings.Get("FtpServerHost");
                buf = Environment.ExpandEnvironmentVariables(buf);
                return buf;
            }
        }
        public static string FtpServerHostPublic
        {
            get
            {
                string buf = AppSettings.Get("FtpServerHostPublic");
                buf = Environment.ExpandEnvironmentVariables(buf);
                return buf;
            }
        }

        public static bool FtpOverwriteFileOnSTOR => CheckFlag();

        public static bool FtpReplaceSlashOnDELE => CheckFlag();
        public static bool FtpActualDirectoryCreationTime => CheckFlag();

        private static bool CheckFlag([CallerMemberName] string key = null)
        {
            string buf = AppSettings.Get(key);
            return !string.IsNullOrEmpty(buf) && buf.ToLowerInvariant() == "true";
        }
        
    }
}