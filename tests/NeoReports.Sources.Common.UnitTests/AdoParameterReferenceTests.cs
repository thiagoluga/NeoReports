using NeoReports.Sources.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Common.UnitTests;

public sealed class AdoParameterReferenceTests
{
    [Fact]
    public void Finds_a_real_reference()
    {
        AdoParameterReference.IsReferenced("SELECT * FROM t WHERE Id > @cursor", "@cursor").ShouldBeTrue();
    }

    [Fact]
    public void Finds_a_reference_at_end_of_text()
    {
        AdoParameterReference.IsReferenced("... ORDER BY Id OFFSET @pageSize", "@pageSize").ShouldBeTrue();
    }

    [Fact]
    public void Finds_a_reference_followed_by_punctuation()
    {
        AdoParameterReference.IsReferenced("WHERE Id > @cursor::uuid ORDER BY Id", "@cursor").ShouldBeTrue();
        AdoParameterReference.IsReferenced("WHERE Id IN (@cursor)", "@cursor").ShouldBeTrue();
    }

    [Theory]
    [InlineData("SELECT TOP (@pageSize) * FROM t", "@page")]  // followed by a letter
    [InlineData("WHERE Id > @cursorId", "@cursor")]           // followed by a letter
    [InlineData("WHERE Id > @cursor_id", "@cursor")]          // followed by an underscore
    [InlineData("LIMIT @page2", "@page")]                     // followed by a digit
    public void Does_not_match_a_token_that_is_only_a_prefix_of_a_longer_parameter(string sql, string token)
    {
        AdoParameterReference.IsReferenced(sql, token).ShouldBeFalse();
    }

    [Fact]
    public void Matches_the_shorter_token_when_both_forms_are_present()
    {
        // @page is genuinely referenced here (as a standalone token) even though @pageSize also is.
        AdoParameterReference.IsReferenced("... LIMIT @pageSize OFFSET @page", "@page").ShouldBeTrue();
    }

    [Fact]
    public void Returns_false_when_absent()
    {
        AdoParameterReference.IsReferenced("SELECT 1", "@cursor").ShouldBeFalse();
    }

    [Fact]
    public void Is_case_insensitive()
    {
        AdoParameterReference.IsReferenced("WHERE Id > @CURSOR", "@cursor").ShouldBeTrue();
    }
}
