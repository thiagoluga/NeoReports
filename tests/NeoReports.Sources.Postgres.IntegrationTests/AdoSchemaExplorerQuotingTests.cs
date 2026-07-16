using NeoReports.Sources.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Postgres.IntegrationTests;

/// <summary>
/// Container-free unit tests for <see cref="AdoSchemaExplorer"/>'s identifier-quoting and
/// preview-SQL helpers (ADR D49) — the security-critical part: a table/schema name is always quoted
/// with embedded quote characters doubled, so a hostile identifier can't break out of the quotes to
/// inject SQL. (Lives in this integration-test project for the same reason
/// <c>AdoFilterTranslatorTests</c> does — there is no separate unit-test project for
/// <c>Sources.Common</c>; these run with no container.)
/// </summary>
public class AdoSchemaExplorerQuotingTests
{
    [Fact]
    public void QuoteAnsi_wraps_in_double_quotes_and_doubles_embedded_quotes()
    {
        AdoSchemaExplorer.QuoteAnsi("customers").ShouldBe("\"customers\"");
        AdoSchemaExplorer.QuoteAnsi("a\"b").ShouldBe("\"a\"\"b\"");
    }

    [Fact]
    public void QuoteMySql_wraps_in_backticks_and_doubles_embedded_backticks()
    {
        AdoSchemaExplorer.QuoteMySql("customers").ShouldBe("`customers`");
        AdoSchemaExplorer.QuoteMySql("a`b").ShouldBe("`a``b`");
    }

    [Fact]
    public void QuoteSqlServer_wraps_in_brackets_and_doubles_embedded_close_brackets()
    {
        AdoSchemaExplorer.QuoteSqlServer("customers").ShouldBe("[customers]");
        AdoSchemaExplorer.QuoteSqlServer("a]b").ShouldBe("[a]]b]");
    }

    [Fact]
    public void A_quote_injection_attempt_is_neutralized_by_doubling()
    {
        // Even a name crafted to close the quote and append SQL stays a single quoted identifier.
        AdoSchemaExplorer.QuoteAnsi("x\"; DROP TABLE t; --")
            .ShouldBe("\"x\"\"; DROP TABLE t; --\"");
    }

    [Fact]
    public void Preview_builders_emit_the_right_row_cap_syntax_per_dialect()
    {
        AdoSchemaExplorer.PreviewWithLimit("\"public\".\"t\"", 50).ShouldBe("SELECT * FROM \"public\".\"t\" LIMIT 50");
        AdoSchemaExplorer.PreviewWithTop("[dbo].[t]", 50).ShouldBe("SELECT TOP 50 * FROM [dbo].[t]");
        AdoSchemaExplorer.PreviewWithFetchFirst("\"S\".\"T\"", 50).ShouldBe("SELECT * FROM \"S\".\"T\" FETCH FIRST 50 ROWS ONLY");
    }

    [Fact]
    public void MaxPreviewRows_is_a_sane_engine_side_ceiling()
    {
        AdoSchemaExplorer.MaxPreviewRows.ShouldBeGreaterThan(50);
    }
}
