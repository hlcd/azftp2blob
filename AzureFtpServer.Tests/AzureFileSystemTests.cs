using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureFtpServer.Azure;
using AzureFtpServer.Ftp.FileSystem;
using FluentAssertions;
using NUnit.Framework;

namespace AzureFtpServer.Tests
{
    /// <summary>
    /// Storage behaviour the ftp commands rely on, checked against Azurite (Mode=Debug uses development storage).
    /// Written before the migration to Azure.Storage.Blobs v12, so the same tests show nothing has changed.
    /// Start Azurite first: npx --package azurite azurite-blob --skipApiVersionCheck --loose
    /// </summary>
    [Category("Azurite")]
    public class AzureFileSystemTests
    {
        private const string DevelopmentStorage = "UseDevelopmentStorage=true";

        private readonly string _containerName = "ftp-tests-" + Guid.NewGuid().ToString("N").Substring(0, 12);

        private IFileSystem _fs;

        // looks at the storage independently of the code under test
        private BlobContainerClient _container;

        [OneTimeSetUp]
        public void CreateFileSystem()
        {
            if (!AzuriteIsRunning())
            {
                Assert.Ignore("Azurite is not running on 127.0.0.1:10000, " +
                              "start it with: npx --package azurite azurite-blob --skipApiVersionCheck --loose");
            }

            _fs = new AzureFileSystem(_containerName);
            // directory placeholders are blobs with names ending with '/', the SDK trims it by default
            _container = new BlobServiceClient(DevelopmentStorage, new BlobClientOptions { TrimBlobNameSlashes = false })
                .GetBlobContainerClient(_containerName);
        }

        [OneTimeTearDown]
        public void DeleteContainer()
        {
            _container?.DeleteIfExists();
        }

        [Test]
        public void Root_AlwaysExists()
        {
            _fs.DirectoryExists("/").Should().BeTrue();
            _fs.GetDirectoryInfo("/").FileObjectExists().Should().BeTrue();
        }

        [Test]
        public void MissingPaths_DoNotExist()
        {
            _fs.FileExists("/missing/file.xml").Should().BeFalse();
            _fs.DirectoryExists("/missing/").Should().BeFalse();
            _fs.GetFileInfo("/missing/file.xml").FileObjectExists().Should().BeFalse();
            _fs.GetDirectoryInfo("/missing/").FileObjectExists().Should().BeFalse();
            _fs.GetFiles("/missing/").Should().BeEmpty();
            _fs.GetDirectories("/missing/", false).Should().BeEmpty();
        }

        [Test]
        public void CreateDirectory_DirectoryExistsWithCreationTimeInPlaceholder()
        {
            _fs.CreateDirectory("/mkd/sub/").Should().BeTrue();

            _fs.DirectoryExists("/mkd/sub/").Should().BeTrue();
            _fs.GetDirectoryInfo("/mkd/sub/").FileObjectExists().Should().BeTrue();

            string creationTime = _container.GetBlobClient("mkd/sub/").GetProperties().Value.Metadata["CreationTime"];
            IFileInfo[] directories = _fs.GetDirectories("/mkd/", true);
            directories.Select(d => d.Path()).Should().Equal("/mkd/sub/");
            directories[0].IsDirectory().Should().BeTrue();
            directories[0].GetModifiedTime().Should()
                .Be(DateTimeOffset.ParseExact(creationTime, "O", null).UtcDateTime);

            // placeholder blob "mkd/sub/" is not a file
            _fs.GetFiles("/mkd/").Should().BeEmpty();
            _fs.GetFiles("/mkd/sub/").Should().BeEmpty();
        }

        [Test]
        public void DirectoryWithoutPlaceholder_ExistsWhenItContainsFiles()
        {
            WriteFile("/implied/a/file.xml", Bytes(10));

            _fs.DirectoryExists("/implied/").Should().BeTrue();
            _fs.DirectoryExists("/implied/a/").Should().BeTrue();
            _fs.GetDirectoryInfo("/implied/a/").FileObjectExists().Should().BeTrue();

            IFileInfo[] directories = _fs.GetDirectories("/implied/", true);
            directories.Select(d => d.Path()).Should().Equal("/implied/a/");
            // without placeholder there is no creation time, current time is used
            directories[0].GetModifiedTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        }

        [Test]
        public void GetDirectoryInfo_PathWithoutSlash_MatchesWholeDirectoryName()
        {
            WriteFile("/noslash/abc/file.xml", Bytes(10));

            _fs.GetDirectoryInfo("/noslash/abc").FileObjectExists().Should().BeTrue();
            _fs.GetDirectoryInfo("/noslash/ab").FileObjectExists().Should().BeFalse();
        }

        [Test]
        public void Listing_SeparatesFilesAndDirectories()
        {
            _fs.CreateDirectory("/list/").Should().BeTrue();
            _fs.CreateDirectory("/list/sub/").Should().BeTrue();
            WriteFile("/list/a.xml", Bytes(10));
            WriteFile("/list/implied/b.xml", Bytes(20));

            IFileInfo[] files = _fs.GetFiles("/list/");
            files.Select(f => f.Path()).Should().Equal("list/a.xml");
            files[0].GetSize().Should().Be(10);
            files[0].IsDirectory().Should().BeFalse();
            files[0].GetModifiedTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

            _fs.GetDirectories("/list/", false).Select(d => d.Path())
                .Should().BeEquivalentTo("/list/implied/", "/list/sub/");
        }

