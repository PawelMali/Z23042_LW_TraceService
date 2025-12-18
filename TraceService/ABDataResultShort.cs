using System;
using System.Runtime.InteropServices;

namespace TraceService
{
    [StructLayout(LayoutKind.Sequential)]
    public class ABDataResultShort
    {
        public ABString DMC_Code1 = new ABString();
        public ABString DMC_Code2 = new ABString();
        public Int16 Operation_Result1;
        public Int16 Operation_Result2;
        public ABDTL Operation_DateTime1 = new ABDTL();
        public ABDTL Operation_DateTime2 = new ABDTL();
        public ABString Reference = new ABString();
        public Int16 Cycle_Time;
        public ABString Operator = new ABString();
        public Int32[] dints = new Int32[10];
        public Single[] reals = new Single[10];
        public ABDTL[] dtls = InitializeArray<ABDTL>(3);
        public ABString[] strings = InitializeArray<ABString>(7);

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
