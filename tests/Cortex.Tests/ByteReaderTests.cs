using Cortex.Services;
using Xunit;

namespace Cortex.Tests;

public class ByteReaderTests
{
[Fact]
public void Constructor_WithStartIndex_InitializesPositionAndRemaining()
{
var buffer = new byte[] { 10, 20, 30, 40, 50 };

var reader = new ByteReader(buffer, startIndex: 2);

Assert.Equal(2, reader.Position);
Assert.Equal(3, reader.Remaining);
}

[Fact]
public void ReadByte_ReturnsValueAndAdvancesPosition()
{
var reader = new ByteReader(new byte[] { 0x11, 0x22 });

var value = reader.ReadByte();

Assert.Equal((byte)0x11, value);
Assert.Equal(1, reader.Position);
Assert.Equal(1, reader.Remaining);
}

[Fact]
public void ReadInt16_AndReadUInt16_ReadExpectedValues()
{
var bytes = new byte[] { 0x34, 0x12, 0xFE, 0xFF };
var reader = new ByteReader(bytes);

short signed = reader.ReadInt16();
ushort unsigned = reader.ReadUInt16();

Assert.Equal((short)0x1234, signed);
Assert.Equal((ushort)0xFFFE, unsigned);
Assert.Equal(4, reader.Position);
Assert.Equal(0, reader.Remaining);
}

[Fact]
public void ReadInt32_AndReadUInt32_ReadExpectedValues()
{
var bytes = new byte[]
{
0x78, 0x56, 0x34, 0x12,
0xF0, 0xDE, 0xBC, 0x9A,
};
var reader = new ByteReader(bytes);

int signed = reader.ReadInt32();
uint unsigned = reader.ReadUInt32();

Assert.Equal(0x12345678, signed);
Assert.Equal(0x9ABCDEF0u, unsigned);
Assert.Equal(8, reader.Position);
Assert.Equal(0, reader.Remaining);
}

[Fact]
public void ReadSingle_ReadsFloatAndAdvancesPosition()
{
var bytes = BitConverter.GetBytes(12.5f);
var reader = new ByteReader(bytes);

float value = reader.ReadSingle();

Assert.Equal(12.5f, value);
Assert.Equal(4, reader.Position);
Assert.Equal(0, reader.Remaining);
}

[Fact]
public void ReadChars_ReadsAsciiCharactersAndAdvancesPosition()
{
var reader = new ByteReader(new byte[] { (byte)'A', (byte)'B', (byte)'C', 0x00 });

char[] chars = reader.ReadChars(3);

Assert.Equal(new[] { 'A', 'B', 'C' }, chars);
Assert.Equal(3, reader.Position);
Assert.Equal(1, reader.Remaining);
}

[Fact]
public void ReadBytes_ReturnsCopyAndAdvancesPosition()
{
var source = new byte[] { 1, 2, 3, 4 };
var reader = new ByteReader(source);

byte[] bytes = reader.ReadBytes(2);
bytes[0] = 99;

Assert.Equal(new byte[] { 99, 2 }, bytes);
Assert.Equal(new byte[] { 1, 2, 3, 4 }, source);
Assert.Equal(2, reader.Position);
Assert.Equal(2, reader.Remaining);
}

[Fact]
public void ReadFixedAsciiString_StopsAtNullAndTrimsTrailingSpaces()
{
var reader = new ByteReader(new byte[] { (byte)'T', (byte)'E', (byte)'S', (byte)'T', (byte)' ', (byte)' ', 0x00, (byte)'X' });

string value = reader.ReadFixedAsciiString(8);

Assert.Equal("TEST", value);
Assert.Equal(8, reader.Position);
Assert.Equal(0, reader.Remaining);
}

[Fact]
public void ReadFixedAsciiString_WithoutNull_TrimsTrailingSpaces()
{
var reader = new ByteReader(new byte[] { (byte)'A', (byte)'B', (byte)'C', (byte)' ' });

string value = reader.ReadFixedAsciiString(4);

Assert.Equal("ABC", value);
Assert.Equal(4, reader.Position);
Assert.Equal(0, reader.Remaining);
}

[Fact]
public void ReadByte_WhenAtEnd_ThrowsIndexOutOfRangeException()
{
var reader = new ByteReader(new byte[] { 0x11 }, startIndex: 1);

Assert.Throws<IndexOutOfRangeException>(() => reader.ReadByte());
}

[Fact]
public void ReadInt16_WhenInsufficientBytes_ThrowsArgumentException()
{
var reader = new ByteReader(new byte[] { 0x11 });

Assert.Throws<ArgumentException>(() => reader.ReadInt16());
}

[Fact]
public void ReadInt32_WhenInsufficientBytes_ThrowsArgumentException()
{
var reader = new ByteReader(new byte[] { 0x11, 0x22, 0x33 });

Assert.Throws<ArgumentException>(() => reader.ReadInt32());
}

[Fact]
public void ReadBytes_WhenCountExceedsRemaining_ThrowsArgumentException()
{
var reader = new ByteReader(new byte[] { 0x11, 0x22 });

Assert.Throws<ArgumentException>(() => reader.ReadBytes(3));
}

[Fact]
public void ReadChars_WhenCountExceedsRemaining_ThrowsIndexOutOfRangeException()
{
var reader = new ByteReader(new byte[] { (byte)'A' });

Assert.Throws<IndexOutOfRangeException>(() => reader.ReadChars(2));
}
}
