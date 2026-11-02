using System;
using System.Collections.Generic;
using System.Globalization;

namespace AzureFtpServer.Provider
{
    /// <summary>
    /// Creation time of a directory, stored in metadata of its placeholder blob
    /// </summary>
    internal static class BlobExtension
    {
        private const string CreationTimeKey = "CreationTime";
        private const string DateFormatIso8601 = "O";
        public static void SetCreationTime(this IDictionary<string, string> metadata)
        {
            metadata[CreationTimeKey] = DateTimeOffset.UtcNow.ToString(DateFormatIso8601);
        }

        public static DateTimeOffset? GetCreationTime(this IDictionary<string, string> metadata)
        {
            if (metadata.TryGetValue(CreationTimeKey, out string dateInMeta))
            {
                return DateTimeOffset.ParseExact(dateInMeta, DateFormatIso8601, CultureInfo.InvariantCulture);
            }

            return null;
        }
    }
}
