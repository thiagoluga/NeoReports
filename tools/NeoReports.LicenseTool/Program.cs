using System.Globalization;
using System.Security.Cryptography;
using NeoReports.LicenseTool;
using NeoReports.Licensing;

return Cli.Run(args);

namespace NeoReports.LicenseTool
{
    /// <summary>
    /// Maintainer-side tooling for NeoReports Pro licensing (ADR D70): generates the signing key pair
    /// and issues license keys. Deliberately not shipped — the private half of the key pair is what
    /// makes a license authentic, so it must never travel with the product.
    /// </summary>
    internal static class Cli
    {
        public static int Run(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            try
            {
                return args[0] switch
                {
                    "keygen" => KeyGen(args),
                    "sign" => Sign(args),
                    _ => Unknown(args[0]),
                };
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or FormatException or ArgumentException)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }
        }

        private static int Unknown(string command)
        {
            Console.Error.WriteLine($"error: unknown command '{command}'.");
            PrintUsage();
            return 1;
        }

        private static void PrintUsage() => Console.Error.WriteLine(
            """
            NeoReports Pro license tool

              keygen --out <private-key.pem>
                  Generates a new ECDsa P-256 signing key pair.
                  Writes the PRIVATE key to <private-key.pem> and prints the PUBLIC key to stdout.

              sign --key <private-key.pem> --licensee <name> [--days 30] [--from <yyyy-MM-dd>]
                  Issues a license key signed with the private key. Prints the key to stdout.

            Rotating the signing key invalidates every license already issued under the old one.
            """);

        private static int KeyGen(string[] args)
        {
            string outPath = Path.GetFullPath(RequireOption(args, "--out"));

            // A signing key inside a working tree is one `git add -A` away from being published, and
            // a leaked key can mint licenses forever (validation is offline, with no revocation).
            if (FindGitRoot(outPath) is { } gitRoot)
            {
                Console.Error.WriteLine($"error: '{outPath}' is inside the git working tree at '{gitRoot}'.");
                Console.Error.WriteLine("Refusing to write a signing key where it could be committed — choose a path outside the repository.");
                return 1;
            }

            if (File.Exists(outPath))
            {
                // Overwriting a signing key silently would orphan every license issued under it.
                Console.Error.WriteLine($"error: '{outPath}' already exists. Refusing to overwrite a signing key — move or delete it first.");
                return 1;
            }

            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            WritePrivateKey(outPath, key.ExportPkcs8PrivateKeyPem());

            Console.WriteLine("Public key (embed as ProLicense.PublicKeyBase64):");
            Console.WriteLine();
            Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
            Console.WriteLine();
            Console.Error.WriteLine($"Private key written to: {outPath}");
            if (OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine(
                    "On Windows the file inherits its directory's ACL — make sure that directory is user-private " +
                    "(e.g. under %USERPROFILE%), not a shared or world-readable location.");
            }

            Console.Error.WriteLine("Move it into a secrets vault now, and keep it out of source control, chat logs and CI logs.");
            return 0;
        }

        /// <summary>The nearest ancestor directory of <paramref name="path"/> containing a <c>.git</c> entry, or <c>null</c>.</summary>
        private static string? FindGitRoot(string path)
        {
            for (DirectoryInfo? dir = new FileInfo(path).Directory; dir is not null; dir = dir.Parent)
            {
                // A worktree/submodule has .git as a file rather than a directory — both count.
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                    return dir.FullName;
            }

            return null;
        }

        private static int Sign(string[] args)
        {
            string keyPath = RequireOption(args, "--key");
            string licensee = RequireOption(args, "--licensee");
            int days = int.Parse(Option(args, "--days") ?? "30", CultureInfo.InvariantCulture);
            if (days <= 0)
                throw new ArgumentException("--days must be greater than zero.");

            DateTimeOffset issuedAt = Option(args, "--from") is { } from
                ? DateTimeOffset.Parse(from, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
                : DateTimeOffset.UtcNow;

            using ECDsa key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(keyPath));

            var token = new LicenseToken(licensee, issuedAt, issuedAt.AddDays(days));
            Console.WriteLine(LicenseSigner.Sign(token, key));

            Console.Error.WriteLine($"Issued to \"{token.Licensee}\": {token.IssuedAtUtc:yyyy-MM-dd} to {token.ExpiresAtUtc:yyyy-MM-dd} ({days} days).");
            return 0;
        }

        /// <summary>
        /// Writes the private key.
        /// <para>
        /// <c>UnixCreateMode</c> is applied by the <c>open</c> call itself, so on Unix the file is
        /// never even momentarily group/world-readable — chmod-ing after creation would leave a
        /// window in which another local user could open the path and keep reading through the
        /// permission change (a Unix permission check happens at open, not per read). umask can only
        /// clear bits, never add them, so 0600 here is a ceiling regardless of the caller's umask.
        /// </para>
        /// <para>
        /// On Windows the option is ignored and the file inherits its directory's ACL — Windows has
        /// per-file ACLs, but setting one needs a package this repo doesn't reference, so the
        /// protection there comes from choosing a user-private directory. <c>keygen</c> says so
        /// explicitly, and refuses to write inside a git working tree either way.
        /// </para>
        /// <c>FileMode.CreateNew</c> also makes "don't clobber an existing key" atomic, rather than
        /// resting on the earlier <see cref="File.Exists"/> check alone.
        /// </summary>
        private static void WritePrivateKey(string path, string pem)
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };

            // Setting UnixCreateMode is itself unsupported on Windows, so it is guarded rather than
            // merely ignored there; FileMode.CreateNew still applies on both platforms.
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            using var stream = new FileStream(path, options);
            using var writer = new StreamWriter(stream);
            writer.Write(pem);
        }

        private static string RequireOption(string[] args, string name) =>
            Option(args, name) ?? throw new ArgumentException($"missing required option {name}.");

        private static string? Option(string[] args, string name)
        {
            int index = Array.IndexOf(args, name);
            if (index < 0)
                return null;
            if (index + 1 >= args.Length)
                throw new ArgumentException($"option {name} needs a value.");

            return args[index + 1];
        }
    }
}
