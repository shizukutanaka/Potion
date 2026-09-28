using Xunit;

// Several suites assert on real OS sampling (ProcessRunner, SystemMetricsSampler,
// GC counters, drive enumeration). Running test classes in parallel lets a
// CPU/memory-hungry class starve a timing-sensitive one — serializing the whole
// assembly removes that interference class at a negligible wall-time cost.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
