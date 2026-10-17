#!/usr/bin/env python3
"""Compare sets of PNG captures against the renderer's run-to-run noise floor.

Frame capture is not bit-deterministic, so a change cannot be shown to render "identically". What can be
shown is that its diff against a baseline falls inside the spread the renderer produces when nothing has
changed at all. Two runs are not enough to measure that spread; take at least three per build.

    python3 scripts/capture-noise-floor.py --before base1.png base2.png base3.png \
                                          --after  new1.png new2.png new3.png

The primary check is nearest-neighbour: every new capture must sit as close to some baseline capture as the
baseline captures sit to each other. Run-to-run noise is not a single spread; a run lands in one of a few
distinct states, so comparing ranges fails whenever the two sets happen to split across states differently.
The range comparison is still printed, and a cross-build pair tighter than the tightest same-build pair
remains a strong signal: a real difference cannot make two builds agree more closely than one build agrees
with itself.

Reads PNGs directly (zlib plus manual unfiltering) so it needs no third-party packages.
"""

import argparse
import itertools
import struct
import sys
import zlib


def read_png(path):
    data = open(path, "rb").read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"{path} is not a PNG")
    pos, idat, width, height, depth, color = 8, b"", None, None, None, None
    while pos < len(data):
        length = struct.unpack(">I", data[pos:pos + 4])[0]
        kind = data[pos + 4:pos + 8]
        chunk = data[pos + 8:pos + 8 + length]
        pos += 12 + length
        if kind == b"IHDR":
            width, height, depth, color = struct.unpack(">IIBB", chunk[:10])
        elif kind == b"IDAT":
            idat += chunk
        elif kind == b"IEND":
            break
    if depth != 8:
        raise ValueError(f"{path}: only 8-bit PNGs are supported, got {depth}")
    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[color]
    raw = zlib.decompress(idat)
    stride = width * channels
    out = bytearray(height * stride)
    prev = bytearray(stride)
    read = 0
    for y in range(height):
        filter_type = raw[read]
        read += 1
        line = bytearray(raw[read:read + stride])
        read += stride
        if filter_type == 1:
            for i in range(channels, stride):
                line[i] = (line[i] + line[i - channels]) & 255
        elif filter_type == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 255
        elif filter_type == 3:
            for i in range(stride):
                left = line[i - channels] if i >= channels else 0
                line[i] = (line[i] + ((left + prev[i]) >> 1)) & 255
        elif filter_type == 4:
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                b = prev[i]
                c = prev[i - channels] if i >= channels else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pred = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pred) & 255
        out[y * stride:(y + 1) * stride] = line
        prev = line
    return width, height, channels, bytes(out)


def compare(first, second):
    w1, h1, c1, a = first
    w2, h2, c2, b = second
    if (w1, h1, c1) != (w2, h2, c2):
        raise ValueError("captures differ in size or channel count")
    pixels, worst, outliers = set(), 0, {}
    for i in range(len(a)):
        if a[i] != b[i]:
            delta = abs(a[i] - b[i])
            pixel = i // c1
            pixels.add(pixel)
            worst = max(worst, delta)
            if delta > 20:
                x, y = pixel % w1, pixel // w1
                outliers[(x, y)] = max(outliers.get((x, y), 0), delta)
    return len(pixels), worst, outliers


def summarise(label, pairs, images):
    if not pairs:
        print(f"{label:<24} (needs at least two captures)")
        return None
    results = [compare(images[x], images[y]) for x, y in pairs]
    counts = [r[0] for r in results]
    worst = max(r[1] for r in results)
    outliers = {}
    for _, _, found in results:
        for point, delta in found.items():
            outliers[point] = max(outliers.get(point, 0), delta)
    print(f"{label:<24} {min(counts):5d}-{max(counts):5d} px, max delta {worst}")
    return min(counts), max(counts), worst, outliers


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--before", nargs="+", required=True, help="baseline captures (3 or more)")
    parser.add_argument("--after", nargs="+", required=True, help="captures after the change (3 or more)")
    args = parser.parse_args()

    for group, name in ((args.before, "--before"), (args.after, "--after")):
        if len(group) < 3:
            print(f"warning: {name} has {len(group)} capture(s); 3 or more are needed to measure a spread",
                  file=sys.stderr)

    images = {path: read_png(path) for path in args.before + args.after}
    before = summarise("within before", list(itertools.combinations(args.before, 2)), images)
    after = summarise("within after", list(itertools.combinations(args.after, 2)), images)
    across = summarise("across", [(x, y) for x in args.before for y in args.after], images)

    if not (before and after and across):
        return 0

    floor_low = min(before[0], after[0])
    floor_high = max(before[1], after[1])
    floor_delta = max(before[2], after[2])
    inside = across[1] <= floor_high and across[2] <= floor_delta
    print()
    print(f"same-build spread : {floor_low}-{floor_high} px, max delta {floor_delta}")
    print(f"across builds     : {across[0]}-{across[1]} px, max delta {across[2]}")
    print("range check       : " + ("within the same-build spread" if inside else
          "outside the same-build spread (secondary; see nearest-neighbour)"))
    if across[0] < before[0]:
        print(f"         tightest cross-build pair ({across[0]} px) beats the tightest baseline pair "
              f"({before[0]} px), which a real difference cannot do")

    # Nearest-neighbour check. Run-to-run noise here is not one spread but a few distinct states a run lands
    # in, so ranges can be blown out by pairing a run from one state with a run from another. The robust
    # question is whether every new capture sits as close to some baseline capture as the baseline captures
    # sit to each other.
    def nearest(path, pool):
        return min(compare(images[path], images[other])[0] for other in pool if other != path)
    baseline_nn = max(nearest(path, args.before) for path in args.before)
    after_nn = {path: nearest(path, args.before) for path in args.after}
    worst_after_nn = max(after_nn.values())
    print()
    print(f"nearest-neighbour : every baseline capture is within {baseline_nn} px of another baseline capture")
    for path, distance in after_nn.items():
        print(f"                    {path} is within {distance} px of a baseline capture")
    passed = worst_after_nn <= baseline_nn

    new_outliers = sorted(set(across[3]) - set(before[3]) - set(after[3]))
    if new_outliers:
        print(f"         NEW high-delta pixels not seen within either build: {new_outliers}")
    else:
        print("         no high-delta pixels beyond those the noise floor already produces")
    print()
    print("VERDICT: within the noise floor" if passed else
          "VERDICT: OUTSIDE the noise floor - a new capture is further from every baseline capture than the "
          "baseline captures are from each other; investigate")
    if new_outliers:
        print("         then print each new outlier pixel in every capture before concluding anything")
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
