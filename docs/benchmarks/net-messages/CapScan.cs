using System;
using System.IO;
using System.Linq;
using System.Reflection;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages;
using ACE.Server.WorldObjects;

static class CapScan
{
    public static void Run()
    {
        var declared = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "../../../declared_caps.txt"))
            .Select(l => l.Split('|')).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.Select(p => p[1]).ToArray());
        var asm = typeof(GameMessage).Assembly;
        var wo = Stubs.MakeWorldObject();
        int over = 0, fit = 0, fail = 0;
        foreach (var t in asm.GetTypes().Where(t => typeof(GameMessage).IsAssignableFrom(t) && !t.IsAbstract).OrderBy(t => t.Name))
        {
            if (!declared.TryGetValue(t.Name, out var caps)) continue;
            foreach (var (ctor, i) in t.GetConstructors().Select((c, i) => (c, i)))
            {
                var cap = i < caps.Length ? caps[i] : caps[0];
                if (cap == "-") continue;
                int declaredCap = int.Parse(cap);
                try
                {
                    var args = ctor.GetParameters().Select(p => Arg(p, wo)).ToArray();
                    var m = (GameMessage)ctor.Invoke(args);
                    var len = m.Data.Length; var final = m.Data.Capacity;
                    bool o = len > declaredCap;
                    if (o) over++; else fit++;
                    Console.WriteLine($"{(o ? "OVERFLOW" : "ok      ")} {t.Name,-48} declared={declaredCap,5} minLen={len,5} finalCap={final,5}");
                }
                catch (Exception e)
                {
                    fail++;
                    var ie = e is TargetInvocationException tie ? tie.InnerException : e;
                    Console.WriteLine($"n/a      {t.Name,-48} declared={declaredCap,5} (could not construct: {ie?.GetType().Name})");
                }
            }
        }
        Console.WriteLine($"overflow={over} fit={fit} not-constructible={fail}");
    }

    static object Arg(ParameterInfo p, WorldObject wo)
    {
        var t = p.ParameterType;
        if (t == typeof(Session)) return Stubs.MakeSession();
        if (t.IsAssignableFrom(typeof(StubWO))) return wo;
        if (t == typeof(string)) return "";
        if (Nullable.GetUnderlyingType(t) != null) return null;
        if (t.IsValueType) return Activator.CreateInstance(t);
        return null;
    }
}
