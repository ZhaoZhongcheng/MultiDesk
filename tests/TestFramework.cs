using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace MultiDesk.Tests
{
    /// <summary>
    /// Marks a public static parameterless method as a test case.
    /// Zero external dependencies: the project has no NuGet packages installed.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class TestAttribute : Attribute
    {
        private readonly string _name;

        public TestAttribute(string name)
        {
            _name = name;
        }

        public string Name
        {
            get { return _name; }
        }
    }

    /// <summary>Thrown by the Assert helpers when a check fails.</summary>
    public sealed class AssertFailedException : Exception
    {
        public AssertFailedException(string message)
            : base(message)
        {
        }
    }

    /// <summary>Minimal assertion helpers (no third-party test framework is available).</summary>
    public static class Assert
    {
        public static void Fail(string message)
        {
            throw new AssertFailedException(message);
        }

        public static void True(bool condition, string message)
        {
            if (!condition) Fail(message);
        }

        public static void False(bool condition, string message)
        {
            if (condition) Fail(message);
        }

        public static void Null(object value, string message)
        {
            if (value != null) Fail(message + " (expected null, actual " + Describe(value) + ")");
        }

        public static void NotNull(object value, string message)
        {
            if (value == null) Fail(message + " (expected non-null)");
        }

        public static void Same(object expected, object actual, string message)
        {
            if (!object.ReferenceEquals(expected, actual))
            {
                Fail(message + " (expected the same instance)");
            }
        }

        public static void NotSame(object expected, object actual, string message)
        {
            if (object.ReferenceEquals(expected, actual))
            {
                Fail(message + " (expected different instances)");
            }
        }

        public static void Equal(object expected, object actual, string message)
        {
            if (!object.Equals(expected, actual))
            {
                Fail(message + " (expected " + Describe(expected) + ", actual " + Describe(actual) + ")");
            }
        }

        public static void NotEqual(object notExpected, object actual, string message)
        {
            if (object.Equals(notExpected, actual))
            {
                Fail(message + " (value must differ from " + Describe(notExpected) + ")");
            }
        }

        public static void Near(double expected, double actual, double tolerance, string message)
        {
            if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
            {
                Fail(message + " (expected " + FormatDouble(expected) + " +/- " + FormatDouble(tolerance)
                    + ", actual " + FormatDouble(actual) + ")");
            }
        }

        public static void IsNaN(double value, string message)
        {
            if (!double.IsNaN(value))
            {
                Fail(message + " (expected NaN, actual " + FormatDouble(value) + ")");
            }
        }

        public static void NotNaN(double value, string message)
        {
            if (double.IsNaN(value)) Fail(message + " (value must not be NaN)");
        }

        public static void InRange(int value, int minInclusive, int maxInclusive, string message)
        {
            if (value < minInclusive || value > maxInclusive)
            {
                Fail(message + " (expected " + minInclusive + ".." + maxInclusive + ", actual " + value + ")");
            }
        }

        public static void InRange(double value, double minInclusive, double maxInclusive, string message)
        {
            if (double.IsNaN(value) || value < minInclusive || value > maxInclusive)
            {
                Fail(message + " (expected " + FormatDouble(minInclusive) + ".." + FormatDouble(maxInclusive)
                    + ", actual " + FormatDouble(value) + ")");
            }
        }

        public static void Greater(double value, double lowerBound, string message)
        {
            if (!(value > lowerBound))
            {
                Fail(message + " (expected > " + FormatDouble(lowerBound) + ", actual " + FormatDouble(value) + ")");
            }
        }

        public static void Contains(string text, string fragment, string message)
        {
            if (text == null || text.IndexOf(fragment, StringComparison.Ordinal) < 0)
            {
                Fail(message + " (expected " + Describe(text) + " to contain " + Describe(fragment) + ")");
            }
        }

        private static string Describe(object value)
        {
            if (value == null) return "<null>";
            string s = value as string;
            if (s != null) return "\"" + s + "\"";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string FormatDouble(double value)
        {
            if (double.IsNaN(value)) return "NaN";
            if (double.IsPositiveInfinity(value)) return "Infinity";
            if (double.IsNegativeInfinity(value)) return "-Infinity";
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    internal sealed class TestEntry
    {
        public string Name;
        public MethodInfo Method;
    }

    /// <summary>
    /// Discovers [Test] methods via reflection, runs them in a stable order and prints
    /// PASS/FAIL per case plus a Total/Passed/Failed summary. Exit code is 1 on failure.
    /// </summary>
    public static class TestRunner
    {
        public static int Run()
        {
            Console.WriteLine("==========================================================");
            Console.WriteLine("MultiDesk unit test runner");
            Console.WriteLine("Runtime : .NET " + Environment.Version.ToString()
                + " (" + (IntPtr.Size == 8 ? "x64" : "x86") + ")");
            Console.WriteLine("OS      : " + Environment.OSVersion.VersionString);
            Console.WriteLine("==========================================================");

            List<TestEntry> tests = DiscoverTests();
            if (tests.Count == 0)
            {
                Console.WriteLine("FAILED: no test cases discovered");
                return 2;
            }

            int passed = 0;
            List<string> failed = new List<string>();
            for (int i = 0; i < tests.Count; i++)
            {
                TestEntry entry = tests[i];
                try
                {
                    entry.Method.Invoke(null, null);
                    passed++;
                    Console.WriteLine("[PASS] " + entry.Name);
                }
                catch (Exception ex)
                {
                    Exception real = ex;
                    TargetInvocationException wrapped = ex as TargetInvocationException;
                    if (wrapped != null && wrapped.InnerException != null)
                    {
                        real = wrapped.InnerException;
                    }
                    failed.Add(entry.Name);
                    Console.WriteLine("[FAIL] " + entry.Name);
                    PrintFailure(real);
                }
            }

            Console.WriteLine("----------------------------------------------------------");
            Console.WriteLine("Total: " + tests.Count + "  Passed: " + passed + "  Failed: " + failed.Count);
            if (failed.Count > 0)
            {
                Console.WriteLine("Failed cases:");
                for (int i = 0; i < failed.Count; i++)
                {
                    Console.WriteLine("  - " + failed[i]);
                }
                Console.WriteLine("RESULT: FAILED");
                return 1;
            }
            Console.WriteLine("RESULT: PASSED");
            return 0;
        }

        private static void PrintFailure(Exception ex)
        {
            if (ex == null) return;
            AssertFailedException assertion = ex as AssertFailedException;
            if (assertion != null)
            {
                Console.WriteLine("       assertion failed: " + assertion.Message);
            }
            else
            {
                Console.WriteLine("       " + ex.GetType().Name + ": " + ex.Message);
            }
            Console.WriteLine("       " + FirstUserFrame(ex.StackTrace));
        }

        private static string FirstUserFrame(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace)) return "(no stack trace)";
            string[] lines = stackTrace.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> frames = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("at System.Reflection", StringComparison.Ordinal)) continue;
                if (line.StartsWith("at System.RuntimeMethodHandle", StringComparison.Ordinal)) continue;
                frames.Add(line);
                if (frames.Count >= 3) break;
            }
            if (frames.Count == 0) return lines[0].Trim();
            return string.Join(" <- ", frames.ToArray());
        }

        private static List<TestEntry> DiscoverTests()
        {
            List<TestEntry> tests = new List<TestEntry>();
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            Assembly assembly = typeof(TestRunner).Assembly;
            Type[] types = assembly.GetTypes();
            for (int i = 0; i < types.Length; i++)
            {
                Type type = types[i];
                if (type == null || !type.IsClass) continue;
                MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Static | BindingFlags.DeclaredOnly);
                for (int j = 0; j < methods.Length; j++)
                {
                    MethodInfo method = methods[j];
                    object[] attributes = method.GetCustomAttributes(typeof(TestAttribute), false);
                    if (attributes == null || attributes.Length == 0) continue;
                    TestAttribute attribute = (TestAttribute)attributes[0];
                    if (method.GetParameters().Length != 0 || method.ReturnType != typeof(void))
                    {
                        Console.WriteLine("WARNING: ignored invalid test method " + type.Name + "." + method.Name);
                        continue;
                    }
                    string displayName = attribute.Name;
                    if (string.IsNullOrEmpty(displayName)) displayName = type.Name + "." + method.Name;
                    if (!names.Add(displayName))
                    {
                        displayName = displayName + " [" + type.Name + "." + method.Name + "]";
                        names.Add(displayName);
                    }
                    TestEntry entry = new TestEntry();
                    entry.Name = displayName;
                    entry.Method = method;
                    tests.Add(entry);
                }
            }
            tests.Sort(delegate(TestEntry a, TestEntry b) { return string.Compare(a.Name, b.Name, StringComparison.Ordinal); });
            return tests;
        }
    }
}
