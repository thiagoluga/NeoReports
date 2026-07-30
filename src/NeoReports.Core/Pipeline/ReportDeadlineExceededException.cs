namespace NeoReports.Core.Pipeline;

/// <summary>
/// Thrown when a run exceeds the whole-job deadline configured with
/// <c>ReportBuilder&lt;T&gt;.Deadline(TimeSpan)</c>.
/// <para>
/// It derives from <see cref="OperationCanceledException"/> because a deadline <b>is</b> a
/// cooperative cancellation — every existing <c>catch (OperationCanceledException)</c> keeps working
/// and the run still surfaces as cancelled. The distinct type exists so a caller can tell a deadline
/// apart from an <see cref="OperationCanceledException"/> raised by something else: the run's own
/// token is not cancelled in either case, so the token alone cannot discriminate (an
/// <c>HttpClient.Timeout</c>, for instance, throws a <see cref="TaskCanceledException"/> carrying its
/// own already-cancelled internal token, which is a genuine failure rather than a cancellation).
/// </para>
/// </summary>
public sealed class ReportDeadlineExceededException : OperationCanceledException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="reportName">The report whose deadline elapsed.</param>
    /// <param name="deadline">The configured deadline.</param>
    /// <param name="innerException">The cancellation that unwound the run.</param>
    public ReportDeadlineExceededException(string reportName, TimeSpan deadline, Exception? innerException = null)
        : base($"Report '{reportName}' exceeded its {deadline} deadline and was cancelled.", innerException)
    {
        ReportName = reportName;
        Deadline = deadline;
    }

    /// <summary>The report whose deadline elapsed.</summary>
    public string ReportName { get; }

    /// <summary>The configured deadline.</summary>
    public TimeSpan Deadline { get; }
}
