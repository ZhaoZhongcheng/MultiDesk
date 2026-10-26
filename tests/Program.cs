using System;

namespace MultiDesk.Tests
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            return TestRunner.Run();
        }
    }
}
