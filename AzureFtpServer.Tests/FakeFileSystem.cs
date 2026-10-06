using System;
using System.IO;
using AzureFtpServer.Ftp.FileSystem;

namespace AzureFtpServer.Tests
{
    /// <summary>
    /// Accepts any login. Only *.xml files exist, there are no directories,
    /// reading info of a file with "fail" in its name throws.
    /// </summary>
    internal class FakeFileSystemFactory : IFileSystemClassFactory
    {
        public IFileSystem Create(string sUser, string sPassword) => new FakeFileSystem();
    }

    internal class FakeFileSystem : IFileSystem
    {
        public bool FileExists(string sPath) => sPath.EndsWith(".xml");

        public IFileInfo GetFileInfo(string sPath)
        {
            if (sPath.Contains("fail"))
            {
                throw new InvalidOperationException("storage is not available");
            }

            return new FakeFileInfo(sPath);
        }

        public bool DirectoryExists(string sDirPath) => false;

        public IFile OpenFile(string sPath, bool fWrite) => throw new NotSupportedException();
        public IFileInfo GetDirectoryInfo(string sPath) => throw new NotSupportedException();
        public IFileInfo[] GetFiles(string sDirPath) => throw new NotSupportedException();
        public IFileInfo[] GetDirectories(string sDirPath, bool actualCreationTime) => throw new NotSupportedException();
        public bool CreateDirectory(string sPath) => throw new NotSupportedException();
        public bool Move(string sOldPath, string sNewPath) => throw new NotSupportedException();
        public bool DeleteFile(string sPath) => throw new NotSupportedException();
        public bool DeleteDirectory(string sPath) => throw new NotSupportedException();
        public bool AppendFile(string sPath, Stream stream) => throw new NotSupportedException();
        public void Log4Upload(string sPath) { }
    }

    internal class FakeFileInfo : IFileInfo
    {
        private readonly string _path;

        public FakeFileInfo(string path)
        {
            _path = path;
        }

        public DateTime GetModifiedTime() => new DateTime(2026, 9, 21, 8, 39, 21);
        public long GetSize() => 1;
        public string GetAttributeString() => "-rw-r--r--";
        public bool IsDirectory() => false;
        public string Path() => _path;
        public bool FileObjectExists() => true;
    }
}
