namespace UnitTests;

using Utility;
public class Tests
{
    private CircularLinkedList<int> _list { get; set; } = null!;
    [SetUp]
    public void Setup()
    {
        _list = new CircularLinkedList<int>();
    }

    [Test]
    public void Test1()
    {
        Assert.Pass();
    }
}