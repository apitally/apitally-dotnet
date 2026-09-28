using System.Globalization;
using Apitally.Export;

namespace Apitally.Tests.Export;

public class AttributeValuesTests
{
    [Fact]
    public void ScalarsConvertToExportedTypes()
    {
        var guid = Guid.NewGuid();
        var normalized = AttributeValues.Normalize(
            new Dictionary<string, object?>
            {
                ["string"] = "text",
                ["bool"] = true,
                ["int"] = 42,
                ["long"] = 42L,
                ["byte"] = (byte)7,
                ["float"] = 1.5f,
                ["double"] = 2.5,
                ["decimal"] = 1.25m,
                ["ulong"] = ulong.MaxValue,
                ["guid"] = guid,
                ["enum"] = DayOfWeek.Monday,
                ["null"] = null,
            }
        );

        Assert.Equal("text", normalized["string"]);
        Assert.Equal(true, normalized["bool"]);
        Assert.Equal(42L, normalized["int"]);
        Assert.Equal(42L, normalized["long"]);
        Assert.Equal(7L, normalized["byte"]);
        Assert.Equal(1.5, normalized["float"]);
        Assert.Equal(2.5, normalized["double"]);
        Assert.Equal("1.25", normalized["decimal"]);
        Assert.Equal(ulong.MaxValue.ToString(CultureInfo.InvariantCulture), normalized["ulong"]);
        Assert.Equal(guid.ToString(), normalized["guid"]);
        Assert.Equal("Monday", normalized["enum"]);
        Assert.Null(normalized["null"]);
        Assert.True(normalized.ContainsKey("null"));
    }

    [Fact]
    public void ArraysAreCopiedWithConvertedElements()
    {
        var strings = new[] { "a", "b" };
        var bytes = new byte[] { 1, 2 };
        var normalized = AttributeValues.Normalize(
            new Dictionary<string, object?>
            {
                ["strings"] = strings,
                ["ints"] = new[] { 1, 2 },
                ["floats"] = new[] { 1.5f },
                ["bools"] = new[] { true },
                ["bytes"] = bytes,
                ["objects"] = new object?[] { 1, "x", null },
            }
        );
        strings[0] = "changed";
        bytes[0] = 9;

        Assert.Equal(new[] { "a", "b" }, normalized["strings"]);
        Assert.Equal(new[] { 1L, 2L }, normalized["ints"]);
        Assert.Equal(new[] { 1.5 }, normalized["floats"]);
        Assert.Equal(new[] { true }, normalized["bools"]);
        Assert.Equal(new byte[] { 1, 2 }, normalized["bytes"]);
        Assert.Equal(new[] { "1", "x", null }, normalized["objects"]);
    }

    [Fact]
    public void DictionariesConvertOneLevelDeep()
    {
        var normalized = AttributeValues.Normalize(
            new Dictionary<string, object?>
            {
                ["map"] = new Dictionary<object, object?>
                {
                    [1] = 2,
                    ["list"] = new[] { "a" },
                    ["nested"] = new Dictionary<string, int> { ["x"] = 1 },
                },
            }
        );

        var map = Assert.IsType<Dictionary<string, object?>>(normalized["map"]);
        Assert.Equal(2L, map["1"]);
        Assert.Equal(new[] { "a" }, map["list"]);
        Assert.IsType<string>(map["nested"]);
    }

    [Fact]
    public void ListsAndObjectsUseInvariantStringConversion()
    {
        var normalized = AttributeValues.Normalize(
            new Dictionary<string, object?>
            {
                ["list"] = new List<int> { 1, 2 },
                ["date"] = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc),
            }
        );

        Assert.Equal(typeof(List<int>).ToString(), normalized["list"]);
        Assert.Equal(
            new DateTime(2026, 9, 28, 10, 0, 0).ToString(CultureInfo.InvariantCulture),
            normalized["date"]
        );
    }

    [Fact]
    public void ValuesThatFailConversionAreOmitted()
    {
        var normalized = AttributeValues.Normalize(
            new Dictionary<string, object?> { ["broken"] = new ThrowingValue(), ["ok"] = 1 }
        );

        Assert.Equal(["ok"], normalized.Keys);
    }

    private sealed class ThrowingValue
    {
        public override string ToString() => throw new InvalidOperationException();
    }
}
