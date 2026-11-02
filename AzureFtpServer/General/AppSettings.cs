using System;
using Microsoft.Extensions.Configuration;

namespace AzureFtpServer.General
{
    /// <summary>
    /// Settings from appsettings.json next to the application
    /// (same keys as appSettings in the former azftp2blob.exe.config)
    /// </summary>
    public static class AppSettings
    {
        private static readonly Lazy<IConfiguration> Configuration = new Lazy<IConfiguration>(() =>
            new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .Build());

        /// <returns>null when the key is missing</returns>
        public static string Get(string key) => Configuration.Value[key];
    }
}
