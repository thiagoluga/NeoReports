# NeoReports.Destinations.S3

Amazon S3 destination for [NeoReports](https://github.com/thiagoluga/NeoReports).

Uploads the finished report file to an S3 bucket at a key resolved from a template. The upload is
all-or-nothing (`PutObject` is atomic per object), so a failure never leaves a partial object.

## Usage

```csharp
using NeoReports.Destinations.S3;

b.UploadTo(Destination.S3("my-bucket", "reports/{name}/{date:yyyy-MM-dd}.{ext}"));
```

The S3 client is resolved from DI (`IAmazonS3`) when registered; otherwise a default client is
built from ambient AWS credentials/region.

## License

MIT © NeoReports Contributors
