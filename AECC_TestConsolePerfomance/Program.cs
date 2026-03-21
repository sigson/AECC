using Async;
using Threads;

namespace AECC_TestConsolePerfomance
{
    internal class Program
    {
        static void Main(string[] args)
        {
            new Thread(() => new Simulation().Start()).Start();
            //new Thread(() => new SpaceSimulationPipelines().Start()).Start();
            
            //new Thread(() => new SimulationThreads().Start()).Start();
            Console.ReadLine();
        }
    }
}
