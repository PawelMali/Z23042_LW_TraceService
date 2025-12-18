using System;
using System.Runtime.InteropServices;

namespace TraceService
{
    [StructLayout(LayoutKind.Sequential)]
    public class ABDTL
    {
        #region Fields

        public Int32 year;
        public Int32 month;
        public Int32 day;
        public Int32 hour;
        public Int32 minute;
        public Int32 second;
        public Int32 nanosecond;

        #endregion Fields

        #region Methods

        public DateTime ToDateTime()
        {
            try
            {
                return new DateTime(year, month, day, hour, minute, second);
            }
            catch 
            {
                return new DateTime(1970, 1, 1, 0, 0, 0);
            }
            
        }

        #endregion Methods
    }
}
