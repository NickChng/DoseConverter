using EQD2Converter;
using ESAPIScript;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Remoting.Contexts;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using VMS.TPS.Common.Model.API;

[assembly: ESAPIScript(IsWriteable = true)]

namespace VMS.TPS
{
    class Script
    {
        private void RunOnNewStaThread(Action a)
        {
            Thread thread = new Thread(() => a());
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }

        private void InitializeAndStartMainWindow(EsapiWorker esapiWorker)
        {
            var viewModel = new ViewModel(esapiWorker);
            var mainWindow = new MainWindow(viewModel);
            mainWindow.ShowDialog();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Execute(ScriptContext scriptcontext)
        {
            if (scriptcontext.ExternalPlanSetup == null)
            {
                MessageBox.Show("No plan is open.", "Error");
                return;
            }

            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");

            Helpers.SeriLog.Initialize(scriptcontext.CurrentUser.Id);
            // The ESAPI worker needs to be created in the main thread
            //EsapiWorker esapiWorker = null;
            //if (scriptcontext.PlanSumsInScope.Count()>0)
            //    esapiWorker = new EsapiWorker(scriptcontext.Patient, scriptcontext.PlanSum);
            //else
            var esapiWorker = new EsapiWorker(scriptcontext.Patient, scriptcontext.PlanSetup);

            // This new queue of tasks will prevent the script
            // for exiting until the new window is closed
            DispatcherFrame frame = new DispatcherFrame();

            RunOnNewStaThread(() =>
            {
                // This method won't return until the window is closed

                InitializeAndStartMainWindow(esapiWorker);

                // End the queue so that the script can exit
                frame.Continue = false;
            });

            // Start the new queue, waiting until the window is closed
            Dispatcher.PushFrame(frame);
        }
    }
}
