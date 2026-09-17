#!/usr/bin/env python3
"""Makes the two tables NameNormalizer works from, as C# source in src/Modbot.Shared/Names/.

    python tools/name-folding/generate-folding-tables.py UnicodeData.txt confusables.txt

Both inputs come from unicode.org and are not kept in the repository:

- UnicodeData.txt for the Unicode version .NET's own character tables are on
  (https://www.unicode.org/Public/16.0.0/ucd/UnicodeData.txt for .NET 10). It gives the
  decompositions: the plain letters behind mathematical, full-width, circled, superscript and
  ligature letters, and the base letter behind an accented one. Modbot builds with invariant
  globalization, where string.Normalize hands non-ASCII text back unchanged, so the table stands
  in for it.
- confusables.txt from UTS #39 (https://www.unicode.org/Public/security/latest/confusables.txt).
  It lists characters that look like another character. Only the entries that resolve to one
  printable ASCII character are kept.

On top of those, the hand tables in this file: the alphabets people paste from "font" generators
(small capitals, hooked and stroked letters, and the Greek, Cyrillic, Thai, Hebrew, Armenian and
Georgian letters those alphabets borrow because they look like a Latin letter) and the plain
spelling of Latin letters that have no accent to strip (ø, ł, æ, ß).

The research behind the hand tables is .agent/research/2026-09-16-display-name-findings.md.
"""
import io
import os
import sys

# --- hand tables ---------------------------------------------------------------------------

# Letters a name generator uses for a Latin letter. Source -> the letter it stands for. Case is
# literal: an entry mapping to "A" is a capital-looking letter. An empty target drops the
# character: those are letters used as decoration (a Georgian ღ is a heart, ʚ and ɞ are the wings
# of a butterfly face).
FONT: dict[str, str] = {}


def add(pairs: str) -> None:
    for pair in pairs.split():
        src, dst = pair.split("=", 1)
        assert len(src) == 1, pair
        assert all(ord(c) < 128 for c in dst), pair
        assert src not in FONT or FONT[src] == dst, (pair, FONT[src])
        FONT[src] = dst


# Small capitals ("ꜱᴍᴀʟʟ ᴄᴀᴘꜱ"). Lowercase, because that is how they read.
add("ᴀ=a ʙ=b ᴄ=c ᴅ=d ᴇ=e ꜰ=f ғ=f ɢ=g ʜ=h ɪ=i ᴊ=j ᴋ=k ʟ=l ᴍ=m ɴ=n ᴏ=o ᴘ=p ǫ=q ʀ=r ꜱ=s ᴛ=t ᴜ=u ᴠ=v ᴡ=w ʏ=y ᴢ=z ᴧ=a")
# Capital letters borrowed from other scripts ("PΛPIƬӨ", "ᄃYЯЦƧЯӨZΣ", "BLΛNK", "ƛƇƦƳԼƖƇ").
add("Λ=A Δ=A ƛ=A Σ=E Ξ=E Я=R Ө=O Θ=O Ƨ=S Ƭ=T Ц=U П=N Π=N И=N Щ=W Ш=W ᄃ=C Ψ=Y Ʀ=R Ƴ=Y Լ=L Ƈ=C Ɓ=B Ɗ=D Ɠ=G Ƙ=K Ɲ=N Ƥ=P Ʈ=T Ʋ=V Ƶ=Z Ɯ=W Ɛ=E Ʃ=S Ʒ=Z "
    "Ǥ=G Ⱥ=A Ɽ=R Ꝗ=Q Ɇ=E Ɵ=O Ɔ=O Ǝ=E Ə=E Ƒ=F Ɣ=Y Ʊ=U Ʉ=U Ɏ=Y Ȼ=C Ƀ=B Ɉ=J Ɍ=R ʘ= Ꙇ=I Ӏ=I Ԍ=G Ԋ=H Ӈ=H Ծ=O Ս=U Ֆ=S Ђ=D Є=E ϴ=O Ͼ=C")
