using System;

namespace Cortex.Services
{
    public class ByteReader
    {
        private readonly byte[] _buffer;
        private int _index;

        public ByteReader(byte[] buffer, int startIndex = 0)
        {
            _buffer = buffer;
            _index = startIndex;
        }

        public byte ReadByte()
        {
            return _buffer[_index++];
        }

        public int ReadInt32()
        {
            int value = BitConverter.ToInt32(_buffer, _index);
            _index += 4;
            return value;
        }

        public uint ReadUInt32()
        {
            uint value = BitConverter.ToUInt32(_buffer, _index);
            _index += 4;
            return value;
        }

        public float ReadSingle()
        {
            float value = BitConverter.ToSingle(_buffer, _index);
            _index += 4;
            return value;
        }

        public short ReadInt16()
        {
            short value = BitConverter.ToInt16(_buffer, _index);
            _index += 2;
            return value;
        }

        public ushort ReadUInt16()
        {
            ushort value = BitConverter.ToUInt16(_buffer, _index);
            _index += 2;
            return value;
        }

        public char[] ReadChars(int count)
        {
            char[] result = new char[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = (char)_buffer[_index++]; // 1 byte = 1 char
            }
            return result;
        }

        public byte[] ReadBytes(int count)
        {
            byte[] result = new byte[count];
            Buffer.BlockCopy(_buffer, _index, result, 0, count);
            _index += count;
            return result;
        }
    }

}

