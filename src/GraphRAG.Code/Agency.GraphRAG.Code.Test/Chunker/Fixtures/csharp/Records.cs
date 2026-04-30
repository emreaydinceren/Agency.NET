namespace Sample.Models;

public record class Person(string Name, int Age)
{
    public string Display => $"{Name}:{Age}";
}