# The cute lowercase alphabet ("αвc∂єfgнιנкℓмησρqяѕтυνωχуz").
add("α=a в=b ∂=d є=e н=h ι=i נ=j к=k м=m η=n σ=o ρ=p я=r ѕ=s т=t υ=u ν=v ω=w χ=x у=y")
# The Thai-looking alphabet ("ค๒ς๔єŦgђเןкl๓ภ๏קợгรtยשฬאץչ") and the Lao digits used with it.
add("ค=a ๒=b ς=c ๔=d ђ=h เ=i ן=j ๓=m ภ=n ๏=o ק=p г=r ร=s ย=u ש=v ฬ=w א=x ץ=y չ=z น=u ๖=b ໐=o ໓=d ໒=b ຮ=s ຖ=n")
# The rune-looking alphabet ("ǟɮƈɖɛʄɢɦɨʝӄʟʍռօքզʀֆȶʊʋաӼʏʐ").
add("ɮ=b ƈ=c ɖ=d ɛ=e ʄ=f ɦ=h ɨ=i ʝ=j ӄ=k ʍ=m ռ=n օ=o ք=p զ=q ֆ=s ȶ=t ʊ=u ʋ=v ա=w Ӽ=x ʐ=z ս=u")
# The curly alphabet ("ąɓƈɖɛʄɠɧıʝƙƖɱŋơ℘զཞʂɬųʋῳҳყʑ").
add("ɓ=b ɠ=g ɧ=h ı=i ƙ=k ɱ=m ŋ=n ơ=o ℘=p ཞ=r ʂ=s ɬ=t ų=u ۷=v ҳ=x ყ=y ʑ=z")
# The Greek-looking alphabet ("αႦƈԃҽϝɠԋιʝƙʅɱɳσρϙɾʂƚυʋɯxყȥ").
add("Ⴆ=b ԃ=d ҽ=e ϝ=f ԋ=h ʅ=l ɳ=n ϙ=q ɾ=r ƚ=t ɯ=w ȥ=z")
# The currency alphabet ("₳฿₵ĐɆ₣₲ĦłJ₭Ł₥₦Ø₱Q₹₴₮ɄV₩ӾɎƵ").
add("₳=A ฿=B ₵=C ₣=F ₲=G ₭=K ₥=M ₦=N ₱=P ₴=S ₮=T ₩=W Ӿ=X €=E £=L ¥=Y ¢=c ₤=L ₸=T ₡=C")
# Other Greek letters used for the Latin letter they resemble.
add("β=b γ=y δ=d ε=e ζ=z κ=k μ=u π=n τ=t θ=o φ=o ψ=y ϐ=b ϵ=e ϱ=p ϲ=c ϳ=j ς=c ϟ=s")
# Other Cyrillic letters used for the Latin letter they resemble.
add("ц=u щ=w ш=w ь=b э=e з=z и=n п=n ђ=h ԁ=d ԝ=w ӏ=l ԛ=q ԍ=g ӈ=h")
# Armenian letters used the same way, and Georgian ღ, which is a heart.
add("ղ=n ո=n ղ=n ღ=")
# Faces. ᴖ and ᴗ are what ᵔ and ᵕ decompose to.
add("ʚ= ɞ= ᴥ= ᴖ= ᴗ=")
# Symbols used as a letter, and symbols that stand for an ASCII one.
add("§=S Ω=O ≺=< ≻=> ∗=* ⋆=* ‧=. ・=. ･=. ｡=. 。=. 、=, ⁄=/")
# IPA letters that read as a Latin letter.
add("ɑ=a ɔ=o ɕ=c ɗ=d ɘ=e ə=e ɜ=e ɟ=j ɡ=g ɣ=y ɥ=h ɩ=i ɫ=l ɭ=l ɲ=n ɵ=o ɹ=r ɺ=r ɻ=r ɼ=r ɽ=r ɿ=r ʁ=r ʃ=s ʇ=t ʈ=t ʉ=u ʌ=v ʎ=y ʒ=z ʓ=z ʗ=c ʛ=g "
    "ʞ=k ʠ=q ʮ=h ʯ=h ƞ=n ƫ=t ƭ=t ƥ=p ƴ=y ƶ=z ƒ=f ɍ=r ɇ=e ɉ=j ɏ=y ȼ=c ƀ=b ǥ=g ꝗ=q ꝁ=k ⱥ=a ɐ=a ɒ=a")
