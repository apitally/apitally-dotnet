using System.Collections.Concurrent;
using Apitally.Export;
using Apitally.Logging;
using Apitally.Requests;
using Apitally.Tests.Support;

namespace Apitally.Tests.Requests;

public class ConsumerUpdatesTests
{
    [Fact]
    public void PatchesAreNormalizedBeforeUse()
    {
        var consumer = ConsumerUpdates.Apply(
            null,
            "  " + new string('c', 130) + " ",
            " Acme ",
            new string('g', 70),
            new Dictionary<string, string?>
            {
                [" plan "] = " pro ",
                ["region"] = " ",
                ["deleted"] = null,
                [""] = "ignored",
                [new string('k', 65)] = "ignored",
                ["too-long"] = new string('v', 1_025),
                ["nul"] = "a\0b",
            }
        )!;

        Assert.Equal(new string('c', 128), consumer.Identifier);
        Assert.Equal("Acme", consumer.Name);
        Assert.Equal(new string('g', 64), consumer.Group);
        Assert.Equal(
            [
                new KeyValuePair<string, string?>("plan", "pro"),
                new KeyValuePair<string, string?>("region", null),
                new KeyValuePair<string, string?>("deleted", null),
            ],
            consumer.Attributes
        );
    }

    [Fact]
    public void InvalidIdentifierIsIgnored()
    {
        Assert.Null(ConsumerUpdates.Apply(null, " ", "Name", null, null));
        Assert.Null(ConsumerUpdates.Apply(null, "a\0b", "Name", null, null));
    }

    [Fact]
    public void RepeatedCallsMergeForTheSameIdentifierAndKeepTenAttributes()
    {
        var consumer = ConsumerUpdates.Apply(null, "acme", "Acme", null, Attributes(0, 8));
        consumer = ConsumerUpdates.Apply(consumer, "acme", null, "enterprise", Attributes(6, 12));
        var other = ConsumerUpdates.Apply(consumer, "other", null, null, null)!;

        Assert.Equal("Acme", consumer!.Name);
        Assert.Equal("enterprise", consumer.Group);
        Assert.Equal(
            Enumerable.Range(0, 10).Select(i => $"key{i}"),
            consumer.Attributes.Select(a => a.Key)
        );
        Assert.Null(other.Name);
        Assert.Empty(other.Attributes);
    }

    [Fact]
    public void UnchangedPayloadsAreSuppressedIndependentlyOfOrder()
    {
        var (updates, events) = Create();

        updates.EmitIfChanged(Consumer("acme", ("a", "1"), ("b", null)));
        updates.EmitIfChanged(Consumer("acme", ("b", ""), ("a", "1")));
        updates.EmitIfChanged(Consumer("acme", ("a", "2")));
        updates.EmitIfChanged(new RequestConsumer("no-metadata"));

        var emitted = events();
        Assert.Equal(2, emitted.Count);
        Assert.Equal("apitally.consumer.update", emitted[0].EventName);
        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["identifier"] = "acme",
                ["attributes"] = new Dictionary<string, object?> { ["a"] = "1", ["b"] = null },
            },
            emitted[0].EventBody
        );
    }

    [Fact]
    public void CacheEvictsLeastRecentlyUsedIdentifiers()
    {
        var (updates, events) = Create();

        updates.EmitIfChanged(Consumer("first", ("a", "1")));
        for (var i = 0; i < ConsumerUpdates.MaxCachedConsumers; i++)
            updates.EmitIfChanged(Consumer($"consumer-{i}", ("a", "1")));
        updates.EmitIfChanged(Consumer("first", ("a", "1")));
        updates.EmitIfChanged(Consumer("consumer-9999", ("a", "1")));

        Assert.Equal(ConsumerUpdates.MaxCachedConsumers + 2, events().Count);
    }

    private static (ConsumerUpdates, Func<List<LogSnapshot>>) Create()
    {
        var emitted = new ConcurrentQueue<LogSnapshot>();
        var processor = new ApitallyBatchProcessor<LogSnapshot>(logs =>
        {
            foreach (var log in logs)
                emitted.Enqueue(log);
        });
        var updates = new ConsumerUpdates(new InternalEvents(processor, TimeProvider.System));
        return (
            updates,
            () =>
            {
                processor.Shutdown();
                return [.. emitted];
            }
        );
    }

    private static RequestConsumer Consumer(
        string identifier,
        params (string Key, string? Value)[] attributes
    ) =>
        ConsumerUpdates.Apply(
            null,
            identifier,
            null,
            null,
            attributes.ToDictionary(attribute => attribute.Key, attribute => attribute.Value)
        )!;

    private static Dictionary<string, string?> Attributes(int from, int to) =>
        Enumerable.Range(from, to - from).ToDictionary(i => $"key{i}", i => (string?)i.ToString());
}