        [TestCase(10)]
        [TestCase(5 * 1024 * 1024 + 123)] // more than one 4 MB block
        public void WriteFile_FileHasContentSizeAndMd5(int size)
        {
            string path = $"/stor/file-{size}.xml";
            byte[] content = Bytes(size);

            WriteFile(path, content);

            _fs.FileExists(path).Should().BeTrue();
            IFileInfo info = _fs.GetFileInfo(path);
            info.FileObjectExists().Should().BeTrue();
            info.IsDirectory().Should().BeFalse();
            info.GetSize().Should().Be(size);
            info.GetModifiedTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

            ReadFile(path).Should().Equal(content);

            using (var md5 = MD5.Create())
            {
                _container.GetBlobClient(path.TrimStart('/')).GetProperties().Value.ContentHash
                    .Should().Equal(md5.ComputeHash(content));
            }
        }

        [Test]
        public void WriteFile_FileAppearsOnlyWhenClosed()
        {
            // a partner's import must not pick up an empty or incomplete file during upload
            IFile file = _fs.OpenFile("/upload/file.xml", true);
            file.Write(Bytes(5 * 1024 * 1024), 5 * 1024 * 1024);

            _fs.FileExists("/upload/file.xml").Should().BeFalse();

            file.Close();
            _fs.FileExists("/upload/file.xml").Should().BeTrue();
        }

        [Test]
        public void WriteFile_ExistingFile_IsOverwritten()
        {
            WriteFile("/overwrite/file.xml", Bytes(100));
            byte[] second = Bytes(10, seed: 2);

            WriteFile("/overwrite/file.xml", second);

            ReadFile("/overwrite/file.xml").Should().Equal(second);
        }

        [Test]
        public void WriteFile_ThroughStreamWriter_IsStored()
        {
            // STOR in ASCII mode writes through a StreamWriter, which closes the blob stream before IFile.Close
            IFile file = _fs.OpenFile("/ascii/file.txt", true);
            using (var writer = new StreamWriter(file.BlobStream, Encoding.ASCII))
            {
                writer.Write("line 1\r\nline 2");
            }
            file.Close();

            Encoding.ASCII.GetString(ReadFile("/ascii/file.txt")).Should().Be("line 1\r\nline 2");
        }

        [Test]
        public void Move_FileIsAvailableUnderNewNameOnly()
        {
            byte[] content = Bytes(100);
            WriteFile("/mv/old.xml", content);

            _fs.Move("/mv/old.xml", "/mv/new.xml").Should().BeTrue();

            _fs.FileExists("/mv/old.xml").Should().BeFalse();
            ReadFile("/mv/new.xml").Should().Equal(content);
        }

        [Test]
        public void DeleteFile_FileNoLongerExists()
        {
            WriteFile("/dele/file.xml", Bytes(10));

            _fs.DeleteFile("/dele/file.xml").Should().BeTrue();

            _fs.FileExists("/dele/file.xml").Should().BeFalse();
            _fs.DeleteFile("/dele/file.xml").Should().BeFalse();
        }

        [Test]
        public void DeleteDirectory_RemovesPlaceholderAndFiles()
        {
            _fs.CreateDirectory("/rmd/").Should().BeTrue();
            WriteFile("/rmd/a.xml", Bytes(10));
            WriteFile("/rmd/b.xml", Bytes(10));

            _fs.DeleteDirectory("/rmd/").Should().BeTrue();

            _fs.DirectoryExists("/rmd/").Should().BeFalse();
            _container.GetBlobs(new GetBlobsOptions { Prefix = "rmd/" }).Should().BeEmpty();
        }

        [Test]
        public void AppendFile_AppendsToFileCreatedByAppend()
        {
            _fs.AppendFile("/appe/file.txt", new MemoryStream(Encoding.ASCII.GetBytes("abc"))).Should().BeTrue();
            _fs.AppendFile("/appe/file.txt", new MemoryStream(Encoding.ASCII.GetBytes("def"))).Should().BeTrue();

            Encoding.ASCII.GetString(ReadFile("/appe/file.txt")).Should().Be("abcdef");
        }

        private void WriteFile(string path, byte[] content)
        {
            IFile file = _fs.OpenFile(path, true);
            file.Write(content, content.Length).Should().Be(content.Length);
            file.Close();
        }

        private byte[] ReadFile(string path)
        {
            IFile file = _fs.OpenFile(path, false);
            var result = new MemoryStream();
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = file.Read(buffer, buffer.Length)) > 0)
            {
                result.Write(buffer, 0, read);
            }
            file.Close();
            return result.ToArray();
        }

        private static byte[] Bytes(int size, int seed = 1)
        {
            var bytes = new byte[size];
            new Random(seed).NextBytes(bytes);
            return bytes;
        }

        private static bool AzuriteIsRunning()
        {
            try
            {
                using (var client = new TcpClient())
                {
                    client.Connect("127.0.0.1", 10000);
                    return true;
                }
            }
            catch (SocketException)
            {
                return false;
            }
        }
    }
}
