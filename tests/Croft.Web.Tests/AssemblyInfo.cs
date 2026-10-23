// Brief 03h item 3: this suite is audited safe under xUnit's default class parallelism — no
// shared mutable statics; the one process-wide write (AppCulture.ConfigureDefaults in
// BlogBunitContext) is idempotent and the only value anything writes. The cap is pinned to the
// dev machine's thread count (4c/8t) so every machine runs the same verified parallelism
// instead of ProcessorCount, whatever the host.
[assembly: CollectionBehavior(MaxParallelThreads = 8)]
