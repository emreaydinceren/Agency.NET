using System;

namespace Sample.Large;

public class LargeWorker
{
    public void Run()
    {
        int counter = 0;
        counter += 1;
        counter += 2;
        counter += 3;
        if (counter > 0)
        {
            Console.WriteLine(counter);
        }

        counter += 4;
        counter += 5;
        Console.WriteLine(counter);
    }
}
