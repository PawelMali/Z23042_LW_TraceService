using System;

namespace TraceService
{
    public class TemplatePreviousMachine
    {
        public Int32 MachineID { get; set; }
        public Int16 MaxDaysNumber { get; set; }
        public Boolean CheckSecondaryCode { get; set; }

        public string DbIp { get; set; }
        public string DbUser { get; set; }
        public string DbPassword { get; set; }
    }
}
