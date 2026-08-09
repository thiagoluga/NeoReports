using System.Security.Cryptography;
using NeoReports.LicenseTool;
using Shouldly;
using Xunit;

namespace NeoReports.Licensing.UnitTests;

/// <summary>
/// Covers the two pieces of <c>tools/NeoReports.LicenseTool</c> that are security controls rather
/// than plumbing (ADR D70, Q3a): refusing to overwrite an existing signing key, and the permissions
/// the key file is created with. Both could otherwise be deleted with the whole suite still green.
/// </summary>
[Collection(LicenseEnvironmentCollection.Name)]
public sealed class LicenseToolKeyHandlingTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("neoreports-licensetool-").FullName;

    // Console.Out is process-global; these tests capture it, so they run in the serialized collection
    // and hand the original back afterwards.
    private readonly TextWriter _originalOut = Console.Out;

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Directory.Delete(_directory, recursive: true);
    }

    // Path.Join, not Path.Combine: Combine would silently discard _directory if the name ever looked
    // rooted, quietly writing a key somewhere other than the temp directory this test cleans up.
    private string PathIn(params string[] parts) => Path.Join([_directory, .. parts]);

    /// <summary>Runs the CLI with stdout captured, restoring and disposing the capture afterwards.</summary>
    private (int ExitCode, string Stdout) RunCapturingStdout(params string[] args)
    {
        using var capture = new StringWriter();
        Console.SetOut(capture);
        try
        {
            return (Cli.Run(args), capture.ToString());
        }
        finally
        {
            Console.SetOut(_originalOut);
        }
    }

    /// <summary>
    /// The pre-release key-pair check (ADR D83) is only worth running if it can fail. A `verify` that
    /// reports success regardless would be worse than having none — it would launder exactly the
    /// mistake it exists to catch (a public key committed that is not the pair of the vaulted private
    /// key) into a green tick.
    /// </summary>
    /// <remarks>
    /// A freshly generated pair stands in for a mismatched one: it is, by construction, not the pair
    /// of the key embedded in this build. The positive path cannot be tested here — it needs the real
    /// private key, which lives in a vault and never touches CI. That asymmetry is the point: this
    /// test pins that the command discriminates, and the maintainer runs the positive half by hand.
    /// </remarks>
    /// <summary>
    /// The success path of `verify` — the branch the maintainer actually depends on before tagging a
    /// release, and the one that would otherwise ship having never executed. Producing a license that
    /// validates against the *embedded* key needs the vaulted private half, which never touches CI, so
    /// the test drives the same code with an explicit key pair through the internal seam.
    /// </summary>
    [Fact]
    public void Verify_reports_a_matching_pair_as_valid()
    {
        string keyPath = PathIn("matching-key.pem");
        RunCapturingStdout("keygen", "--out", keyPath).ExitCode.ShouldBe(0);

        (int signExit, string licenseKey) = RunCapturingStdout(
            "sign", "--key", keyPath, "--licensee", "Acme Corp", "--days", "7");
        signExit.ShouldBe(0);

        using ECDsa verifyingKey = ECDsa.Create();
        verifyingKey.ImportFromPem(File.ReadAllText(keyPath));

        using var capture = new StringWriter();
        Console.SetOut(capture);
        int exitCode;
        try
        {
            exitCode = Cli.VerifyWith(licenseKey.Trim(), verifyingKey);
        }
        finally
        {
            Console.SetOut(_originalOut);
        }

        exitCode.ShouldBe(0);
        capture.ToString().ShouldContain("VALID");
        // The licensee is echoed back, so a mismatch between what was signed and what verifies is
        // visible to the eye and not just to the exit code.
        capture.ToString().ShouldContain("Acme Corp");
    }

    [Fact]
    public void Verify_rejects_a_license_signed_by_a_key_that_is_not_the_embedded_pair()
    {
        string keyPath = PathIn("foreign-key.pem");
        RunCapturingStdout("keygen", "--out", keyPath).ExitCode.ShouldBe(0);

        (int signExit, string licenseKey) = RunCapturingStdout(
            "sign", "--key", keyPath, "--licensee", "Someone Else", "--days", "1");
        signExit.ShouldBe(0);

        (int verifyExit, string stdout) = RunCapturingStdout("verify", "--license", licenseKey.Trim());

        verifyExit.ShouldNotBe(0, "a non-zero exit is what makes this usable as a release gate");
        stdout.ShouldNotContain("VALID");
    }

    [Fact]
    public void Keygen_writes_a_private_key_and_prints_the_public_half()
    {
        string keyPath = PathIn("signing-key.pem");

        (int exitCode, string stdout) = RunCapturingStdout("keygen", "--out", keyPath);

        exitCode.ShouldBe(0);
        File.ReadAllText(keyPath).ShouldContain("BEGIN PRIVATE KEY");
        // The printed half must be the public one — never any part of the PEM written to disk.
        stdout.ShouldNotContain("PRIVATE KEY");
        stdout.ShouldContain("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQc");
    }

    [Fact]
    public void Keygen_refuses_to_overwrite_an_existing_key_and_leaves_it_untouched()
    {
        string keyPath = PathIn("signing-key.pem");
        File.WriteAllText(keyPath, "the original signing key");

        (int exitCode, _) = RunCapturingStdout("keygen", "--out", keyPath);

        exitCode.ShouldBe(1);
        File.ReadAllText(keyPath).ShouldBe("the original signing key");
    }

    [Fact]
    public void Keygen_refuses_to_write_inside_a_git_working_tree()
    {
        // Where a signing key is one `git add -A` away from being published.
        Directory.CreateDirectory(PathIn("repo", ".git"));
        Directory.CreateDirectory(PathIn("repo", "nested"));
        string keyPath = PathIn("repo", "nested", "signing-key.pem");

        (int exitCode, _) = RunCapturingStdout("keygen", "--out", keyPath);

        exitCode.ShouldBe(1);
        File.Exists(keyPath).ShouldBeFalse();
    }

    [Fact]
    public void Keygen_detects_a_git_worktree_whose_dot_git_is_a_file()
    {
        Directory.CreateDirectory(PathIn("worktree"));
        File.WriteAllText(PathIn("worktree", ".git"), "gitdir: /elsewhere/.git/worktrees/x");
        string keyPath = PathIn("worktree", "signing-key.pem");

        (int exitCode, _) = RunCapturingStdout("keygen", "--out", keyPath);

        exitCode.ShouldBe(1);
        File.Exists(keyPath).ShouldBeFalse();
    }

    [Fact]
    public void The_written_key_is_readable_only_by_its_owner_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return; // Windows has no file mode; protection there comes from the directory's ACL.

        string keyPath = PathIn("signing-key.pem");
        RunCapturingStdout("keygen", "--out", keyPath).ExitCode.ShouldBe(0);

        File.GetUnixFileMode(keyPath).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void Sign_issues_a_key_that_validates_against_the_generated_public_key()
    {
        string keyPath = PathIn("signing-key.pem");
        (int keygenExit, string keygenOut) = RunCapturingStdout("keygen", "--out", keyPath);
        keygenExit.ShouldBe(0);
        string publicKeyBase64 = keygenOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Last();

        (int signExit, string signOut) = RunCapturingStdout("sign", "--key", keyPath, "--licensee", "Acme Corp", "--days", "30");
        signExit.ShouldBe(0);

        using var publicKey = ECDsa.Create();
        publicKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
        LicenseValidator.Validate(signOut.Trim(), publicKey).Licensee.ShouldBe("Acme Corp");
    }

    [Fact]
    public void Sign_rejects_a_non_positive_day_count()
    {
        string keyPath = PathIn("signing-key.pem");
        RunCapturingStdout("keygen", "--out", keyPath).ExitCode.ShouldBe(0);

        RunCapturingStdout("sign", "--key", keyPath, "--licensee", "Acme Corp", "--days", "0").ExitCode.ShouldBe(1);
    }
}
