namespace Apitally;

using Apitally.Models;

class ConsumerRegistry
{
    private readonly Dictionary<string, Consumer> _consumers = [];
    private readonly HashSet<string> _updated = [];

    public static Consumer? ConsumerFromObject(object? consumer)
    {
        return consumer switch
        {
            null => null,
            Consumer c => string.IsNullOrWhiteSpace(c.Identifier) ? null : c,
            string s => string.IsNullOrWhiteSpace(s) ? null : new Consumer { Identifier = s },
            int or long => new Consumer { Identifier = consumer.ToString()! },
            _ => null,
        };
    }

    public void AddOrUpdateConsumer(Consumer? consumer)
    {
        if (consumer is null || (consumer.Name is null && consumer.Group is null))
        {
            return;
        }

        if (!_consumers.TryGetValue(consumer.Identifier, out var existing))
        {
            _consumers[consumer.Identifier] = consumer;
            _updated.Add(consumer.Identifier);
            return;
        }

        var hasChanges = false;
        var newName = existing.Name;
        var newGroup = existing.Group;
        if (consumer.Name is not null && consumer.Name != existing.Name)
        {
            newName = consumer.Name;
            hasChanges = true;
        }
        if (consumer.Group is not null && consumer.Group != existing.Group)
        {
            newGroup = consumer.Group;
            hasChanges = true;
        }
        if (hasChanges)
        {
            var updatedConsumer = new Consumer
            {
                Identifier = consumer.Identifier,
                Name = newName,
                Group = newGroup,
            };
            _consumers[consumer.Identifier] = updatedConsumer;
            _updated.Add(consumer.Identifier);
        }
    }

    public List<Consumer> GetAndResetConsumers()
    {
        var data = _updated.Select(identifier => _consumers[identifier]).ToList();
        _updated.Clear();
        return data;
    }

    public void Clear()
    {
        _consumers.Clear();
        _updated.Clear();
    }
}
