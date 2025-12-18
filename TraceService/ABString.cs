using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TraceService
{
    [StructLayout(LayoutKind.Sequential)]
    public class ABString
    {
        #region Fields

        public Int32 len;
        public Byte[] data = new Byte[82];

        #endregion Fields

        #region Methods

        override public String ToString()
        {
            return ASCIIEncoding.ASCII.GetString(data, 0, len);
        }

        public void SetString(String s)
        {
            if (s.Length > data.Length)
                throw new ArgumentOutOfRangeException("s", "Maximum allowable string length is " + data.Length.ToString() + " bytes");
            Buffer.BlockCopy(ASCIIEncoding.ASCII.GetBytes(s), 0, data, 0, s.Length);
            len = s.Length;
        }

        #endregion Methods
    }
}
