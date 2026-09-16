// The CLI writes machine-readable output straight to stdout, and capturing that means
// redirecting a process-global stream. Running these tests serially is the honest cost of
// testing the real output channels rather than a stand-in for them.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
