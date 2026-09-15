"""WCAG 2 contrast ratios for a palette. Usage: python contrast.py '#fg' '#bg' [...pairs]"""
import sys


def lum(hex_: str) -> float:
    h = hex_.lstrip("#")
    r, g, b = (int(h[i : i + 2], 16) / 255 for i in (0, 2, 4))
    lin = lambda c: c / 12.92 if c <= 0.03928 else ((c + 0.055) / 1.055) ** 2.4
    return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b)


def ratio(fg: str, bg: str) -> float:
    a, b = lum(fg), lum(bg)
    hi, lo = max(a, b), min(a, b)
    return (hi + 0.05) / (lo + 0.05)


if __name__ == "__main__":
    args = sys.argv[1:]
    for fg, bg in zip(args[::2], args[1::2]):
        r = ratio(fg, bg)
        grade = "AAA" if r >= 7 else "AA" if r >= 4.5 else "AA-large" if r >= 3 else "fail"
        print(f"{fg} on {bg}: {r:.2f}:1 {grade}")
