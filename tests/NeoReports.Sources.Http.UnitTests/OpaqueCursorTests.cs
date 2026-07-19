using NeoReports.Sources.Http.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Http.UnitTests;

/// <summary>Base64(UTF-8(JSON)) opaque-cursor codec shared across the HTTP-family sources (ADR D61/D62).</summary>
public sealed class OpaqueCursorTests
{
    private sealed record State(string? Token = null);

    [Fact]
    public void Null_cursor_decodes_to_default()
    {
        OpaqueCursor.Decode<State>(null).ShouldBeNull();
    }

    [Fact]
    public void Round_trips_through_encode_and_decode()
    {
        string cursor = OpaqueCursor.Encode(new State("abc"));

        OpaqueCursor.Decode<State>(cursor).ShouldBe(new State("abc"));
    }

    [Fact]
    public void Empty_string_cursor_throws_instead_of_silently_resetting_to_first_page()
    {
        // A non-null cursor is always something this codec itself produced; an empty string is
        // neither "first page" (that's null) nor anything Encode ever emits — treating it as a
        // corrupted cursor and failing loudly is safer than silently restarting pagination.
        Should.Throw<Exception>(() => OpaqueCursor.Decode<State>(string.Empty));
    }
}