# Enclosed capitals that have no decomposition: negative squared 🅰 and negative circled 🅐.
for i in range(26):
    add(f"{chr(0x1F170 + i)}={chr(65 + i)}")
    add(f"{chr(0x1F150 + i)}={chr(65 + i)}")
# Circled zero and the negative circled digits.
add("⓪=0 ⓿=0 ❶=1 ❷=2 ❸=3 ❹=4 ❺=5 ❻=6 ❼=7 ❽=8 ❾=9 ➀=1 ➁=2 ➂=3 ➃=4 ➄=5 ➅=6 ➆=7 ➇=8 ➈=9 ➊=1 ➋=2 ➌=3 ➍=4 ➎=5 ➏=6 ➐=7 ➑=8 ➒=9")

# Latin letters with no accent to strip but a plain spelling.
PLAIN: dict[str, str] = {}
for pair in ("æ=ae Æ=AE œ=oe Œ=OE ß=ss ẞ=SS ø=o Ø=O ł=l Ł=L đ=d Đ=D ħ=h Ħ=H ŧ=t Ŧ=T ı=i ĸ=k ð=d Ð=D þ=th Þ=Th ŋ=n Ŋ=N ſ=s "
             "ƀ=b Ƀ=B ȼ=c Ȼ=C ɇ=e Ɇ=E ɨ=i Ɨ=I ɉ=j Ɉ=J ɍ=r Ɍ=R ʉ=u Ʉ=U ɏ=y Ɏ=Y ƶ=z Ƶ=Z ǥ=g Ǥ=G ǀ=l ǁ=ll ǃ=! "
             "ĳ=ij Ĳ=IJ ŉ=n Ŀ=L ŀ=l ƪ=s ʼ=' ʻ=' ʽ='").split():
    s, d = pair.split("=", 1)
    PLAIN[s] = d

# Letters that could be a capital I or a small l. Which one is decided by the letters around them.
CAPITAL_I_OR_SMALL_L = set("ƖΙІӀⅠꓲǀł")

# --- Unicode data --------------------------------------------------------------------------

CATEGORY: dict[int, str] = {}
NAME: dict[int, str] = {}
DECOMP_RAW: dict[int, str] = {}


def load_unicode_data(path: str) -> None:
    with io.open(path, encoding="utf-8") as f:
        for line in f:
            fields = line.rstrip("\n").split(";")
            if len(fields) < 6:
                continue
            cp = int(fields[0], 16)
            NAME[cp] = fields[1]
            CATEGORY[cp] = fields[2]
            if fields[5]:
                DECOMP_RAW[cp] = fields[5]


def category(cp: int) -> str:
    return CATEGORY.get(cp, "Cn")


def is_mark(cp: int) -> bool:
    return category(cp).startswith("M")


def decompose(cp: int) -> tuple[list[int], bool]:
    """The full decomposition of cp (itself when it has none), and whether any step was a
    compatibility mapping rather than a canonical one."""
    raw = DECOMP_RAW.get(cp)
    if raw is None:
        return [cp], False
    compat = raw.startswith("<")
    parts = raw.split()
    if compat:
        parts = parts[1:]
    out: list[int] = []
    for p in parts:
        sub, sub_compat = decompose(int(p, 16))
        out.extend(sub)
        compat = compat or sub_compat
    return out, compat


