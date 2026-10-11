using Xunit;

// Avalonia has one UI dispatcher per process. The Desktop suite deliberately exercises real pointer and
// closing paths, so running test classes concurrently would create controls from non-owner threads.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
