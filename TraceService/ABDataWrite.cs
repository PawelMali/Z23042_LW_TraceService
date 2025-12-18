using System;
using System.Runtime.InteropServices;

namespace TraceService
{
    [StructLayout(LayoutKind.Sequential)]
    public class ABDataWrite
    {
        public Int16 Life;
        public UInt16 Task_Counter;
        public Int16 Task_Recived_From_PC;
        public Int16 Task_Confirm_From_PC;
        public UInt16 Task_Succes_Counter;
        public Int16 Error_Status;
        public Int16 Part_Status;
        public Int16 Reserve2;
        public Int16 Reserve3;
        public Int16 Reserve4;
        public Int16 Reserve5;
    }
}
