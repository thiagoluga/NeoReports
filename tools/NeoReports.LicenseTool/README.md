# NeoReports Pro license tool (maintainer only)

Issues the offline license keys the Pro packages validate (ADR D70). **Never published** — the
private half of the signing key is what makes a license authentic, so the issuing side stays with
the maintainer and never travels with the product.

## 1. Generate the signing key pair (once, and on every rotation)

Write it **outside the repository** — `dotnet run --project <path>` resolves a relative `--out`
against your shell's working directory, so `./key.pem` from the repo root lands in the working tree:

```bash
# PowerShell
dotnet run --project tools/NeoReports.LicenseTool -- keygen --out "$env:USERPROFILE\neoreports-signing-key.pem"

# bash
dotnet run --project tools/NeoReports.LicenseTool -- keygen --out "$HOME/neoreports-signing-key.pem"
```

Prints the **public** key to stdout and writes the **private** key to the given path. Then,
immediately:

1. Move `neoreports-signing-key.pem` into a secrets vault and delete the local copy.
2. Paste the printed public key into `ProLicense.PublicKeyBase64`
   (`src/NeoReports.Licensing/ProLicense.cs`) and ship a new release.

Never paste the private key into source control, a chat window, an issue, or a CI log — anyone
holding it can mint licenses, and offline validation means an issued license cannot be revoked.

Three guards back this up, but none of them replaces putting the key in a vault:

- `keygen` **refuses to write inside a git working tree**, since a key there is one `git add -A`
  away from being published.
- It **refuses to overwrite an existing file** — replacing a signing key orphans every license
  already issued under it.
- On Unix the file is created `0600` by the `open` call itself, so it is never briefly
  world-readable. **On Windows there is no equivalent**: the file inherits its directory's ACL, which
  is why the command above targets a user-private directory rather than a shared or repo path.

## 2. Issue a license

```bash
dotnet run --project tools/NeoReports.LicenseTool -- \
    sign --key ./neoreports-signing-key.pem --licensee "Acme Corp" --days 30
```

Prints the license key to stdout (a summary of who/when goes to stderr, so piping to a file gives
you just the key). `--days` defaults to 30 — the trial length the website offers. `--from
<yyyy-MM-dd>` back- or post-dates the start of the window; it defaults to now.

The customer supplies that key as the `NEOREPORTS_LICENSE_KEY` environment variable, or via
`services.AddNeoReportsProLicense(key)` / `ProLicenseGate.Register(key)` — see
`src/NeoReports.Licensing/README.md`.

## What this tool deliberately does not do

- **No revocation.** Validation is fully offline (D70), so an issued key works until it expires.
  Issue short windows rather than relying on being able to take one back.
- **No customer database.** It signs what you pass it; tracking who holds which license belongs to
  the website/store, not here.
- **No machine binding.** A license is not tied to a machine or deployment (D70's documented gap).
