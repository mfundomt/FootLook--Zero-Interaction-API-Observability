// All hosts share FileSink's captures.jsonl (under the test bin folder); running test classes
// in parallel would make their appends contend for the file. The suite is small enough that
// serial execution costs little.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
