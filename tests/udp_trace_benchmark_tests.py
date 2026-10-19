#!/usr/bin/env python3
"""Small deterministic checks for exact compact missing-sequence encoding."""
from udp_trace_benchmark import sequence_ranges


def expand(ranges):
    return [sequence for start, end in ranges for sequence in range(start, end)]


assert sequence_ranges([]) == []
source = [0, 1, 2, 8, 10, 11, 12, 30]
encoded = sequence_ranges(source)
assert encoded == [[0, 3], [8, 9], [10, 13], [30, 31]]
assert expand(encoded) == source
print("PASS: missing sequence ranges preserve every exact ID")
