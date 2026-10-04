using Tamp.Observer.Domain;
using Xunit;

namespace Tamp.Observer.Domain.Tests;

/// <summary>
/// Unit tests for log-derived Issue grouping (ADR 0015, TOBS-42): occurrences of one error class must collapse
/// to a single key, distinct classes and distinct categories must stay separate. Samples are real SkyFire
/// sql.sql validator lines. Fast lane.
/// </summary>
public sealed class LogGroupingTests
{
    // Two occurrences of the SAME class differing only in ids/numbers -> one key.
    [Fact]
    public void Same_class_different_numbers_groups()
    {
        const string cat = "sql.sql";
        var a = LogGrouping.Key("SmartAIMgr: Entry 3887200 SourceType 9 Event 4 Action 33 Kill Credit: There is a killcredit spell for creatureEntry 38872 (SpellId: 73118 effect: 0)", cat);
        var b = LogGrouping.Key("SmartAIMgr: Entry 1120 SourceType 0 Event 25 Action 11 Kill Credit: There is a killcredit spell for creatureEntry 50409 (SpellId: 59390 effect: 1)", cat);
        Assert.Equal(a, b);
    }

    // Different error classes (different prose) -> different keys.
    [Fact]
    public void Different_classes_stay_separate()
    {
        const string cat = "sql.sql";
        var smartAi = LogGrouping.Key("SmartAIMgr: Entry 3887200 SourceType 9 Event 4 Action 33 Kill Credit: ...", cat);
        var missingLoot = LogGrouping.Key("Table 'creature_loot_template' entry 38659 (creature entry) does not exist but used as loot id in DB.", cat);
        Assert.NotEqual(smartAi, missingLoot);
    }

    // Quoted table names are parameterized: the loot-missing class groups across tables (literal stripped).
    [Fact]
    public void Quoted_identifiers_are_parameterized()
    {
        var k1 = LogGrouping.Key("Table 'creature_loot_template' entry 38659 does not exist but used as loot id in DB.", "sql.sql");
        var k2 = LogGrouping.Key("Table 'gameobject_loot_template' entry 90001 does not exist but used as loot id in DB.", "sql.sql");
        Assert.Equal(k1, k2);
    }

    // Same message under different logger categories must not share an Issue.
    [Fact]
    public void Category_separates_otherwise_identical_messages()
    {
        const string body = "reference 123 does not exist";
        Assert.NotEqual(LogGrouping.Key(body, "sql.sql"), LogGrouping.Key(body, "scripts"));
    }

    // No category (non-k8s log) still normalizes; numbers collapse, no category prefix.
    [Fact]
    public void No_category_normalizes_without_prefix()
    {
        var k = LogGrouping.Key("timeout after 30 ms", null);
        Assert.Equal("timeout after # ms", k);
    }

    [Theory]
    [InlineData("Entry 42 and 7", "Entry # and #")]
    [InlineData("rate 3.14 pct", "rate # pct")]
    [InlineData("guid 550e8400-e29b-41d4-a716-446655440000 seen", "guid # seen")]
    [InlineData("addr 0x1A2B done", "addr # done")]
    [InlineData("col `creature`.`id` bad", "col `?`.`?` bad")]
    public void Normalize_parameterizes_literals(string input, string expected) =>
        Assert.Equal(expected, LogGrouping.Normalize(input));

    [Fact]
    public void Blank_body_has_stable_fallback()
    {
        Assert.Equal("log-error", LogGrouping.Normalize(null));
        Assert.Equal("log-error", LogGrouping.Normalize("   "));
    }

    [Fact]
    public void Title_is_single_line_and_bounded()
    {
        var title = LogGrouping.Title("  line one\n   with   spaces  ");
        Assert.Equal("line one with spaces", title);
        Assert.True(LogGrouping.Title(new string('x', 500)).Length <= 140);
    }
}
