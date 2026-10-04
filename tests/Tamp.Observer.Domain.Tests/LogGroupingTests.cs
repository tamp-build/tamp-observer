using Tamp.Observer.Domain;
using Xunit;

namespace Tamp.Observer.Domain.Tests;

/// <summary>
/// Unit tests for log-derived Issue grouping (ADR 0015, TOBS-42): occurrences of one error class must collapse
/// to a single key, distinct classes and distinct categories must stay separate. Samples are real Sample
/// db.query validator lines. Fast lane.
/// </summary>
public sealed class LogGroupingTests
{
    // Two occurrences of the SAME class differing only in ids/numbers -> one key.
    [Fact]
    public void Same_class_different_numbers_groups()
    {
        const string cat = "db.query";
        var a = LogGrouping.Key("RecordValidator: Entry 3887200 Type 9 Code 4 Step 33 Link: there is a linked record for refEntry 38872 (RefId: 73118 slot: 0)", cat);
        var b = LogGrouping.Key("RecordValidator: Entry 1120 Type 0 Code 25 Step 11 Link: there is a linked record for refEntry 50409 (RefId: 59390 slot: 1)", cat);
        Assert.Equal(a, b);
    }

    // Different error classes (different prose) -> different keys.
    [Fact]
    public void Different_classes_stay_separate()
    {
        const string cat = "db.query";
        var validator = LogGrouping.Key("RecordValidator: Entry 3887200 Type 9 Code 4 Step 33 Link: ...", cat);
        var missingRef = LogGrouping.Key("Table 'items_catalog' entry 38659 (catalog entry) does not exist but used as reference id in DB.", cat);
        Assert.NotEqual(validator, missingRef);
    }

    // Quoted identifiers (table/script names) are the class DISCRIMINATOR and must be preserved: different
    // tables in the same message shape are different classes (TOBS-42 over-collapse fix).
    [Fact]
    public void Quoted_identifiers_discriminate_classes()
    {
        var k1 = LogGrouping.Key("Table 'items_catalog' entry 38659 does not exist but used as reference id in DB.", "db.query");
        var k2 = LogGrouping.Key("Table 'orders_catalog' entry 90001 does not exist but used as reference id in DB.", "db.query");
        Assert.NotEqual(k1, k2);
        // ...but the SAME table with a different numeric entry still collapses to one class.
        var k1b = LogGrouping.Key("Table 'items_catalog' entry 11111 does not exist but used as reference id in DB.", "db.query");
        Assert.Equal(k1, k1b);
    }

    // Same "did not match the expected schema" shape, different handler names in backticks + different ids ->
    // different handlers are different classes, same handler collapses (TOBS-42 over-collapse fix).
    [Fact]
    public void Handler_name_discriminates_schema_mismatch_classes()
    {
        var alpha1 = LogGrouping.Key("Record `62518` Field `Index: FIELD_1 Name: 28` of handler `handler_alpha` did not match the expected schema", "handlers");
        var alpha2 = LogGrouping.Key("Record `64306` Field `Index: FIELD_0 Name: 14` of handler `handler_alpha` did not match the expected schema", "handlers");
        var beta = LogGrouping.Key("Record `45204` Field `Index: FIELD_1 Name: 77` of handler `handler_beta` did not match the expected schema", "handlers");
        Assert.Equal(alpha1, alpha2);   // same handler, different ids -> one class
        Assert.NotEqual(alpha1, beta);  // different handler -> separate class (no over-collapse)
    }

    // Same message under different logger categories must not share an Issue.
    [Fact]
    public void Category_separates_otherwise_identical_messages()
    {
        const string body = "reference 123 does not exist";
        Assert.NotEqual(LogGrouping.Key(body, "db.query"), LogGrouping.Key(body, "scripts"));
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
    [InlineData("col `orders`.`id` bad", "col `orders`.`id` bad")]
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
