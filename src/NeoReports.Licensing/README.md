# NeoReports.Licensing

Offline signed-license validation for NeoReports Pro packages ([D70](https://github.com/thiagoluga/NeoReports/blob/master/DECISIONS.md)). **MIT** — the verification logic here is publicly auditable; enforcement comes from the Pro packages refusing to run without a valid key, not from hiding this code.

## Registering a license (application code)

```csharp
services.AddNeoReportsProLicense(); // reads the NEOREPORTS_LICENSE_KEY environment variable
// or:
services.AddNeoReportsProLicense("your-license-key-here");
```

Throws `NeoReportsLicenseException` immediately if the key is missing, malformed, has an invalid
signature, or is expired — a Pro package won't start with a bad license.

## Validating directly

```csharp
LicenseToken token = ProLicense.Validate(licenseKey);
Console.WriteLine($"Licensed to {token.Licensee}, expires {token.ExpiresAtUtc:yyyy-MM-dd}");
```

## Signing (maintainer tooling only)

`LicenseSigner` produces a license key from a `LicenseToken` and an ECDsa P-256 private key. Real
license issuance (the website's 30-day trial flow) is out of scope for this package — this is the
primitive that flow would call, kept private-key-agnostic so the actual signing key never needs to
live in this repo.

```csharp
using ECDsa privateKey = ECDsa.Create();
privateKey.ImportPkcs8PrivateKey(Convert.FromBase64String(theRealPrivateKey), out _);

string key = LicenseSigner.Sign(
    new LicenseToken("Acme Corp", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30)),
    privateKey);
```
