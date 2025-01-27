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
        _list.Add(10);
        _list.Add(20);
        Assert.That(2, Is.EqualTo(_list.Size)); 
        Assert.That(10, Is.EqualTo(_list.GetCurrent().Data));
        Assert.That(20, Is.EqualTo(_list.GetNext().Data));
        _list.MoveNext();
        Assert.That(20, Is.EqualTo(_list.GetCurrent().Data));
    }
}