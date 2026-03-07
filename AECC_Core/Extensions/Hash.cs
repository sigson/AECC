using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace AECC.Extensions
{
    public class Crc32
    {
        private readonly uint[] _table;
        private const uint Poly = 0xedb88320;

        public uint ComputeChecksum(IEnumerable<byte> bytes)
        {
            var crc = 0xffffffff;
            foreach (var t in bytes)
            {
                var index = (byte)((crc & 0xff) ^ t);
                crc = (crc >> 8) ^ _table[index];
            }

            return ~crc;
        }

        public IEnumerable<byte> ComputeChecksumBytes(IEnumerable<byte> bytes)
        {
            return BitConverter.GetBytes(ComputeChecksum(bytes));
        }

        public Crc32()
        {
            _table = new uint[256];
            for (uint i = 0; i < _table.Length; ++i)
            {
                var temp = i;
                for (var j = 8; j > 0; --j)
                    if ((temp & 1) == 1)
                        temp = (temp >> 1) ^ Poly;
                    else
                        temp >>= 1;
                _table[i] = temp;
            }
        }
    }
    public static class HashExtension
    {
        // Some random MD5 stuff
        public static string MD5(string input)
        {
            if (input == null) input = string.Empty;
            using (MD5 hasher = System.Security.Cryptography.MD5.Create())
            {
                StringBuilder sb = new StringBuilder();
                foreach (byte bit in hasher.ComputeHash(Encoding.UTF8.GetBytes(input)))
                    sb.Append(bit.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}