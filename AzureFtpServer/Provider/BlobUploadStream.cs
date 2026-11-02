using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace AzureFtpServer.Provider
{
    /// <summary>
    /// Uploads written data as blocks of a block blob and commits them when closed, together with MD5
    /// of the whole content in blob's Content-MD5 - the way the former WindowsAzure.Storage OpenWrite
    /// (with StoreBlobContentMD5 option) did. The blob appears only once the upload is complete,
    /// unlike with BlockBlobClient.OpenWrite, which creates an empty blob up front.
    /// Can be closed more than once: STOR in ASCII mode closes it through StreamWriter and then by IFile.Close.
    /// </summary>
    internal sealed class BlobUploadStream : Stream
    {
        private const int BlockSize = 4 * 1024 * 1024;

        private readonly BlockBlobClient _blob;
        private readonly List<string> _blockIds = new List<string>();
        private readonly MemoryStream _block = new MemoryStream();
        private readonly IncrementalHash _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        private bool _closed;

        public BlobUploadStream(BlockBlobClient blob)
        {
            _blob = blob;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_closed;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_closed)
            {
                throw new ObjectDisposedException(nameof(BlobUploadStream));
            }

            _md5.AppendData(buffer, offset, count);
            while (count > 0)
            {
                int chunk = Math.Min(count, BlockSize - (int)_block.Length);
                _block.Write(buffer, offset, chunk);
                offset += chunk;
                count -= chunk;

                if (_block.Length == BlockSize)
                {
                    StageBlock();
                }
            }
        }

        // blocks are committed when the stream is closed
        public override void Flush()
        {
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                _closed = true;
                StageBlock();
                _blob.CommitBlockList(_blockIds,
                    new CommitBlockListOptions { HttpHeaders = new BlobHttpHeaders { ContentHash = _md5.GetHashAndReset() } });
                _md5.Dispose();
                _block.Dispose();
            }

            base.Dispose(disposing);
        }

        private void StageBlock()
        {
            if (_block.Length == 0)
            {
                return;
            }

            // all block ids of a blob must have the same length
            string blockId = Convert.ToBase64String(Encoding.ASCII.GetBytes(_blockIds.Count.ToString("d6")));
            _block.Position = 0;
            _blob.StageBlock(blockId, _block);
            _blockIds.Add(blockId);
            _block.SetLength(0);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
