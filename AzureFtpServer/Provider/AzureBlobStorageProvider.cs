using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using AzureFtpServer.Ftp.General;
using AzureFtpServer.Azure;
using AzureFtpServer.General;


namespace AzureFtpServer.Provider {

    public class StorageProviderEventArgs : EventArgs {
        public StorageOperation Operation;
        public StorageOperationResult Result;
    }

    public sealed class AzureBlobStorageProvider
    {
        #region Member variables

        private BlobContainerClient _container;

        #endregion

        #region Construction

        public AzureBlobStorageProvider(string containerName)
        {
            Initialise(containerName);
        }

        #endregion

        #region Properties

        public String ContainerName { private get; set; }

        #endregion

        private static BlobClientOptions ClientOptions { get; } = CreateClientOptions();

        private static BlobClientOptions CreateClientOptions()
        {
            var options = new BlobClientOptions
            {
                // directory placeholders are blobs with names ending with '/', don't let the SDK trim it
                TrimBlobNameSlashes = false
            };
            options.Retry.Mode = RetryMode.Exponential;
            options.Retry.Delay = TimeSpan.FromSeconds(2);
            options.Retry.MaxRetries = 3;
            return options;
        }

        // Initialiser method
        private void Initialise(string containerName)
        {
            if (String.IsNullOrEmpty(containerName))
            {
                throw new ArgumentException("You must provide the base Container Name", nameof(containerName));
            }

            ContainerName = containerName;

            string connString = StorageProviderConfiguration.Mode == Modes.Debug
                ? "UseDevelopmentStorage=true"
                : AppSettings.Get("StorageAccount");
            _container = new BlobServiceClient(connString, ClientOptions).GetBlobContainerClient(ContainerName);
            try
            {
                _container.GetProperties();
            }
            catch (RequestFailedException)
            {
                Trace.WriteLine($"Create new container: {ContainerName}", "Information");
                // the container is private, so that it can't be accessed anonymously
                _container.CreateIfNotExists(PublicAccessType.None);
            }
        }

        #region Storage operations

        public Stream GetWriteBlobStream(string path)
        {
            return new BlobUploadStream(GetCloudBlob(path));
        }

        public Stream GetReadBlobStream(string path)
        {
            return GetCloudBlob(path).OpenRead();
        }

        private BlockBlobClient GetCloudBlob(string path)
        {
            // convert to azure path
            string blobPath = path.ToAzurePath();

            // Create a reference for the filename
            // Note: won't check whether the blob exists
            return _container.GetBlockBlobClient(blobPath);
        }

        /// <summary>
        /// Delete the specified file from the Azure container.
        /// </summary>
        /// <param name="path">the file to be deleted</param>
        public bool DeleteFile(string path)
        {
            if (!IsValidFile(path))
                return false;

            // convert to azure path
            string blobPath = path.ToAzurePath();

            _container.GetBlobClient(blobPath).Delete();
            return true;
        }

