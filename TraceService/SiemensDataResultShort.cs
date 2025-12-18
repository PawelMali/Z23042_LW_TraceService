using System;
using System.Runtime.InteropServices;
using AutomatedSolutions.ASCommStd.SI.S7.Data;

namespace TraceService
{
    [StructLayout(LayoutKind.Sequential)]
    public class SiemensDataResultShort : UDT
    {
        public SiemensString DMC_Code1 = new SiemensString();
        public SiemensString DMC_Code2 = new SiemensString();
        public Int16 Operation_Result1;
        public Int16 Operation_Result2;
        public DTL Operation_DateTime1 = new DTL();
        public DTL Operation_DateTime2 = new DTL();
        public SiemensString Reference = new SiemensString();
        public Int16 Cycle_Time;
        public SiemensString Operator = new SiemensString();
        public Int32[] dints = new Int32[10];
        public Single[] reals = new Single[10];
        public DTL[] dtls = InitializeArray<DTL>(3);
        public SiemensString[] strings = InitializeArray<SiemensString>(7);

        public static T[] InitializeArray<T>(Int32 length) where T : new()
        {
            T[] array = new T[length];
            for (Int32 i = 0; i < length; i++)
            {
                array[i] = new T();
            }
            return array;
        }

        public Single[] Reals
        {
            get { return reals; }
            set { reals = value; }
        }
    }
}
