using NUnit.Framework;
using System;
using System.IO;
using Typhon.Client.Internal;

namespace Typhon.Client.Tests;

[TestFixture]
public class FrameReaderTests
{
    private static MemoryStream BuildFrameStream(byte[] payload)
    {
        var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(payload.Length));
        ms.Write(payload);
        ms.Position = 0;
        return ms;
    }

    private static MemoryStream BuildMultiFrameStream(params byte[][] payloads)
    {
        var ms = new MemoryStream();
        foreach (var payload in payloads)
        {
            ms.Write(BitConverter.GetBytes(payload.Length));
            ms.Write(payload);
        }
        ms.Position = 0;
        return ms;
    }

    [Test]
    public void ReadFrame_SimplePayload_ReturnsCorrectBytes()
    {
        var expected = new byte[] { 1, 2, 3, 4, 5 };
        using var stream = BuildFrameStream(expected);
        var reader = new FrameReader(256);

        var result = reader.ReadFrame(stream);

        Assert.That(result.Length, Is.EqualTo(5));
        Assert.That(result.ToArray(), Is.EqualTo(expected));
    }

    [Test]
    public void ReadFrame_MultipleFrames_ReadsSequentially()
    {
        var payload1 = new byte[] { 10, 20, 30 };
        var payload2 = new byte[] { 40, 50, 60, 70 };
        using var stream = BuildMultiFrameStream(payload1, payload2);
        var reader = new FrameReader(256);

        var result1 = reader.ReadFrame(stream);
        Assert.That(result1.ToArray(), Is.EqualTo(payload1));

        var result2 = reader.ReadFrame(stream);
        Assert.That(result2.ToArray(), Is.EqualTo(payload2));
    }

    [Test]
    public void ReadFrame_LargePayload_BufferGrows()
    {
        // Start with 256-byte buffer, send 1024-byte payload
        var payload = new byte[1024];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 256);
        }

        using var stream = BuildFrameStream(payload);
        var reader = new FrameReader(256);

        var result = reader.ReadFrame(stream);

        Assert.That(result.Length, Is.EqualTo(1024));
        Assert.That(result.ToArray(), Is.EqualTo(payload));
    }

    [Test]
    public void ReadFrame_ConnectionClosed_ThrowsEndOfStream()
    {
        // Stream with length prefix but no payload data
        var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(100)); // Claims 100 bytes
        ms.Position = 0;
        var reader = new FrameReader(256);

        Assert.Throws<EndOfStreamException>(() => reader.ReadFrame(ms));
    }

    [Test]
    public void ReadFrame_EmptyStream_ThrowsEndOfStream()
    {
        using var stream = new MemoryStream();
        var reader = new FrameReader(256);

        Assert.Throws<EndOfStreamException>(() => reader.ReadFrame(stream));
    }

    [Test]
    public void ReadFrame_ZeroLength_ThrowsInvalidData()
    {
        var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(0));
        ms.Position = 0;
        var reader = new FrameReader(256);

        Assert.Throws<InvalidDataException>(() => reader.ReadFrame(ms));
    }

    [Test]
    public void ReadFrame_NegativeLength_ThrowsInvalidData()
    {
        var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(-1));
        ms.Position = 0;
        var reader = new FrameReader(256);

        Assert.Throws<InvalidDataException>(() => reader.ReadFrame(ms));
    }

    [Test]
    public void ReadFrame_ExcessiveLength_ThrowsInvalidData()
    {
        var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(17 * 1024 * 1024)); // 17 MB, over 16 MB limit
        ms.Position = 0;
        var reader = new FrameReader(256);

        Assert.Throws<InvalidDataException>(() => reader.ReadFrame(ms));
    }

    [Test]
    public void ReadFrame_PartialReads_HandlesFragmentation()
    {
        // Use a SlowStream that returns 1 byte at a time
        var payload = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE };
        var ms = BuildFrameStream(payload);
        using var slowStream = new SlowStream(ms, maxBytesPerRead: 1);
        var reader = new FrameReader(256);

        var result = reader.ReadFrame(slowStream);

        Assert.That(result.ToArray(), Is.EqualTo(payload));
    }

    [Test]
    public void ReadFrame_BufferReuse_PreviousDataNotCorrupted()
    {
        var payload1 = new byte[] { 1, 2, 3 };
        var payload2 = new byte[] { 4, 5, 6, 7, 8 };
        using var stream = BuildMultiFrameStream(payload1, payload2);
        var reader = new FrameReader(256);

        // Read first and copy it
        var result1 = reader.ReadFrame(stream).ToArray();

        // Read second — the underlying buffer is reused but result1 (copied) is safe
        var result2 = reader.ReadFrame(stream).ToArray();

        Assert.That(result1, Is.EqualTo(payload1));
        Assert.That(result2, Is.EqualTo(payload2));
    }

    /// <summary>
    /// Stream wrapper that limits the number of bytes returned per Read call, simulating TCP fragmentation.
    /// </summary>
    private sealed class SlowStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _maxBytesPerRead;

        public SlowStream(Stream inner, int maxBytesPerRead)
        {
            _inner = inner;
            _maxBytesPerRead = maxBytesPerRead;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, Math.Min(count, _maxBytesPerRead));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
