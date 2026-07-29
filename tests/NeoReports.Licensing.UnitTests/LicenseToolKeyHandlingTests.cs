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

    private string PathIn(string name) => Path.Combine(_directory, name);

    [Fact]
    public void Keygen_writes_a_private_key_and_prints_the_public_half()
    {
        string keyPath = PathIn("signing-key.pem");
        var stdout = new StringWriter();
        Console.SetOut(stdout);

        int exitCode = Cli.Run(["keygen", "--out", keyPath]);

        exitCode.ShouldBe(0);
        File.ReadAllText(keyPath).ShouldContain("BEGIN PRIVATE KEY");
        // The printed half must be the public one — never any part of the PEM written to disk.
        stdout.ToString().ShouldNotContain("PRIVATE KEY");
        stdout.ToString().ShouldContain("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQc");
    }

    [Fact]
    public void Keygen_refuses_to_overwrite_an_existing_key_and_leaves_it_untouched()
    {
        string keyPath = PathIn("signing-key.pem");
        File.WriteAllText(keyPath, "the original signing key");

        int exitCode = Cli.Run(["keygen", "--out", keyPath]);

        exitCode.ShouldBe(1);
        File.ReadAllText(keyPath).ShouldBe("the original signing key");
    }

    [Fact]
    public void Keygen_refuses_to_write_inside_a_git_working_tree()
    {
        // Where a signing key is one `git add -A` away from being published.
        Directory.CreateDirectory(Path.Combine(_directory, "repo", ".git"));
        Directory.CreateDirectory(Path.Combine(_directory, "repo", "nested"));
        string keyPath = Path.Combine(_directory, "repo", "nested", "signing-key.pem");

        int exitCode = Cli.Run(["keygen", "--out", keyPath]);

        exitCode.ShouldBe(1);
        File.Exists(keyPath).ShouldBeFalse();
    }

    [Fact]
    public void Keygen_detects_a_git_worktree_whose_dot_git_is_a_file()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "worktree"));
        File.WriteAllText(Path.Combine(_directory, "worktree", ".git"), "gitdir: /elsewhere/.git/worktrees/x");
        string keyPath = Path.Combine(_directory, "worktree", "signing-key.pem");

        Cli.Run(["keygen", "--out", keyPath]).ShouldBe(1);
        File.Exists(keyPath).ShouldBeFalse();
    }

    [Fact]
    public void The_written_key_is_readable_only_by_its_owner_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return; // Windows has no file mode; protection there comes from the directory's ACL.

        string keyPath = PathIn("signing-key.pem");
        Cli.Run(["keygen", "--out", keyPath]).ShouldBe(0);

        File.GetUnixFileMode(keyPath).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void Sign_issues_a_key_that_validates_against_the_generated_public_key()
    {
        string keyPath = PathIn("signing-key.pem");
        var keygenOut = new StringWriter();
        Console.SetOut(keygenOut);
        Cli.Run(["keygen", "--out", keyPath]).ShouldBe(0);
        string publicKeyBase64 = keygenOut.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Last();

        var signOut = new StringWriter();
        Console.SetOut(signOut);
        Cli.Run(["sign", "--key", keyPath, "--licensee", "Acme Corp", "--days", "30"]).ShouldBe(0);
        string licenseKey = signOut.ToString().Trim();

        using var publicKey = System.Security.Cryptography.ECDsa.Create();
        publicKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
        LicenseValidator.Validate(licenseKey, publicKey).Licensee.ShouldBe("Acme Corp");
    }

    [Fact]
    public void Sign_rejects_a_non_positive_day_count()
    {
        string keyPath = PathIn("signing-key.pem");
        Console.SetOut(new StringWriter());
        Cli.Run(["keygen", "--out", keyPath]).ShouldBe(0);

        Cli.Run(["sign", "--key", keyPath, "--licensee", "Acme Corp", "--days", "0"]).ShouldBe(1);
    }
}
