namespace NeoReports.ConsumerSmoke;

/// <summary>
/// A deliberately tiny assertion recorder. This project cannot use the repo's test stack (xUnit,
/// Shouldly) without a <c>ProjectReference</c> or the repo's Central Package Management, and pulling
/// either in would reconnect it to the working tree it exists to stay away from.
/// </summary>
/// <remarks>
/// Collects failures instead of throwing on the first one: when a release is broken it is far more
/// useful to see every check that failed in a single run than to fix and re-run six times.
/// </remarks>
internal sealed class Checks
{
    private readonly List<string> _failures = [];
    private int _passed;

    /// <summary>Records a condition. <paramref name="detail"/> is appended to the failure line.</summary>
    public void That(bool condition, string description, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  ok    {description}");
            return;
        }

        string line = detail is null ? description : $"{description} — {detail}";
        _failures.Add(line);
        Console.WriteLine($"  FAIL  {line}");
    }

    /// <summary>Records that <paramref name="action"/> throws <typeparamref name="TException"/>.</summary>
    /// <remarks>
    /// A wrong exception type is reported distinctly from no exception at all: the first usually means
    /// the guard moved, the second means it is gone. They call for different fixes.
    /// </remarks>
    public void Throws<TException>(Action action, string description)
        where TException : Exception
    {
        try
        {
            action();
            That(false, description, $"nothing was thrown — expected {typeof(TException).Name}");
        }
        catch (TException)
        {
            That(true, description);
        }
        catch (Exception ex)
        {
            That(false, description, $"threw {ex.GetType().Name} instead of {typeof(TException).Name}");
        }
    }

    /// <summary>Prints the summary and returns the process exit code (0 = every check passed).</summary>
    public int Report()
    {
        Console.WriteLine();
        if (_failures.Count == 0)
        {
            Console.WriteLine($"All {_passed} checks passed against the published packages.");
            return 0;
        }

        Console.WriteLine($"{_failures.Count} of {_passed + _failures.Count} checks FAILED:");
        foreach (string failure in _failures)
            Console.WriteLine($"  - {failure}");

        return 1;
    }
}
