using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Apitally.Logging;

namespace Apitally.Requests;

// A request's consumer: the identifier for span and metric attribution, plus the normalized
// metadata patch submitted for a consumer-update event.
internal sealed class RequestConsumer(string identifier)
{
    public const int MaxAttributesPerUpdate = 10;

    public string Identifier { get; } = identifier;
    public string? Name { get; set; }
    public string? Group { get; set; }

    // Insertion-ordered partial patch; a null value deletes the attribute.
    public List<KeyValuePair<string, string?>> Attributes { get; } = [];

    public bool HasMetadata => Name is not null || Group is not null || Attributes.Count > 0;
}

// Normalizes consumer patches and suppresses unchanged updates with a bounded cache of the
// last submitted payload hash per identifier.
internal sealed class ConsumerUpdates(InternalEvents events)
{
    public const int MaxCachedConsumers = 10_000;
    private const int MaxIdentifierLength = 128;
    private const int MaxNameLength = 64;
    private const int MaxAttributeKeyLength = 64;
    private const int MaxAttributeValueLength = 1_024;

    private readonly object sync = new();
    private readonly Dictionary<string, LinkedListNode<(string Identifier, UInt128 Hash)>> hashes =
    [];
    private readonly LinkedList<(string Identifier, UInt128 Hash)> leastRecentlyUsed = new();

    private static string? NormalizeIdentifier(string? identifier) =>
        Normalize(identifier, MaxIdentifierLength);

    // Merges a submitted patch into the request's consumer. A different identifier starts over.
    public static RequestConsumer? Apply(
        RequestConsumer? current,
        string identifier,
        string? name,
        string? group,
        IReadOnlyDictionary<string, string?>? attributes
    )
    {
        if (NormalizeIdentifier(identifier) is not { } normalizedIdentifier)
            return current;
        var consumer =
            current?.Identifier == normalizedIdentifier
                ? current
                : new RequestConsumer(normalizedIdentifier);
        consumer.Name = Normalize(name, MaxNameLength) ?? consumer.Name;
        consumer.Group = Normalize(group, MaxNameLength) ?? consumer.Group;
        if (attributes is null)
            return consumer;
        foreach (var (rawKey, rawValue) in attributes)
        {
            var key = rawKey?.Trim();
            var value = rawValue?.Trim() is { Length: > 0 } trimmed ? trimmed : null;
            if (
                string.IsNullOrEmpty(key)
                || key.Length > MaxAttributeKeyLength
                || key.Contains('\0')
                || value?.Length > MaxAttributeValueLength
                || value?.Contains('\0') == true
            )
                continue;
            var index = consumer.Attributes.FindIndex(attribute => attribute.Key == key);
            if (index >= 0)
                consumer.Attributes[index] = new(key, value);
            else if (consumer.Attributes.Count < RequestConsumer.MaxAttributesPerUpdate)
                consumer.Attributes.Add(new(key, value));
        }
        return consumer;
    }

    public void EmitIfChanged(RequestConsumer consumer)
    {
        if (!consumer.HasMetadata)
            return;
        var hash = Hash(consumer);
        lock (sync)
        {
            if (hashes.TryGetValue(consumer.Identifier, out var node))
            {
                leastRecentlyUsed.Remove(node);
                leastRecentlyUsed.AddLast(node);
                if (node.Value.Hash == hash)
                    return;
                node.Value = (consumer.Identifier, hash);
            }
            else
            {
                hashes[consumer.Identifier] = leastRecentlyUsed.AddLast(
                    (consumer.Identifier, hash)
                );
                if (hashes.Count > MaxCachedConsumers)
                {
                    hashes.Remove(leastRecentlyUsed.First!.Value.Identifier);
                    leastRecentlyUsed.RemoveFirst();
                }
            }
        }
        events.EmitConsumerUpdate(consumer);
    }

    // Order-independent: attributes are hashed sorted by key. Normalized values are never empty
    // and never contain \0, so joining with \0 and writing null as "" is unambiguous. The first
    // 128 bits of SHA-256 are ample for change detection.
    private static UInt128 Hash(RequestConsumer consumer)
    {
        string[] parts =
        [
            consumer.Name ?? "",
            consumer.Group ?? "",
            .. consumer
                .Attributes.OrderBy(attribute => attribute.Key, StringComparer.Ordinal)
                .SelectMany(attribute => new[] { attribute.Key, attribute.Value ?? "" }),
        ];
        var canonical = string.Join('\0', parts);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(canonical), hash);
        return BinaryPrimitives.ReadUInt128LittleEndian(hash);
    }

    private static string? Normalize(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Contains('\0'))
            return null;
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}
