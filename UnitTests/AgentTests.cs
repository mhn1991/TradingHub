namespace UnitTests;
using Agent;

public class AgentTests
{
    private Agent _agent;
    [SetUp]
    public void Setup()
    {
        _agent = new Agent();   
        
    }

    [Test]
    public async Task TestAgent()
    {
        //await _agent.InitAsync();
        await _agent.Run();
    }
    
}