# Blocks the decomposition table leaves out: whole-word compatibility forms (㍿, ㏒, ™, ﷺ) that
# would turn one symbol into letters, and CJK compatibility ideographs and radicals. Hangul
# syllables are not in UnicodeData individually and are left composed.
DECOMPOSITION_SKIPPED_RANGES = (
    (0x2E80, 0x2FDF), (0x3200, 0x33FF), (0xF900, 0xFAFF), (0xFDF0, 0xFDFF),
    (0x1F200, 0x1F2FF), (0x2F800, 0x2FA1F),
)
DECOMPOSITION_SKIPPED = {0x2120, 0x2121, 0x2122, 0x2139, 0x00BC, 0x00BD, 0x00BE, 0x2153, 0x2154,
                         0x2155, 0x2156, 0x2157, 0x2158, 0x2159, 0x215A, 0x215B, 0x215C, 0x215D,
                         0x215E, 0x215F}


def skip_decomposition(cp: int) -> bool:
    if cp in DECOMPOSITION_SKIPPED:
        return True
    return any(lo <= cp <= hi for lo, hi in DECOMPOSITION_SKIPPED_RANGES)


# Script classes, the same as NameScripts.cs. Only the Latin/Common distinction matters here: a
# canonical composition whose base letter is not Latin is kept composed in the readable form.
LATIN_RANGES = (
    (0x0041, 0x005A), (0x0061, 0x007A), (0x00AA, 0x00AA), (0x00BA, 0x00BA), (0x00C0, 0x024F),
    (0x0250, 0x02AF), (0x1D00, 0x1DBF), (0x1E00, 0x1EFF), (0x2071, 0x2071), (0x207F, 0x207F),
    (0x2090, 0x209C), (0x2100, 0x214F), (0x2460, 0x24FF), (0x2C60, 0x2C7F), (0xA720, 0xA7FF),
    (0xAB30, 0xAB6F), (0xFB00, 0xFB06), (0xFF21, 0xFF3A), (0xFF41, 0xFF5A), (0x1D400, 0x1D7FF),
    (0x1F100, 0x1F1FF),
)


def is_latin_or_common(cp: int) -> bool:
    if cp < 0x80:
        return True
    if any(lo <= cp <= hi for lo, hi in LATIN_RANGES):
        return True
    return not category(cp).startswith(("L", "N"))


# --- resolving ---------------------------------------------------------------------------

def hand(cp: int) -> str | None:
    c = chr(cp)
    if c in FONT:
        return FONT[c]
    if c in PLAIN:
        return PLAIN[c]
    return None


def strip_marks_and_quotes(cps: list[int]) -> list[int]:
    return [c for c in cps if not is_mark(c) and c not in (0x27, 0x2BC, 0x2BB, 0x2BD)]


def to_ascii(cps: list[int], depth: int = 0) -> str | None:
    """A sequence of code points folded to ASCII through the hand tables and decompositions, or
    None when any of it has no plain form."""
    if depth > 4:
        return None
    out: list[str] = []
    for cp in cps:
        if cp < 0x80:
            out.append(chr(cp))
            continue
        h = hand(cp)
        if h is not None:
            out.append(h)
            continue
        d, _ = decompose(cp)
        d = strip_marks_and_quotes(d)
        if d == [cp] or not d:
            return None
        r = to_ascii(d, depth + 1)
        if r is None:
            return None
        out.append(r)
    return "".join(out)


