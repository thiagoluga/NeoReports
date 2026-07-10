using NeoReports.Abstractions;
using NeoReports.Core.Scheduling;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests.Scheduling;

public class CronValidationTests
{
    [Theory]
    [InlineData("0 6 * * 1")]
    [InlineData("*/5 * * * *")]
    [InlineData("0 0 1 1 *")]
    public void Valid_expressions_parse_without_throwing(string cron) =>
        Should.NotThrow(() => CronValidation.Validate(cron));

    [Theory]
    [InlineData("not a cron")]
    [InlineData("* * * *")]
    [InlineData("99 99 99 99 99")]
    public void Invalid_expressions_throw_ConfigurationException(string cron)
    {
        var ex = Should.Throw<ConfigurationException>(() => CronValidation.Validate(cron));
        ex.Message.ShouldContain(cron);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_expressions_throw_ConfigurationException(string? cron) =>
        Should.Throw<ConfigurationException>(() => CronValidation.Validate(cron!));
}
