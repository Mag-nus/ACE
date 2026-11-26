using System;
using System.Linq;

namespace NetE2E
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("usage: NetE2E <mode> [log=bridge|none] [key=value ...]");
                Console.WriteLine("modes: tick systemic flush retained broadcast inbound ack types behave e2e sendcost");
                return 1;
            }
            string mode = args[0];
            Env.Init(U.ArgS(args, "log", "bridge"));
            Console.WriteLine($"[env] Timers.PortalYearTicks={ACE.Server.Entity.Timers.PortalYearTicks:F0}  (ushort)={(ushort)ACE.Server.Entity.Timers.PortalYearTicks}");
            switch (mode)
            {
                case "tick": TickMode.Run(args); break;
                case "systemic": SystemicMode.Run(args); break;
                case "flush": FlushMode.Run(args); break;
                case "retained": RetainedMode.Run(args); break;
                case "broadcast": BroadcastMode.Run(args); break;
                case "inbound": InboundMode.Run(args); break;
                case "ack": AckMode.Run(args); break;
                case "types": TypesMode.Run(args); break;
                case "behave": BehaveMode.Run(args); break;
                case "e2e": E2EMode.Run(args); break;
                case "sendcost": SendCostMode.Run(args); break;
                default: Console.WriteLine("unknown mode " + mode); return 1;
            }
            Console.Out.Flush();
            Environment.Exit(0); // the real listeners keep background receives pending
            return 0;
        }
    }
}
