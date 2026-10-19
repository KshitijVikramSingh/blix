using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Blix.Demos.VulkanSponza;

// Who holds the managed heap (BLIX_HEAP_HOLDERS=1): every array reachable from the loop's fields, its bytes charged to the
// field it was first reached through. The heap stood at 5-6 GB live after the cook on a 16 GB machine whose GPU is the
// same RAM (2026-10-19, swap 5.4 GB in use); the GC's own dump is lossy on a heap that large and named only types.
internal sealed partial class SponzaLoop
{
    private static readonly ConcurrentDictionary<Type, int> heapElementSizes = new();

    private void LogHeapHolders()
    {
        if (Environment.GetEnvironmentVariable("BLIX_HEAP_HOLDERS") != "1") return;
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance) { this };
        var byField = new List<(string Name, long Bytes)>();
        foreach (var f in AllFields(GetType()))
        {
            if (f.FieldType.IsValueType && !f.FieldType.IsGenericType) continue;
            long bytes = 0;
            var stack = new Stack<(object O, int Depth)>();
            var root = f.GetValue(this);
            if (root is not null && seen.Add(root)) stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (o, depth) = stack.Pop();
                bytes += Walk(o, depth, seen, stack);
            }
            if (bytes >= 1 << 20) byField.Add((f.Name, bytes));
        }
        // Then what the loop doesn't reach: the Blix assemblies' statics (caches, loaders, the asset store).
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name?.StartsWith("Blix", StringComparison.Ordinal) == true))
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t is not null).ToArray()!; }
            foreach (var t in types.Where(t => !t.ContainsGenericParameters))
                foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.IsLiteral || f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType.IsPointer) continue;
                    object? root;
                    try { root = f.GetValue(null); } catch { continue; }
                    long bytes = 0;
                    var stack = new Stack<(object O, int Depth)>();
                    if (root is not null && seen.Add(root)) stack.Push((root, 0));
                    while (stack.Count > 0) { var (o, depth) = stack.Pop(); bytes += Walk(o, depth, seen, stack); }
                    if (bytes >= 1 << 20) byField.Add(($"static {t.Name}.{f.Name}", bytes));
                }
        }
        foreach (var (name, b) in byField.OrderByDescending(x => x.Bytes).Take(30))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {b / 1048576.0,7:N0} MB  held by {name}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"    held by the loop and the statics: {byField.Sum(x => x.Bytes) / 1048576.0:N0} MB of a {GC.GetTotalMemory(false) / 1048576.0:N0} MB heap."));
    }

    private static IEnumerable<FieldInfo> AllFields(Type t)
    {
        for (; t is not null && t != typeof(object); t = t.BaseType!)
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                yield return f;
    }

    // An object's arrays' bytes; the objects it references go on the stack (each charged once, to the first field reaching it).
    private static long Walk(object o, int depth, HashSet<object> seen, Stack<(object, int)> stack)
    {
        if (o is string || o is Type || o is MemberInfo || depth > 16) return 0;
        // A delegate's closure (a task's lambda, a stored callback) holds what it captured.
        if (o is Delegate d)
        {
            foreach (var each in d.GetInvocationList()) Push(each.Target, depth, seen, stack);
            return 0;
        }
        var t = o.GetType();
        if (o is Array a)
        {
            var e = t.GetElementType()!;
            if (e.IsValueType)
            {
                long bytes = a.LongLength * heapElementSizes.GetOrAdd(e, ElementSize);
                if (ContainsReferences(e))
                    foreach (var item in a) Push(item, depth, seen, stack);
                return bytes;
            }
            foreach (var item in a) Push(item, depth, seen, stack);
            return a.LongLength * 8;
        }
        if (t.IsPointer || o is Thread) return 0;
        foreach (var f in AllFields(t))
            if (!f.FieldType.IsPrimitive && !f.FieldType.IsPointer && !f.FieldType.IsEnum)
                Push(f.GetValue(o), depth, seen, stack);
        return 0;
    }

    private static void Push(object? v, int depth, HashSet<object> seen, Stack<(object, int)> stack)
    {
        if (v is null) return;
        if (v.GetType().IsValueType)
        {
            // A struct's own references (a tuple of arrays, a record struct).
            foreach (var f in AllFields(v.GetType()))
                if (!f.FieldType.IsPrimitive && !f.FieldType.IsPointer && !f.FieldType.IsEnum)
                    Push(f.GetValue(v), depth, seen, stack);
            return;
        }
        if (seen.Add(v)) stack.Push((v, depth + 1));
    }

    private static bool ContainsReferences(Type e) =>
        (bool)typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.IsReferenceOrContainsReferences))!.MakeGenericMethod(e).Invoke(null, null)!;

    private static int ElementSize(Type e) =>
        (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(e).Invoke(null, null)!;
}
