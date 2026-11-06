using System.Reflection;

namespace NodeWatch.Tests;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class TestAttribute : Attribute;

internal sealed class AssertException(string message) : Exception(message);

internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new AssertException(message);
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertException($"{what}: várt '{expected}', kapott '{actual}'");
    }

    public static void Throws<TException>(Action action, string what) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new AssertException($"{what}: {typeof(TException).Name} kivételt vártam");
    }
}

internal static class Runner
{
    public static async Task<int> Main(string[] args)
    {
        NodeWatch.Log.SyslogPrefix = false;
        NodeWatch.Log.Verbose = args.Contains("--verbose");
        var filter = args.FirstOrDefault(a => !a.StartsWith('-'));
        var tests = typeof(Runner).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
            .Where(m => filter is null || $"{m.DeclaringType!.Name}.{m.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.DeclaringType!.Name).ThenBy(m => m.Name)
            .ToList();
        var failed = 0;
        foreach (var test in tests)
        {
            var name = $"{test.DeclaringType!.Name}.{test.Name}";
            try
            {
                var result = test.Invoke(null, null);
                if (result is Task task) await task;
                Console.WriteLine($"  OK    {name}");
            }
            catch (Exception e)
            {
                failed++;
                var inner = e is TargetInvocationException { InnerException: { } i } ? i : e;
                Console.WriteLine($"  HIBA  {name}: {inner.Message}");
                if (inner is not AssertException) Console.WriteLine(inner.StackTrace);
            }
        }
        Console.WriteLine($"\n{tests.Count - failed}/{tests.Count} teszt sikeres.");
        return failed == 0 ? 0 : 1;
    }
}
