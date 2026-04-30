using System;
using System.Threading.Tasks;

namespace Sample.Simple
{
    public class Worker
    {
        public void Run()
        {
            Console.WriteLine("run");
        }

        public Task StopAsync()
        {
            return Task.CompletedTask;
        }
    }
}
