// Semantic scan of ACE.Server for every site that depends on AFrame / Physics.Common.Position
// being reference types. Reads the source read-only; never writes to it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

class Program
{
    static void Main(string[] args)
    {
        var srcRoot = args[0];              // .../Source/ACE.Server
        var binDir = args[1];               // snapshot bin
        var outDir = args[2];
        var trees = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/"))
            .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), new CSharpParseOptions(LanguageVersion.Preview), path: f.Substring(srcRoot.Length + 1)))
            .ToList();
        var refs = new List<MetadataReference>();
        foreach (var d in new[] { "/usr/lib/dotnet/packs/Microsoft.NETCore.App.Ref/10.0.15/ref/net10.0", "/usr/lib/dotnet/packs/Microsoft.AspNetCore.App.Ref/10.0.15/ref/net10.0" })
            refs.AddRange(Directory.EnumerateFiles(d, "*.dll").Select(f => MetadataReference.CreateFromFile(f)));
        var fw = new HashSet<string>(refs.Select(r => Path.GetFileName(((PortableExecutableReference)r).FilePath)));
        foreach (var f in Directory.EnumerateFiles(binDir, "*.dll"))
        {
            var n = Path.GetFileName(f);
            if (n.StartsWith("ACE.Server") || fw.Contains(n)) continue;
            try { System.Reflection.AssemblyName.GetAssemblyName(f); refs.Add(MetadataReference.CreateFromFile(f)); } catch { }
        }
        var comp = CSharpCompilation.Create("ACE.Server", trees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Disable));
        var errs = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Console.WriteLine($"trees={trees.Count} compile errors={errs.Count}");
        foreach (var e in errs.Take(10)) Console.WriteLine("  " + e);

        var afr = comp.GetTypeByMetadataName("ACE.Server.Physics.Animation.AFrame");
        var pos = comp.GetTypeByMetadataName("ACE.Server.Physics.Common.Position");
        Scan(comp, afr, new[] { "Combine:inst", "InterpolateOrigin", "InterpolateRotation", "GRotate", "Rotate", "Subtract", "set_heading", "set_position", "set_rotate", "set_vector_heading" }, Path.Combine(outDir, "scan_AFrame.txt"));
        Scan(comp, pos, new[] { "Subtract", "add_offset", "adjust_to_outside" }, Path.Combine(outDir, "scan_Position.txt"));
    }

    static bool IsT(ITypeSymbol t, INamedTypeSymbol T) => t != null && SymbolEqualityComparer.Default.Equals(t, T);

    static void Scan(CSharpCompilation comp, INamedTypeSymbol T, string[] mutators, string outFile)
    {
        var lines = new List<(string cat, string loc, string text)>();
        var mut = new HashSet<string>(mutators.Select(m => m.Split(':')[0]));

        // pass 1: which methods mutate a by-value parameter of type T (directly)?
        var mutParamMethods = new Dictionary<IMethodSymbol, HashSet<int>>(SymbolEqualityComparer.Default);
        var storesParam = new List<string>();

        foreach (var tree in comp.SyntaxTrees)
        {
            var model = comp.GetSemanticModel(tree);
            var root = tree.GetRoot();
            string Loc(SyntaxNode n) => $"{tree.FilePath}:{n.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";
            string Txt(SyntaxNode n) { var s = n.ToString().Replace("\r", "").Replace("\n", " "); return s.Length > 150 ? s.Substring(0, 150) + "..." : s; }

            // innermost T-typed receiver of a mutated member chain
            ExpressionSyntax TReceiver(ExpressionSyntax e)
            {
                while (e is MemberAccessExpressionSyntax ma)
                {
                    var rt = model.GetTypeInfo(ma.Expression).Type;
                    if (IsT(rt, T)) return ma.Expression;
                    e = ma.Expression;
                }
                return null;
            }
            string RootKind(ExpressionSyntax r)
            {
                if (r == null) return "?";
                if (r is ThisExpressionSyntax) return "this";
                var s = model.GetSymbolInfo(r).Symbol;
                switch (s)
                {
                    case ILocalSymbol: return "LOCAL";
                    case IParameterSymbol p: return p.RefKind == RefKind.None ? "PARAM(by-value)" : "PARAM(" + p.RefKind + ")";
                    case IFieldSymbol: return "field";
                    case IPropertySymbol: return "PROPERTY";
                    case IMethodSymbol: return "INVOCATION";
                }
                if (r is ElementAccessExpressionSyntax) return "ELEMENT";
                if (r is InvocationExpressionSyntax) return "INVOCATION";
                if (r is ObjectCreationExpressionSyntax) return "new";
                return r.Kind().ToString();
            }

            foreach (var n in root.DescendantNodes())
            {
                // ---- mutation via assignment / ++ -- ----
                ExpressionSyntax target = n switch
                {
                    AssignmentExpressionSyntax a => a.Left,
                    PrefixUnaryExpressionSyntax p when p.IsKind(SyntaxKind.PreIncrementExpression) || p.IsKind(SyntaxKind.PreDecrementExpression) => p.Operand,
                    PostfixUnaryExpressionSyntax p when p.IsKind(SyntaxKind.PostIncrementExpression) || p.IsKind(SyntaxKind.PostDecrementExpression) => p.Operand,
                    _ => null
                };
                if (target != null)
                {
                    var recv = TReceiver(target);
                    if (recv != null)
                    {
                        var rk = RootKind(recv);
                        lines.Add(("MUTATE-FIELD via " + rk, Loc(n), Txt(n)));
                        RecordParamMutation(model, recv, n, mutParamMethods);
                    }
                    // ---- alias creation: storage = <existing T reference> ----
                    if (n is AssignmentExpressionSyntax asg && asg.IsKind(SyntaxKind.SimpleAssignmentExpression) && IsT(model.GetTypeInfo(asg.Left).Type, T))
                        Classify(asg.Right, "ASSIGN " + RootKind(asg.Left), n);
                }
                if (n is VariableDeclaratorSyntax vd && vd.Initializer != null)
                {
                    var sym = model.GetDeclaredSymbol(vd) as ILocalSymbol;
                    if (sym != null && IsT(sym.Type, T)) Classify(vd.Initializer.Value, "LOCAL-INIT", vd);
                    var fsym = model.GetDeclaredSymbol(vd) as IFieldSymbol;
                    if (fsym != null && IsT(fsym.Type, T)) Classify(vd.Initializer.Value, "FIELD-INIT", vd);
                }
                // ---- mutator method calls ----
                if (n is InvocationExpressionSyntax inv)
                {
                    var ms = model.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                    if (ms != null)
                    {
                        if (inv.Expression is MemberAccessExpressionSyntax ma2 && IsT(model.GetTypeInfo(ma2.Expression).Type, T) && !ms.IsStatic && mut.Contains(ms.Name)
                            && SymbolEqualityComparer.Default.Equals(ms.ContainingType, T))
                        {
                            lines.Add(("MUTATE-CALL via " + RootKind(ma2.Expression), Loc(n), Txt(n)));
                            RecordParamMutation(model, ma2.Expression, n, mutParamMethods);
                        }
                        // argument passing of existing references
                        for (int i = 0; i < inv.ArgumentList.Arguments.Count; i++)
                        {
                            var arg = inv.ArgumentList.Arguments[i];
                            if (!IsT(model.GetTypeInfo(arg.Expression).Type, T)) continue;
                            if (arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)) { lines.Add(("ARG-BY-REF", Loc(arg), Txt(inv))); continue; }
                            var k = ExprKind(model, arg.Expression);
                            if (k != "fresh" && k != "null")
                                lines.Add(("ARG-SHARED(" + k + ") -> " + ms.ContainingType.Name + "." + ms.Name, Loc(arg), Txt(inv)));
                        }
                    }
                }
                if (n is ObjectCreationExpressionSyntax oc && oc.ArgumentList != null)
                {
                    var ms = model.GetSymbolInfo(oc).Symbol as IMethodSymbol;
                    foreach (var arg in oc.ArgumentList.Arguments)
                    {
                        if (!IsT(model.GetTypeInfo(arg.Expression).Type, T)) continue;
                        var k = ExprKind(model, arg.Expression);
                        if (k != "fresh" && k != "null")
                            lines.Add(("CTOR-ARG-SHARED(" + k + ") -> new " + ms?.ContainingType.Name, Loc(arg), Txt(oc)));
                    }
                }
                // ---- null / identity ----
                if (n is BinaryExpressionSyntax be && (be.IsKind(SyntaxKind.EqualsExpression) || be.IsKind(SyntaxKind.NotEqualsExpression) || be.IsKind(SyntaxKind.CoalesceExpression)))
                {
                    bool l = IsT(model.GetTypeInfo(be.Left).Type, T), r = IsT(model.GetTypeInfo(be.Right).Type, T);
                    if (l || r)
                    {
                        var isNull = be.Left.IsKind(SyntaxKind.NullLiteralExpression) || be.Right.IsKind(SyntaxKind.NullLiteralExpression);
                        lines.Add((be.IsKind(SyntaxKind.CoalesceExpression) ? "NULL-COALESCE" : isNull ? "NULL-CHECK" : (l && r ? "REFERENCE-EQUALITY(==)" : "EQ-OTHER"), Loc(n), Txt(n)));
                    }
                }
                if (n is IsPatternExpressionSyntax ip && IsT(model.GetTypeInfo(ip.Expression).Type, T))
                    lines.Add(("NULL-CHECK(is)", Loc(n), Txt(n)));
                if (n is ConditionalAccessExpressionSyntax ca && IsT(model.GetTypeInfo(ca.Expression).Type, T))
                    lines.Add(("NULL-CONDITIONAL(?.)", Loc(n), Txt(n)));
                if (n is ReturnStatementSyntax rs && rs.Expression != null && IsT(model.GetTypeInfo(rs.Expression).Type, T))
                {
                    var k = ExprKind(model, rs.Expression);
                    if (k == "field" || k == "element") lines.Add(("RETURN-SHARED(" + k + ")", Loc(n), Txt(n)));
                }
                if (n is ArrowExpressionClauseSyntax ae && IsT(model.GetTypeInfo(ae.Expression).Type, T))
                {
                    var k = ExprKind(model, ae.Expression);
                    if (k == "field" || k == "element") lines.Add(("RETURN-SHARED(" + k + ")", Loc(n), Txt(n)));
                }
                if (n is ArgumentSyntax a2 && a2.Parent?.Parent is InvocationExpressionSyntax rinv && model.GetSymbolInfo(rinv).Symbol is IMethodSymbol rm && rm.Name == "ReferenceEquals"
                    && IsT(model.GetTypeInfo(a2.Expression).Type, T))
                    lines.Add(("REFERENCE-EQUALS()", Loc(n), Txt(rinv)));
                if (n is LockStatementSyntax ls && IsT(model.GetTypeInfo(ls.Expression).Type, T))
                    lines.Add(("LOCK-ON", Loc(n), Txt(n)));

                void Classify(ExpressionSyntax rhs, string what, SyntaxNode at)
                {
                    var k = ExprKind(model, rhs);
                    if (k == "fresh") return;
                    lines.Add(((k == "null" ? "NULL-ASSIGN " : "ALIAS(" + k + ") ") + what, Loc(at), Txt(at)));
                }
            }
        }

        // second pass: calls to methods that mutate a by-value T parameter
        foreach (var tree in comp.SyntaxTrees)
        {
            var model = comp.GetSemanticModel(tree);
            foreach (var inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(inv).Symbol is IMethodSymbol ms && mutParamMethods.TryGetValue(ms.OriginalDefinition, out var idx))
                    foreach (var i in idx)
                        if (i < inv.ArgumentList.Arguments.Count)
                            lines.Add(($"CALLS-PARAM-MUTATOR {ms.ContainingType.Name}.{ms.Name}(arg{i}) arg={ExprKind(model, inv.ArgumentList.Arguments[i].Expression)}",
                                $"{tree.FilePath}:{inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1}", inv.ToString().Replace("\n", " ")));
            }
        }

        using var w = new StreamWriter(outFile);
        w.WriteLine($"Type {T.ToDisplayString()}  total records {lines.Count}");
        w.WriteLine("---- counts by category ----");
        foreach (var g in lines.GroupBy(l => Norm(l.cat)).OrderBy(g => g.Key)) w.WriteLine($"{g.Count(),5}  {g.Key}");
        w.WriteLine("---- by-value-parameter mutators ----");
        foreach (var kv in mutParamMethods) w.WriteLine($"  {kv.Key.ContainingType.Name}.{kv.Key.Name} params[{string.Join(",", kv.Value)}]");
        w.WriteLine("---- detail ----");
        foreach (var l in lines.OrderBy(l => l.cat).ThenBy(l => l.loc)) w.WriteLine($"{l.cat} | {l.loc} | {l.text}");
        Console.WriteLine($"{T.Name}: wrote {lines.Count} records to {outFile}");
    }

    static string Norm(string c) { var i = c.IndexOf(" -> "); return i > 0 ? c.Substring(0, i) : c; }

    static void RecordParamMutation(SemanticModel model, ExpressionSyntax recv, SyntaxNode at, Dictionary<IMethodSymbol, HashSet<int>> map)
    {
        // walk to root identifier
        var e = recv;
        while (e is MemberAccessExpressionSyntax ma) e = ma.Expression;
        if (model.GetSymbolInfo(recv).Symbol is IParameterSymbol p && p.RefKind == RefKind.None && p.ContainingSymbol is IMethodSymbol m)
        {
            if (!map.TryGetValue(m.OriginalDefinition, out var s)) map[m.OriginalDefinition] = s = new HashSet<int>();
            s.Add(p.Ordinal);
        }
    }

    static string ExprKind(SemanticModel model, ExpressionSyntax e)
    {
        while (e is ParenthesizedExpressionSyntax pe) e = pe.Expression;
        switch (e)
        {
            case ObjectCreationExpressionSyntax: case ImplicitObjectCreationExpressionSyntax: return "fresh";
            case LiteralExpressionSyntax l when l.IsKind(SyntaxKind.NullLiteralExpression) || l.IsKind(SyntaxKind.DefaultLiteralExpression): return "null";
            case ConditionalExpressionSyntax c:
                var a = ExprKind(model, c.WhenTrue); var b = ExprKind(model, c.WhenFalse);
                return a == b ? a : $"cond[{a}|{b}]";
            case InvocationExpressionSyntax inv:
                var ms = model.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                return "call:" + (ms?.ContainingType.Name + "." + ms?.Name);
            case ElementAccessExpressionSyntax: return "element";
        }
        var s = model.GetSymbolInfo(e).Symbol;
        return s switch
        {
            ILocalSymbol => "local",
            IParameterSymbol => "param",
            IFieldSymbol => "field",
            IPropertySymbol => "property",
            _ => e.Kind().ToString()
        };
    }
}
