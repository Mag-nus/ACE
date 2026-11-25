using System;

namespace Bench
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            Console.WriteLine($".NET {Environment.Version}, {Environment.ProcessorCount} cpus, ServerGC={System.Runtime.GCSettings.IsServerGC}");
            foreach (var a in args)
            {
                switch (a)
                {
                    case "inbound": InboundBench.Run(); break;
                    case "outbound": OutboundBench.Run(); break;
                    case "tick": TickBench.Run(); break;
                    case "socket": SocketBench.Run(); break;
                    case "micro": MicroBench.Run(); break;
                    default: Console.WriteLine("unknown " + a); break;
                }
            }
        }
    }
}
