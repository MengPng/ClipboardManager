using System.IO;

namespace ClipboardManager.Windows.Services;

internal sealed class BoundedMemoryStream(long maximumLength) : MemoryStream
{
    private readonly long _maximumLength = maximumLength;

    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureCapacityForWrite(count);
        base.Write(buffer, offset, count);
    }

    public override void WriteByte(byte value)
    {
        EnsureCapacityForWrite(1);
        base.WriteByte(value);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureCapacityForWrite(buffer.Length);
        base.Write(buffer);
    }

    public override void SetLength(long value)
    {
        if (value > _maximumLength) throw new IOException("剪切板图片编码超过安全大小限制。");
        base.SetLength(value);
    }

    private void EnsureCapacityForWrite(int count)
    {
        if (count < 0 || Position > _maximumLength - count)
            throw new IOException("剪切板图片编码超过安全大小限制。");
    }
}
