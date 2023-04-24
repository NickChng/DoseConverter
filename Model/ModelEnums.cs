using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DoseConverter
{
    public enum DoseFormat
    {
        [Description("None")] None,
        [Description("EQD2")] EQD2,
        [Description("BED")] BED,
        [Description("EQDd")] EQDd,
        [Description("EQDn#")] EQDn,
        [Description("BASE")] Base,
    }
    public enum AlphaBetaSortFormat
    {
        [Description("Ascending")] Ascending,
        [Description("Descending")] Descending
    }
}
