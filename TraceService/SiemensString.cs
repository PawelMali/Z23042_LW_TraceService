using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TraceService
{
    [StructLayout(LayoutKind.Sequential)]
    public class SiemensString
    {
        #region Fields

        private const Int32 maxLen = 254;
        public Int16 len;
        public Byte[] data = new Byte[maxLen];

        #endregion Fields

        #region Methods

        override public String ToString()
        {
            Int32 lenght = len + 512;
            return ASCIIEncoding.ASCII.GetString(data, 0, lenght);
        }

        public void SetString(String s)
        {
            if (s.Length > data.Length)
                throw new ArgumentOutOfRangeException(nameof(s), "Maximum allowable string length is " + data.Length.ToString() + " bytes");
            Buffer.BlockCopy(ASCIIEncoding.ASCII.GetBytes(s), 0, data, 0, s.Length);
            len = (Int16)s.Length;
        }

        public void Clear()
        {
            len = 0;

            for (int i = 0; i < data.Length; i++)
                data[i] = 0;
        }

        #endregion Methods
    }
}
