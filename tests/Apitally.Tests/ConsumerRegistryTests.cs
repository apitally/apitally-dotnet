namespace Apitally.Tests;

using Xunit;
using Apitally.Models;

public class ConsumerRegistryTests
{
    private readonly ConsumerRegistry _consumerRegistry;

    public ConsumerRegistryTests()
    {
        _consumerRegistry = new ConsumerRegistry();
    }

    [Fact]
    public void AddOrUpdateConsumer_ShouldUpdateConsumerCorrectly()
    {
        var consumer = ConsumerRegistry.ConsumerFromObject("test");
        _consumerRegistry.AddOrUpdateConsumer(consumer);
        consumer = new Consumer { Identifier = "test", Name = "Test 1", Group = "Group 1" };
        _consumerRegistry.AddOrUpdateConsumer(consumer);
        consumer = new Consumer { Identifier = "test", Group = "Group 2" };
        _consumerRegistry.AddOrUpdateConsumer(consumer);
        consumer = new Consumer { Identifier = "test", Name = "Test 2" };
        _consumerRegistry.AddOrUpdateConsumer(consumer);

        var consumers = _consumerRegistry.GetAndResetConsumers();
        Assert.Single(consumers);
        Assert.Equal("test", consumers[0].Identifier);
        Assert.Equal("Test 2", consumers[0].Name);
        Assert.Equal("Group 2", consumers[0].Group);

        consumer = new Consumer { Identifier = "test", Name = "Test 2", Group = "Group 2" };
        _consumerRegistry.AddOrUpdateConsumer(consumer);
        consumers = _consumerRegistry.GetAndResetConsumers();
        Assert.Empty(consumers);
    }

    [Fact]
    public void ConsumerFromObject_ShouldHandleVariousInputs()
    {
        var consumer = ConsumerRegistry.ConsumerFromObject("test");
        Assert.Equal("test", consumer?.Identifier);
        Assert.Null(consumer?.Name);
        Assert.Null(consumer?.Group);

        consumer = ConsumerRegistry.ConsumerFromObject(new Consumer { Identifier = "test", Name = "Test 1", Group = "Group 1" });
        Assert.Equal("test", consumer?.Identifier);
        Assert.Equal("Test 1", consumer?.Name);
        Assert.Equal("Group 1", consumer?.Group);

        consumer = ConsumerRegistry.ConsumerFromObject(123);
        Assert.Equal("123", consumer?.Identifier);

        consumer = ConsumerRegistry.ConsumerFromObject(1.23);
        Assert.Null(consumer);

        consumer = ConsumerRegistry.ConsumerFromObject("");
        Assert.Null(consumer);

        consumer = ConsumerRegistry.ConsumerFromObject(null);
        Assert.Null(consumer);
    }
}
