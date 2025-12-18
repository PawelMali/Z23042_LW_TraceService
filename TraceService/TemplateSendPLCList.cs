using System;

namespace TraceService
{
    public class TemplateSendPLCList
    {
        public String MachineID { get; set; }
        public String MachineName { get; set; }
        public String Type { get; set; }
        public String IP { get; set; }
        public Boolean IsRunning { get; set; }
        public String Status { get; set; }
        public DateTime MessageTime { get; set; }
        public Object readData { get; set; }
        public Object writeData { get; set; }
        public Object resultData { get; set; }
    }
}
