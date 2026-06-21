using System;
using AECC.Locking.Benchmark;

namespace AECC.Locking
{
    public static class Program
    {
        // Usage: AECC.Locking [entities] [components] [durationMs] [threads]
        // Defaults model a slice of the target load; bump entities to approach 1M (watch RAM for
        // the legacy backend — that is precisely the point being measured).
        public static void Main(string[] args)
        {
            Defines.OneThreadMode = false;

            // Consistency stress mode: AECC.Locking dictstress [dicts] [keys] [threads] [durationMs]
            if (true || args != null && args.Length > 0 && args[0] == "dictstress")
            {
                int dicts = Arg(args, 1, 2);
                int keys = Arg(args, 2, 24);
                int threads = Arg(args, 3, Math.Max(8, Environment.ProcessorCount * 2));
                int durMs = Arg(args, 4, 5000);
                try { Benchmark.DictStress.Run(dicts, keys, threads, durMs); }
                catch (Exception ex) { Console.WriteLine("FATAL: " + ex); Environment.ExitCode = 1; }
                //return;
            }

            // Perf comparison mode: AECC.Locking dictbench [dicts] [keys] [threads] [durationMs]
            if (true || args != null && args.Length > 0 && args[0] == "dictbench")
            {
                int dicts = Arg(args, 1, 2);
                int keys = Arg(args, 2, 2000);
                int threads = Arg(args, 3, Math.Max(8, Environment.ProcessorCount * 2));
                int durMs = Arg(args, 4, 5000);
                try { Benchmark.DictBench.Run(dicts, keys, threads, durMs); }
                catch (Exception ex) { Console.WriteLine("FATAL: " + ex); Environment.ExitCode = 1; }
                return;
            }

            int entities = Arg(args, 0, 10000000);
            int components = Arg(args, 1, 20);
            int durationMs = Arg(args, 2, 5000);
            int threadsN = Arg(args, 3, 16);

            try
            {
                AECC.Locking.Benchmark.Harness.Run(entities, components, durationMs, threadsN);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex);
                Environment.ExitCode = 1;
            }
        }

        private static int Arg(string[] args, int i, int fallback)
        {
            int v;
            if (args != null && args.Length > i && int.TryParse(args[i], out v)) return v;
            return fallback;
        }
    }
}
