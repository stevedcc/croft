using Xunit.v3;

// Every API test class owns a database on one shared Postgres container, and the cache tests host
// two full API instances each and wait on cross-process LISTEN/NOTIFY round trips. Uncapped, xUnit
// runs as many classes as there are logical processors at once and those waits starve on a loaded
// machine. The cap is half the dev machine's thread count (4c/8t, where Croft.Web.Tests runs 8
// light classes): enough overlap to keep the suite near a minute, few enough that a notification
// round trip is not queued behind dozens of host startups.
[assembly: Parallelization(MaxThreads = 4)]
