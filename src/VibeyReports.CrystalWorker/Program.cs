using System;

namespace VibeyReports.CrystalWorker
{
    /// <summary>
    /// Entry point required because the project is built as an Exe (OutputType=Exe) so that
    /// the worker can eventually be launched as a standalone x86 process by later tasks.
    /// There is no CLI surface yet — Task 4 only proves CrystalSession can bind to the RAS SDK.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            Console.WriteLine("VibeyReports.CrystalWorker: no command specified.");
            return 0;
        }
    }
}
