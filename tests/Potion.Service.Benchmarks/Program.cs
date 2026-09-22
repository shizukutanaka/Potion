using BenchmarkDotNet.Running;
using Potion.Service.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(CommandGuardBenchmarks).Assembly).Run(args);
