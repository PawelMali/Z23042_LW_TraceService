using System;
using System.Runtime.InteropServices;
using AutomatedSolutions.ASCommStd.SI.S7.Data;

namespace TraceService
{
    [StructLayout(LayoutKind.Sequential)]
    public class SiemensDataRead : UDT
    {
        public Int16 Life;
        public UInt16 Task_Counter;
        public Int16 Task_Send_To_PC;
        public Int16 Task_Confirm_To_PC;
        public UInt16 Task_Succes_Counter;
        public Int16 Error_Status;
        public Int16 Reserve1;
        public Int16 Reserve2;
        public Int16 Reserve3;
        public Int16 Reserve4;
        public Int16 Reserve5;
    }
}
