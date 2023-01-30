using System;
using System.Threading;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Data;
using System.IO;
using System.ComponentModel;
using System.Linq;
using System.Text;
using VMS.TPS.Common.Model.API;
using System.Collections.Concurrent;

[assembly: ESAPIScript(IsWriteable = true)]

namespace VMS.TPS
{
    public class ESAPIApplication

    {
        private static readonly Lazy<ESAPIApplication> _instance = new Lazy<ESAPIApplication>(() => new ESAPIApplication());
        // private to prevent direct instantiation.

        private ESAPIApplication()
        {
            try
            {
                Context = VMS.TPS.Common.Model.API.Application.CreateApplication();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return;
            }
        }
        public Application Context { get; private set; }
        public static bool IsLoaded { get; set; }
        // accessor for instance
        public static ESAPIApplication Instance
        {
            get
            {
                IsLoaded = true;
                return _instance.Value;

            }
        }
        public static void Dispose()
        {
            if (IsLoaded) { Instance.Context.Dispose(); }
        }
    }
}
