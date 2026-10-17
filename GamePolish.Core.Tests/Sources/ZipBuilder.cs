using System.IO.Compression;
using System.Text;

namespace GamePolish.Core.Tests.Sources;

/// <summary>
/// Builds archives in memory, including the ones a hostile or careless user would supply.
/// </summary>
/// <remarks>
/// A real <see cref="ZipArchive"/> is used rather than a fixed byte array so an entry can be named anything
/// at all - <c>../escape.txt</c>, <c>C:\abs.txt</c>, <c>a.txt:ads</c>, <c>CON</c> - which is exactly the
/// input the extractor has to refuse.
/// </remarks>
internal static class ZipBuilder
{
    /// <summary>The Unix mode bits of a symbolic link, as an archiver writes them.</summary>
    public const int UnixSymbolicLinkMode = 0xA1FF;

    /// <summary>An archive from (name, contents) pairs, using deflate.</summary>
    /// <param name="entries">Entry names and their contents.</param>
    public static MemoryStream Build(params (string Name, string Content)[] entries) =>
        Build(entries.Select(entry => (entry.Name, Encoding.UTF8.GetBytes(entry.Content))));

    /// <summary>An archive from (name, bytes) pairs, using deflate.</summary>
    /// <param name="entries">Entry names and their contents.</param>
    public static MemoryStream Build(IEnumerable<(string Name, byte[] Content)> entries)
    {
        MemoryStream buffer = new();

        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);

                using Stream stream = entry.Open();
                stream.Write(content, 0, content.Length);
            }
        }

        buffer.Position = 0;

        return buffer;
    }

    /// <summary>An archive whose single entry carries Unix mode bits, i.e. how a symlink arrives.</summary>
    /// <param name="name">The entry's name.</param>
    /// <param name="unixMode">The mode to put in the high 16 bits of the external attributes.</param>
    public static MemoryStream BuildWithUnixMode(string name, int unixMode)
    {
        MemoryStream buffer = new();

        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);

            entry.ExternalAttributes = unixMode << 16;

            using Stream stream = entry.Open();
            stream.Write("target"u8);
        }

        buffer.Position = 0;

        return buffer;
    }

    /// <summary>An archive whose single entry carries the Windows reparse-point attribute.</summary>
    /// <param name="name">The entry's name.</param>
    public static MemoryStream BuildWithWindowsReparsePoint(string name)
    {
        MemoryStream buffer = new();

        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);

            entry.ExternalAttributes = 0x400;

            using Stream stream = entry.Open();
            stream.Write("target"u8);
        }

        buffer.Position = 0;

        return buffer;
    }

    /// <summary>An archive consisting of one highly compressible entry, i.e. the shape of a bomb.</summary>
    /// <param name="name">The entry's name.</param>
    /// <param name="uncompressedBytes">How large the entry expands to.</param>
    public static MemoryStream BuildHighlyCompressible(string name, int uncompressedBytes)
    {
        MemoryStream buffer = new();

        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);

            using Stream stream = entry.Open();
            byte[] zeros = new byte[uncompressedBytes];

            stream.Write(zeros, 0, zeros.Length);
        }

        buffer.Position = 0;

        return buffer;
    }

    /// <summary>An archive with no entries at all.</summary>
    public static MemoryStream Empty()
    {
        MemoryStream buffer = new();

        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
        }

        buffer.Position = 0;

        return buffer;
    }

    /// <summary>A stream that cannot seek, like the body of an HTTP upload.</summary>
    /// <param name="archive">The archive to wrap.</param>
    public static Stream NonSeekable(Stream archive) => new NonSeekableStream(archive);

    /// <summary>A read-only stream that refuses to seek or report a length.</summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly Stream _inner;

        public NonSeekableStream(Stream inner)
        {
            _inner = inner;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