def main(unicode_data_path: str, confusables_path: str, out_dir: str) -> None:
    load_unicode_data(unicode_data_path)

    # -- the decomposition table --
    decompositions: dict[int, tuple[list[int], bool]] = {}
    for cp in sorted(DECOMP_RAW):
        if skip_decomposition(cp) or category(cp) in ("Cn", "Cs", "Co"):
            continue
        full, compat = decompose(cp)
        if full == [cp]:
            continue
        keep_composed_in_readable = (not compat) and not is_latin_or_common(full[0])
        decompositions[cp] = (full, keep_composed_in_readable)

    # -- the folding table --
    version = "unknown"
    table: dict[int, str] = {}
    origin: dict[int, str] = {}
    ambiguous: set[int] = set()

    with io.open(confusables_path, encoding="utf-8") as f:
        for line in f:
            if line.startswith("# Version:"):
                version = line.split(":", 1)[1].strip()
            if line.startswith("#") or not line.strip():
                continue
            src, dst, kind = [p.strip() for p in line.split("#")[0].split(";")][:3]
            if kind != "MA":
                continue
            scp = int(src, 16)
            if scp < 0x80:
                continue  # ASCII is never changed
            cat = category(scp)
            if cat.startswith(("M", "C", "Z")) or cat == "Nd":
                continue  # a digit of another script is that digit, whatever it looks like
            if hand(scp) is not None:
                continue  # the hand tables win
            own, _ = decompose(scp)
            own_ascii = to_ascii(strip_marks_and_quotes(own))
            if own_ascii is not None and own != [scp]:
                continue  # its own decomposition already folds it
            prototype = [int(p, 16) for p in dst.split()]
            r = to_ascii(strip_marks_and_quotes(prototype))
            if r is None or len(r) != 1 or not (0x20 < ord(r) < 0x7F):
                continue
            name = NAME.get(scp, "")
            letterish = cat[0] in "LN"
            if r.isalnum():
                if not letterish:
                    continue  # a symbol never becomes a letter or digit
                if r == "l":
                    if "ONE" in name:
                        r = "1"
                    elif cat == "Lu" or "CAPITAL" in name:
                        r = "I"
                elif r in "Oo" and ("ZERO" in name or "DIGIT" in name):
                    r = "0"
                if r.isalpha():
                    if cat == "Ll" or "SMALL" in name:
                        r = r.lower()
                    elif cat == "Lu":
                        r = r.upper()
            elif letterish:
                continue  # a letter never becomes punctuation
            table[scp] = r
            origin[scp] = "uts39"

    for c, d in FONT.items():
        table[ord(c)] = d
        origin[ord(c)] = "font"
    for c, d in PLAIN.items():
        table[ord(c)] = d
        origin[ord(c)] = "plain"

    # A composed letter whose base letter folds (ї, й, ё, ά) folds the same way. The readable form
    # keeps it composed, so it never sees the base letter on its own.
    for cp, (full, keep_composed) in decompositions.items():
        if not keep_composed or cp in table:
            continue
        base = [c for c in full if not is_mark(c)]
        if len(base) != 1 or base[0] not in table:
            continue
        folded = table[base[0]]
        if category(cp) == "Lu" and len(folded) == 1:
            folded = folded.upper()
        table[cp] = folded
        origin[cp] = "derived"

    for c in CAPITAL_I_OR_SMALL_L:
        cp = ord(c)
        if cp in table and table[cp] in ("I", "l"):
            ambiguous.add(cp)
    for cp, v in table.items():
        if v == "l" and category(cp) == "Lu":
            ambiguous.add(cp)
    for cp in ambiguous:
        table[cp] = "l"

    counts: dict[str, int] = {}
    for cp in table:
        counts[origin[cp]] = counts.get(origin[cp], 0) + 1

    write_folding(os.path.join(out_dir, "NameFoldingTable.cs"), table, ambiguous, version, counts)
    write_decompositions(os.path.join(out_dir, "NameDecompositionTable.cs"), decompositions)
    print(f"folding: {len(table)} entries ({counts}), {len(ambiguous)} I-or-l; "
          f"decompositions: {len(decompositions)}; confusables {version}", file=sys.stderr)


def cs_string(cps: list[int] | str) -> str:
    if isinstance(cps, str):
        cps = [ord(c) for c in cps]
    out = []
    for cp in cps:
        if cp == 0x22:
            out.append('\\"')
        elif cp == 0x5C:
            out.append("\\\\")
        elif 0x20 <= cp < 0x7F:
            out.append(chr(cp))
        elif cp < 0x10000:
            out.append(f"\\u{cp:04X}")
        else:
            out.append(f"\\U{cp:08X}")
    return '"' + "".join(out) + '"'


def rows(items: list[str], per_row: int, indent: str = "        ") -> list[str]:
    return [indent + ", ".join(items[i:i + per_row]) + "," for i in range(0, len(items), per_row)]


