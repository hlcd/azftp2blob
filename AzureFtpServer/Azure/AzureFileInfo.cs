using System;
using System.Text;
using Azure.Storage.Blobs.Models;
using AzureFtpServer.Ftp.FileSystem;
using AzureFtpServer.Provider;

namespace AzureFtpServer.Azure
{
    public sealed class AzureFileInfo : IFileInfo
    {
        public AzureFileInfo(AzureCloudFile file)
        {
            exists = file != null;
            if (exists)
            {
                path = file.FtpPath;
                lastModified = file.LastModified;
                size = file.Size;
                isDirectory = file.IsDirectory;
            }
        }

        public AzureFileInfo(BlobItem blob)
        {
            exists = blob != null;
            if (exists)
            {
                path = blob.Name;
                isDirectory = false;
                lastModified = blob.Properties.LastModified?.UtcDateTime ?? DateTime.UtcNow;
                size = blob.Properties.ContentLength ?? 0;
            }
        }

        /// <param name="directoryPath">ftp path of a directory, e.g. "/dir/sub/"</param>
        /// <param name="lastModified">creation time of the directory or current time</param>
        public AzureFileInfo(string directoryPath, DateTime lastModified)
        {
            exists = directoryPath != null;
            if (exists)
            {
                path = directoryPath;
                isDirectory = true;
                this.lastModified = lastModified;
                size = 1;
            }
        }

        #region Implementation of IFileInfo 

        private readonly bool exists;
        public bool FileObjectExists()
        {
            return exists;
        }

        private readonly string path;
        public string Path()
        {
            return path;
        }

        private readonly DateTime lastModified;
        public DateTime GetModifiedTime()
        {
            return lastModified;
        }

        private readonly long size;
        public long GetSize()
        {
            return size;
        }

        private readonly bool isDirectory;
        public bool IsDirectory()
        {
            return isDirectory;
        }

        public string GetAttributeString()
        {
            bool fDirectory = IsDirectory();
            bool fReadOnly = false; // No file should be read-only.

            var builder = new StringBuilder();

            builder.Append(fDirectory ? "d" : "-");

            builder.Append("r");

            if (fReadOnly)
            {
                builder.Append("-");
            }
            else
            {
                builder.Append("w");
            }

            builder.Append(fDirectory ? "x" : "-");

            builder.Append(fDirectory ? "r-xr-x" : "r--r--");

            return builder.ToString();
        }

        #endregion
    }
}