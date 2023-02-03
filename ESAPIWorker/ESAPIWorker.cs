using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using System.Threading.Tasks;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace ESAPIScript
{
    public class EsapiWorker 
    {
        private readonly StructureSet _ss = null;
        private readonly PlanSetup _pl = null;
        private readonly Patient _p = null;
        private readonly Dispatcher Dispatcher = null;
        
        public EsapiWorker(Patient p, StructureSet ss)
        {
            _p = p;
            _ss = ss;
            Dispatcher = Dispatcher.CurrentDispatcher;
        }
        public EsapiWorker(Patient p, PlanSetup pl)
        {
            _p = p;
            _pl = pl;
            _ss = pl.StructureSet;
            Dispatcher = Dispatcher.CurrentDispatcher;
        }
    
        public delegate void D(Patient p, StructureSet s);
        public async Task<bool> AsyncRunStructureContext(Action<Patient, StructureSet> a)
        {
            await Dispatcher.BeginInvoke(a, _p, _ss);
            return true;
        }
        public async Task<bool> AsyncRunPlanContext(Action<Patient, PlanSetup> a)
        {
            await Dispatcher.BeginInvoke(a, _p, _pl);
            return true;
        }
    }
}
