using BenchmarkDotNet.Running;
using NeoReports.Benchmarks;

// Run all benchmarks:    dotnet run -c Release --project benchmarks/NeoReports.Benchmarks
// Filter to one:         dotnet run -c Release --project benchmarks/NeoReports.Benchmarks -- --filter *Csv*
BenchmarkSwitcher.FromAssembly(typeof(ReportMemoryBenchmark).Assembly).Run(args);
