using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TraceService.Models
{
    // Obiekt przekazujący stan sterowania z PLC do PC
    public class PlcControlState
    {
        public int TaskSendToPc { get; set; }      // Zadanie od PLC (np. 1 = Check, 2 = Save)
        public int TaskConfirmFromPc { get; set; } // Obecny status potwierdzenia (do weryfikacji pętli)
    }
}