HEADER = [
    "// <auto-generated>",
    "// Made by tools/name-folding/generate-folding-tables.py. Do not edit by hand: change the",
    "// generator, or the hand tables in it, and run it again.",
]


def write_folding(path: str, table: dict[int, str], ambiguous: set[int], version: str, counts: dict[str, int]) -> None:
    keys = sorted(table)
    amb = sorted(ambiguous)
    lines = HEADER + [
        "//",
        f"// Sources: the generator's hand tables ({counts.get('font', 0)} font-alphabet letters and",
        f"// {counts.get('plain', 0)} plain spellings of Latin letters), Unicode's confusables.txt,",
        f"// UTS #39 version {version}, of which the {counts.get('uts39', 0)} entries that resolve to one",
        f"// printable ASCII character are kept, and {counts.get('derived', 0)} composed letters whose base",
        "// letter is in one of those. See .agent/research/2026-09-16-display-name-findings.md.",
        "// </auto-generated>",
        "",
        "namespace Modbot.Shared.Names;",
        "",
        "/// <summary>Look-alike characters and the plain ASCII character each one stands for.</summary>",
        "internal static class NameFoldingTable",
        "{",
        "    /// <summary>The confusables.txt version the table was made from.</summary>",
        f'    public const string ConfusablesVersion = "{version}";',
        "",
        "    /// <summary>Code points that fold, ascending.</summary>",
        "    public static ReadOnlySpan<int> Keys =>",
        "    [",
        *rows([f"0x{k:04X}" for k in keys], 10),
        "    ];",
        "",
        "    /// <summary>What each key folds to, in the order of <see cref=\"Keys\"/>. Empty means the character is dropped.</summary>",
        "    public static readonly string[] Values =",
        "    [",
        *rows([cs_string(table[k]) for k in keys], 12),
        "    ];",
        "",
        "    /// <summary>Keys that could be a capital I or a small l; the letters around them decide.</summary>",
        "    public static ReadOnlySpan<int> CapitalIOrSmallL =>",
        "    [",
        *rows([f"0x{k:04X}" for k in amb], 10),
        "    ];",
        "}",
        "",
    ]
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))


def write_decompositions(path: str, decompositions: dict[int, tuple[list[int], bool]]) -> None:
    keys = sorted(decompositions)
    lines = HEADER + [
        "//",
        "// Source: UnicodeData.txt, Unicode 16.0.0 (the version .NET 10's character tables are on),",
        "// every decomposition expanded in full, without the CJK compatibility, Arabic presentation",
        "// and whole-word blocks. See the generator for what is left out and why.",
        "// </auto-generated>",
        "",
        "namespace Modbot.Shared.Names;",
        "",
        "/// <summary>The full decomposition of every character that has one.</summary>",
        "internal static class NameDecompositionTable",
        "{",
        "    /// <summary>Code points that decompose, ascending.</summary>",
        "    public static ReadOnlySpan<int> Keys =>",
        "    [",
        *rows([f"0x{k:04X}" for k in keys], 10),
        "    ];",
        "",
        "    /// <summary>The decomposition of each key, in the order of <see cref=\"Keys\"/>.</summary>",
        "    public static readonly string[] Values =",
        "    [",
        *rows([cs_string(decompositions[k][0]) for k in keys], 8),
        "    ];",
        "",
        "    /// <summary>",
        "    /// One per key: 1 when the key is a canonical composition of a non-Latin letter and its",
        "    /// marks (й, ά), which the readable form keeps as it is; 0 otherwise.",
        "    /// </summary>",
        "    public static ReadOnlySpan<byte> KeepsComposedWhenReadable =>",
        "    [",
        *rows(["1" if decompositions[k][1] else "0" for k in keys], 40),
        "    ];",
        "}",
        "",
    ]
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))


if __name__ == "__main__":
    here = os.path.dirname(os.path.abspath(__file__))
    out = os.path.normpath(os.path.join(here, "..", "..", "src", "Modbot.Shared", "Names"))
    main(sys.argv[1], sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else out)