        /// <summary>
        /// Delete the specified directory from the Azure container.
        /// </summary>
        /// <param name="path">the directory path</param>
        public bool DeleteDirectory(string path)
        {
            if (!IsValidDirectory(path))
            {
                return false;
            }

            // cannot delete root directory
            if (path == "/")
            {
                return false;
            }

            path = path.TrimStart('/');
            foreach (BlobHierarchyItem item in ListDirectory(path))
            {
                if (item.IsBlob && item.Blob.Properties.BlobType == BlobType.Block)
                {
                    _container.GetBlobClient(item.Blob.Name).Delete();
                }

                if (item.IsPrefix)
                {
                    if (!DeleteDirectory(item.Prefix))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Retrieves the object from the storage provider
        /// </summary>
        /// <param name="path">the file/dir path in the FtpServer</param>
        /// <param name="isDirectory">whether path is a directory</param>
        /// <returns>AzureCloudFile, null if not found</returns>
        public AzureCloudFile GetBlobInfo(string path, bool isDirectory)
        {
            // check parameter
            if (path == null || path == "" || path[0] != '/')
                return null;

            // get the info of root directory
            if ((path == "/") && isDirectory)
                return new AzureCloudFile
                            {
                                Uri = _container.Uri,
                                FtpPath = path,
                                IsDirectory = true,
                                Size = 1,
                                LastModified = DateTime.Now
                            };

            // convert to azure path
            string blobPath = path.ToAzurePath();

            try
            {
                if (isDirectory)
                {
                    string prefix = DirectoryPrefix(blobPath);
                    // check whether directory exists
                    if (!ListDirectory(prefix).Any())
                    {
                        Trace.WriteLine(string.Format("Get blob {0} failed", path), "Error");
                        return null;
                    }
                    return new AzureCloudFile
                            {
                                Uri = _container.GetBlobClient(prefix).Uri,
                                FtpPath = path,
                                IsDirectory = true,
                                // default value for size and modify time of directories
                                Size = 1,
                                LastModified = DateTime.Now
                            };
                }

                BlobClient b = _container.GetBlobClient(blobPath);
                BlobProperties properties = b.GetProperties();
                return new AzureCloudFile
                        {
                            Uri = b.Uri,
                            LastModified = properties.LastModified.DateTime,
                            Size = properties.ContentLength,
                            FtpPath = path,
                            IsDirectory = false
                        };
            }
            catch (RequestFailedException)
            {
                Trace.WriteLine(string.Format("Get blob {0} failed", path),"Error");
                return null;
            }
        }

        /// <summary>
        /// Gets the directory under the directory
        /// </summary>
        /// <param name="dirPath">directory path</param>
        /// <returns>blob prefixes of the directories, e.g. "dir/sub/"</returns>
        public IEnumerable<string> GetDirectoryListing(string dirPath)
        {
            return ListDirectory(dirPath.TrimStart('/'))
                .Where(i => i.IsPrefix)
                .Select(i => i.Prefix);
        }

        /// <summary>
        /// List the files under the directory
        /// </summary>
        /// <param name="dirPath">directory path</param>
        /// <returns></returns>
        public IEnumerable<BlobItem> GetFileListing(string dirPath)
        {
            return ListDirectory(dirPath.TrimStart('/'))
                .Where(i => i.IsBlob && i.Blob.Properties.BlobType == BlobType.Block)
                .Select(i => i.Blob)
                .Where(b => !b.Name.EndsWith("/"));//filter out virtual folder files;
        }

        /// <summary>
        /// Creation time stored in the directory placeholder blob
        /// </summary>
        /// <param name="prefix">blob prefix of the directory, e.g. "dir/sub/"</param>
        /// <returns>current time when there is no placeholder or creation time in it</returns>
        public DateTime GetDirectoryCreationTime(string prefix)
        {
            try
            {
                BlobProperties properties = _container.GetBlobClient(prefix).GetProperties();
                return (properties.Metadata.GetCreationTime() ?? DateTimeOffset.UtcNow).UtcDateTime;
            }
            catch (RequestFailedException)
            {
                return DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Renames the specified object by copying the original to a new path and deleting the original.
        /// </summary>
        /// <param name="originalPath">The original path.</param>
        /// <param name="newPath">The new path.</param>
        /// <returns></returns>
        public StorageOperationResult Rename(string originalPath, string newPath)
        {
            BlobClient newBlob = _container.GetBlobClient(newPath.ToAzurePath());
            BlobClient originalBlob = _container.GetBlobClient(originalPath.ToAzurePath());

            // Check if the original path exists on the provider.
            if (!IsValidFile(originalPath))
            {
                throw new FileNotFoundException("The path supplied does not exist on the storage provider.",
                                                originalPath);
            }

            // the original can be deleted only once the copy is complete
            newBlob.StartCopyFromUri(originalBlob.Uri).WaitForCompletion();
            originalBlob.Delete();
            return StorageOperationResult.Completed;
        }

        public bool CreateDirectory(string path)
        {
            path = path.ToAzurePath();

            try
            {
                var metadata = new Dictionary<string, string>();
                metadata.SetCreationTime();
                _container.GetBlockBlobClient(path)
                    .Upload(new MemoryStream(Array.Empty<byte>()), new BlobUploadOptions { Metadata = metadata });
            }
            catch (Exception)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Determines whether the specified path is a valid blob folder.
        /// </summary>
        /// <param name="dirPath">a directory path, final char is '/'</param>
        /// <returns></returns>
        public bool IsValidDirectory(string dirPath)
        {
            if (dirPath == null)
                return false;

            // Important, when dirPath = "/", the behind HasDirectory(dirPath) will throw exceptions
            if (dirPath == "/")
                return true;

            // error check
            if (!dirPath.EndsWith(@"/"))
            {
                Trace.WriteLine(string.Format("Invalid parameter {0} for function IsValidDirectory", dirPath), "Error");
                return false;
            }

            // remove the first '/' char
            string blobDirPath = dirPath.ToAzurePath();

            // non-exist blobDirectory won't contain blobs
            return ListDirectory(blobDirPath).Any();
        }

        /// <summary>
        /// Checks whether the specified path is a valid .
        /// </summary>
        /// <param name="path">The path.</param>
        /// <returns></returns>
        public bool IsValidFile(string filePath)
        {
            if (filePath == null)
                return false;

            // error check
            if (filePath.EndsWith(@"/"))
            {
                Trace.WriteLine(string.Format("Invalid parameter {0} for function IsValidFile", filePath), "Error");
                return false;
            }

            // remove the first '/' char
            string fileBlobPath = filePath.ToAzurePath();

            try
            {
                _container.GetBlobClient(fileBlobPath).GetProperties();
            }
            catch (RequestFailedException)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// read bytes from the stream and append the content to an existed file
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="stream"></param>
        /// <returns></returns>
        public bool AppendFileFromStream(string filePath, Stream stream)
        {
            filePath = filePath.ToAzurePath();

            BlockBlobClient blob = _container.GetBlockBlobClient(filePath);

            // store the block id of this blob
            var blockList = new List<string>();

            try
            {
                foreach (BlobBlock block in blob.GetBlockList(BlockListTypes.Committed).Value.CommittedBlocks)
                {
                    blockList.Add(block.Name);
                }
            }
            catch (RequestFailedException)
            {
                // do nothing, this may happen when blob doesn't exist
            }

            const int blockSize = 4 * 1024 * 1024; // 4M - block size
            byte[] buffer = new byte[blockSize];
            // append file
            try
            {
                int nRead = 0;
                while (nRead < blockSize)
                {
                    int actualRead = stream.Read(buffer, nRead, blockSize - nRead);
                    if (actualRead <= 0) // stream end
                    {
                        //put last block & break
                        string strBlockId = GetBlockID(blockList);
                        blob.StageBlock(strBlockId, new MemoryStream(buffer, 0, nRead));
                        blockList.Add(strBlockId);
                        break;
                    }
                    else if (actualRead == (blockSize - nRead))// buffer full
                    {
                        //put this block
                        string strBlockId = GetBlockID(blockList);
                        blob.StageBlock(strBlockId, new MemoryStream(buffer));
                        blockList.Add(strBlockId);
                        nRead = 0;
                        continue;
                    }
                    nRead += actualRead;
                }
            }
            catch (RequestFailedException)
            {
                // blob.StageBlock error
                return false;
            }

            // put block list
            blob.CommitBlockList(blockList);

            return true;
        }

        /// <summary>
        /// After successfully upload a new file, user Azure queue to record it
        /// </summary>
        /// <param name="filePath">the path of the new file</param>
        public void UploadNotification(string filePath)
        {
            if (!StorageProviderConfiguration.QueueNotification)
                return;
            // queue notification has never been implemented
        }

        #endregion

        #region "Helper methods"

        /// <summary>
        /// Hierarchical listing (directories and blobs) of a blob directory
        /// </summary>
        /// <param name="prefix">blob prefix of the directory, e.g. "dir/sub/", "" for the root</param>
        private Pageable<BlobHierarchyItem> ListDirectory(string prefix)
        {
            return _container.GetBlobsByHierarchy(new GetBlobsByHierarchyOptions { Delimiter = "/", Prefix = prefix });
        }

        // blob prefix of a directory always ends with '/'
        private static string DirectoryPrefix(string blobPath)
        {
            return blobPath.EndsWith("/") ? blobPath : blobPath + "/";
        }

        private string GetBlockID(List<string> currentIds)
        {
            string blockID = null;

            while (true)
            {
                string tempStr = Convert.ToBase64String(Encoding.ASCII.GetBytes(DateTime.Now.ToBinary().ToString()));
                int idLength = (currentIds.Count() == 0) ? 64 : currentIds[0].Length;
                tempStr = TextHelpers.RightAlignString(tempStr, idLength, 'A');
                bool sameId = false;
                foreach (var id in currentIds)
                {
                    if (id == tempStr)
                    {
                        sameId = true;
                        break;
                    }
                }
                if (!sameId)
                {
                    blockID = tempStr;
                    break;
                }
            }

            return blockID;
        }

        #endregion

        #region "Callbacks"


        #endregion
    }
}
