// WORKAROUND-FILE(W-064, NUnit): worker count defaults to 1 and run-e2e.sh raises it to the instance count
// Fixtures are the parallel unit: each fixture pins itself to one stack instance for its
// lifetime (WebTest.StackUrls) and its tests stay sequential, so fixture-level parallelism
// never puts two fixtures on one instance. The default worker count stays 1 because a bare
// `dotnet test` targets a single manually-started instance, which concurrent fixtures would
// race on; dev/run-e2e.sh raises the worker count to its instance count via run settings.
[assembly: LevelOfParallelism(1)]
[assembly: Parallelizable(ParallelScope.Fixtures)]
