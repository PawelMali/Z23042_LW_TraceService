using System;
using System.Text;

namespace TraceService
{
    public class CRC32
    {
        private static readonly uint[] Table;

        static CRC32()
        {
            const uint polynomial = 0xedb88320;
            Table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (uint j = 8; j > 0; j--)
                {
                    if ((crc & 1) == 1)
                    {
                        crc = (crc >> 1) ^ polynomial;
                    }
                    else
                    {
                        crc >>= 1;
                    }
                }
                Table[i] = crc;
            }
        }

        public static uint Compute(byte[] bytes)
        {
            uint crc = 0xffffffff;
            foreach (byte b in bytes)
            {
                byte tableIndex = (byte)((crc & 0xff) ^ b);
                crc = (crc >> 8) ^ Table[tableIndex];
            }
            return ~crc;
        }

        public static string ComputeHex(string input)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(input);
            uint crc32 = Compute(bytes);
            return crc32.ToString("X8");
        }
    }
}
