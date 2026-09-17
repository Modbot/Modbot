using System.Text;
using Modbot.Shared.Names;

namespace Modbot.Core.Tests.Names;

/// <summary>
/// The two plain forms of a name: readable (plain letters, casing kept) and searchable (lower
/// case, no marks). Hand-built cases per kind of decoration, then the group's own names from
/// <c>Fixtures/display-names.tsv</c> (research 2026-09-16).
/// </summary>
public class NameNormalizerTests
{
    // ── nothing ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void NothingIsEmpty(string? name)
    {
        Assert.Equal(string.Empty, NameNormalizer.Readable(name));
        Assert.Equal(string.Empty, NameNormalizer.Searchable(name));
    }

    // ── plain names pass through ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Alice Wonder")]
    [InlineData("Bob_Builder")]
    [InlineData("8JoV9XEdpo")]
    [InlineData("pr3ttyfacee")]
    [InlineData("Dragon135_Racer")]
    [InlineData("Kuna_2.0 48f5")]
    [InlineData("cy_100%")]
    [InlineData("<3 ~ (:")]
    public void PlainAsciiIsUnchangedAndOnlyLowerCasedForSearch(string name)
    {
        Assert.Equal(name, NameNormalizer.Readable(name));
        Assert.Equal(name.ToLowerInvariant(), NameNormalizer.Searchable(name));
    }

    // ── fonts: compatibility letters ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("𝕬𝖑𝖊𝖝", "Alex")]
    [InlineData("𝒟𝒾𝓋𝒶", "Diva")]
    [InlineData("𝑳𝒐𝒕𝒖𝒔 𝒉𝒂𝒔 𝒃𝒆𝒂𝒕𝒔", "Lotus has beats")]
    [InlineData("𝟏𝟐𝟑", "123")]
    [InlineData("Ｋｅｌｌｙ Ｂｒｏｗｎ", "Kelly Brown")]
    [InlineData("＄EvilTwin＄", "$EvilTwin$")]
    [InlineData("（Air）", "(Air)")]
    [InlineData("Ⓐⓛⓔⓧ", "Alex")]
    [InlineData("🅰🅱", "AB")]
    [InlineData("①②⓪", "120")]
    [InlineData("ﬁne", "fine")]
    [InlineData("Foolsgoldᵀᴹ", "FoolsgoldTM")]
    [InlineData("bin¹", "bin1")]
    [InlineData("°.•,ᵤₛₑₗₑₛₛ,•.°", ".,useless,.")]
    [InlineData("♛𝓂𝓊𝒾𝒸𝒽𝓊𝓇ℴ♛", "muichuro")]
    [InlineData("❀ мαя¢ι ℓ", "marci l")]
    public void CompatibilityLettersBecomePlainLetters(string name, string readable)
    {
        Assert.Equal(readable, NameNormalizer.Readable(name));
        Assert.Equal(readable.ToLowerInvariant(), NameNormalizer.Searchable(name));
    }

    // ── fonts: small capitals, hooked letters and borrowed alphabets ───────────────────────

    [Theory]
    [InlineData("ꜱɪᴇɴɴᴀ", "sienna")]
    [InlineData("Kᴇɴᴢᴏ", "Kenzo")]
    [InlineData("Vᴇɴᴜs ᴅᴇ Gʀᴀᴀɴ", "Venus de Graan")]
    [InlineData("BLΛNK", "BLANK")]
    [InlineData("PΛPIƬӨ-ЯIӨ", "PAPITO-RIO")]
    [InlineData("ᄃYЯЦƧЯӨZΣ", "CYRUSROZE")]
    [InlineData("ƛƇƦƳԼƖƇ", "ACRYLIC")]
    [InlineData("ƜЄƧƬƓӇƠƧƬ", "WESTGHOST")]
    [InlineData("-_- נιмму -_-", "-_- jimmy -_-")]
    [InlineData("ღรђค๔๏ฬ ฬ๏ɭŦღ", "shadow wolT")]
    [InlineData("Ֆɨʟɛռȶʟɨɮʀǟ", "Silentlibra")]
    [InlineData("ɱყʂɬıƈąƖզųɛɛŋ", "mysticalqueen")]
    [InlineData("۷ųƖƈąཞơʂ", "vulcaros")]
    [InlineData("Mαȥαƙιҽɳ", "Mazakien")]
    [InlineData("₵ØØ₭łɆ₭ł₦₲", "COOKIEKING")]
    [InlineData("J₳₵₭", "JACK")]
    [InlineData("ɳσɱεאเ", "nomexi")]
    [InlineData("๓ყth໓໐ll", "mythdoll")]
    public void FontAlphabetsFoldToTheLettersTheyStandFor(string name, string readable)
    {
        Assert.Equal(readable, NameNormalizer.Readable(name));
        Assert.Equal(readable.ToLowerInvariant(), NameNormalizer.Searchable(name));
    }

    // ── look-alikes fold only where the word is not written in their script ─────────────────

    [Theory]
    [InlineData("Addеrаll", "Adderall")]       // Cyrillic е and а
    [InlineData("Blondiе", "Blondie")]
    [InlineData("LiΙyPad", "LilyPad")]         // Greek capital iota reads as l between small letters
    [InlineData("Sӏսt", "Slut")]               // palochka and Armenian u
    [InlineData("LОВО", "LOBO")]
    [InlineData("Кatt", "Katt")]
    [InlineData("Lυigi", "Luigi")]
    [InlineData("ԁеаԁ ԁаrling", "ԁеаԁ darling")] // a word of look-alikes alone is a Cyrillic word
    public void LookAlikesFoldInsideLatinWords(string name, string readable)
    {
        Assert.Equal(readable, NameNormalizer.Readable(name));
    }

    [Theory]
    [InlineData("|~Принцесса Кира~|", "|~принцесса кира~|")]
    [InlineData("отъебитесь", "отъебитесь")]
    [InlineData("Σωτήρης", "σωτηρης")]
    [InlineData("翔宇", "翔宇")]
    [InlineData("バハちょん", "ハハちょん")]              // the searchable form drops the dakuten with every other mark
    [InlineData("Meloz メロズ - 멜로즈", "meloz メロス - 멜로즈")]
    [InlineData("Mr John 茶", "mr john 茶")]
    [InlineData("SkyQing青蛙", "skyqing青蛙")]
    public void AWordWrittenInItsOwnScriptStays(string name, string searchable)
    {
        var readable = NameNormalizer.Readable(name);
        Assert.Equal(name, readable);
        Assert.Equal(searchable, NameNormalizer.Searchable(name));
    }

    [Fact]
    public void ArabicPresentationFormsBecomeTheLettersTheyStandFor()
    {
        Assert.Equal("MOHG قوم", NameNormalizer.Readable("MOHG ﻕﻮﻣ"));
        Assert.Equal("mohg قوم", NameNormalizer.Searchable("MOHG ﻕﻮﻣ"));
    }

    [Fact]
    public void ACapitalIOrSmallLLookAlikeReadsAsTheLettersAroundIt()
    {
        Assert.Equal("Michal", NameNormalizer.Readable("Michał"));
        Assert.Equal("Lukasz", NameNormalizer.Readable("Łukasz"));
        Assert.Equal("PAWEL", NameNormalizer.Readable("PAWEŁ"));
        Assert.Equal("l", NameNormalizer.Readable("Ɩ"));
        Assert.Equal("Il", NameNormalizer.Readable("Iӏ"));   // a small palochka is always l
        Assert.Equal("il", NameNormalizer.Readable("iӏ"));
        Assert.Equal("LILY", NameNormalizer.Readable("LƖLY"));
    }

    // ── accents and marks ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Łęmøń Çãkę", "Lemon Cake")]
    [InlineData("José", "Jose")]
    [InlineData("ŞũķũńãMẽrçÿŘøşë", "SukunaMercyRose")]
    [InlineData("Ẅøłfɓłŧê", "Wolfblte")]
    [InlineData("Straße", "Strasse")]
    [InlineData("Æon", "AEon")]
    [InlineData("Þórr", "Thorr")]
    [InlineData("M̷o̷t̷h̷M̷a̷n̷", "MothMan")]
    [InlineData("H̶O̶Z̶A̶K̶I̶", "HOZAKI")]
    [InlineData("N̲u̲c̲l̲e̲a̲r̲", "Nuclear")]
    [InlineData("F̸̐̾R̸̄̾O̶̓̓S̸̈̍T̴̉ͅ", "FROST")]
    [InlineData("ᴇͥᴄʟɪᴘsͣᴇͫ", "eclipse")]
    [InlineData("П̵у҉с̴т̛о̴т̴а̴", "Пустота")]
    public void AccentsOnLatinLettersAndStackedMarksGo(string name, string readable)
    {
        Assert.Equal(readable, NameNormalizer.Readable(name));
    }

    [Fact]
    public void AScriptsOwnMarksStayOnItsLettersInTheReadableFormAndGoFromTheSearchableOne()
    {
        // Composed, as Discord and VRChat send them.
        Assert.Equal("Кира ёж άλφα", NameNormalizer.Readable("Кира ёж άλφα"));
        Assert.Equal("кира еж αλφα", NameNormalizer.Searchable("Кира ёж άλφα"));

        // Typed as a letter and a separate mark: kept as typed, and searched without it.
        Assert.Equal("άλφα", NameNormalizer.Readable("άλφα"));
        Assert.Equal("αλφα", NameNormalizer.Searchable("άλφα"));

        // Sinhala vowel sign on its own letter.
        Assert.Equal("Detective ඩා", NameNormalizer.Readable("Detective ඩා"));
        Assert.Equal("detective ඩ", NameNormalizer.Searchable("Detective ඩා"));

        // A mark from one script on a letter of another is decoration.
        Assert.Equal("ту", NameNormalizer.Readable("туٖٛ"));
    }

    // ── invisible characters and spaces ────────────────────────────────────────────────────

    [Theory]
    [InlineData("Zero​Width‍﻿Join", "ZeroWidthJoin")]
    [InlineData("‮RTL‬", "RTL")]
    [InlineData("soft­hyphen", "softhyphen")]
    [InlineData("a️b͏c", "abc")]
    [InlineData("Momo⠀⠀", "Momo")]
    [InlineData("  a   b\t c ", "a b c")]
    [InlineData("a b　c d", "a b c d")]
    [InlineData("ㅤㅤFiller", "Filler")]
    [InlineData("tag\U000E0067s", "tags")]
    [InlineData("privateuse", "privateuse")]
    public void InvisibleCharactersGoAndAnySpaceIsOneSpace(string name, string readable)
    {
        Assert.Equal(readable, NameNormalizer.Readable(name));
        Assert.Equal(readable.ToLowerInvariant(), NameNormalizer.Searchable(name));
    }

    // ── emoji, symbols and decoration ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("Brie 💙", "Brie", "Brie 💙")]
    [InlineData("❤️𝓡𝓸𝓼𝓮❤️", "Rose", "❤️Rose❤️")]
    [InlineData("🔍🕶️🫆HEAVENLY🫆🕶️🔍", "HEAVENLY", "🔍🕶️🫆HEAVENLY🫆🕶️🔍")]
    [InlineData("Steak™", "Steak", "Steak™")]
    [InlineData("ɴɪɴᴇ©", "nine", "nine©")]
    [InlineData("♡Berrius♡", "Berrius", "♡Berrius♡")]
    [InlineData("Nova ★", "Nova", "Nova ★")]
    [InlineData("༒ Blink ༒", "Blink", "Blink")]
    [InlineData("『Shxdow』", "Shxdow", "Shxdow")]
    [InlineData("≻Black≺", ">Black<", ">Black<")]
    [InlineData("×Obsidian×", "Obsidian", "Obsidian")]
    [InlineData("˗ˏˋ M a o ˎˊ˗", "- M a o -", "- M a o -")]
    [InlineData("Kirby˸", "Kirby:", "Kirby:")]
    [InlineData("paradox ≺3", "paradox <3", "paradox <3")]
    [InlineData("Boba․tea ad9d", "Boba.tea ad9d", "Boba.tea ad9d")]
    [InlineData("👨‍👩‍👧 Fam", "Fam", "👨‍👩‍👧 Fam")]
    public void SymbolsGoUnlessEmojiAreKept(string name, string readable, string withEmoji)
    {
        Assert.Equal(readable, NameNormalizer.Readable(name));
        Assert.Equal(withEmoji, NameNormalizer.Readable(name, keepEmoji: true));
        Assert.Equal(readable.ToLowerInvariant(), NameNormalizer.Searchable(name));
    }

    [Theory]
    [InlineData("ღXylaaღ", "Xylaa")]           // a Georgian letter used as a heart
    [InlineData("Dolly ღ", "Dolly")]
    [InlineData("ღ ೄྀSpazz ೄྀღ", "Spazz")]
    [InlineData("Asherツ", "Asher")]           // a lone kana in a Latin word is a smiley
    [InlineData("Iglo ツ", "Iglo ツ")]          // alone in its own word it is a word
    [InlineData("亞Rebel亞", "Rebel")]          // the same character either side is ornament
    [InlineData("ఌ༅Cσȥყ_Qυιɳɳఌ༅", "Cozy_Quinn")]
    [InlineData("Nixxy ˶ᵕᆺᵕ˶", "Nixxy")]
    [InlineData("Ela ʚïɞ", "Ela i")]
    [InlineData("ꌗꀍꀤꃴꋪꏹꋊ", "")]                // Yi letters as a font: ornament, nothing left
    [InlineData("𐔌.⋮Alex.ᐟ ֹ₊꒱", ".Alex. +")]
    [InlineData("༝༚༝༚༝༚", "")]
    [InlineData("̱̱̱̱̱̱̱̱̱̱", "")]
    public void LettersUsedAsDecorationGo(string name, string readable)
    {
        Assert.Equal(readable, NameNormalizer.Readable(name));
    }

    // ── digits ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DigitsOfOtherScriptsAreDigitsInTheirOwnWordsAndBowsElsewhere()
    {
        Assert.Equal("สมชาย25", NameNormalizer.Readable("สมชาย๒๕"));
        Assert.Equal("Miyu", NameNormalizer.Readable("୨୧Miyu୨୧"));
        Assert.Equal("୨୧Miyu୨୧", NameNormalizer.Readable("୨୧Miyu୨୧", keepEmoji: true));
        Assert.Equal("nuraika", NameNormalizer.Readable("༒୨୧ɴᴜʀᴀɪᴋᴀ୨୧༒"));
    }

    // ── never throws, always the same answer ───────────────────────────────────────────────

    [Theory]
    [InlineData("\ud800abc")]
    [InlineData("abc\udc00")]
    [InlineData("􏿿\ud800")]
    [InlineData("\0")]
    public void BrokenInputIsCleanedNotThrown(string name)
    {
        var readable = NameNormalizer.Readable(name);
        Assert.DoesNotContain(readable, c => char.IsSurrogate(c) || char.IsControl(c));
        Assert.Equal(readable.ToLowerInvariant(), NameNormalizer.Searchable(name));
    }

    [Fact]
    public void EveryCodePointIsAccepted()
    {
        var all = new StringBuilder();
        for (var cp = 0; cp <= 0x10FFFF; cp++)
        {
            if (cp is >= 0xD800 and <= 0xDFFF)
                continue;
            all.Append(char.ConvertFromUtf32(cp));
            if (cp % 64 == 63)
                all.Append(' ');
        }

        var text = all.ToString();
        var readable = NameNormalizer.Readable(text);
        var searchable = NameNormalizer.Searchable(text);
        var withEmoji = NameNormalizer.Readable(text, keepEmoji: true);

        Assert.NotEmpty(readable);
        Assert.NotEmpty(searchable);
        Assert.True(withEmoji.Length >= readable.Length);
        Assert.DoesNotContain("  ", readable, StringComparison.Ordinal);
        Assert.DoesNotContain(readable, char.IsControl);
    }

    [Fact]
    public void ALongNameOfEverythingIsFineAndDeterministic()
    {
        var piece = "𝕬𝖑𝖊𝖝 Addеrаll ༒ M̷o̷t̷h̷ Принцесса 翔宇 ꜱɪᴇɴɴᴀ 💙 ";
        var name = string.Concat(Enumerable.Repeat(piece, 500));

        var first = NameNormalizer.Readable(name);
        var again = NameNormalizer.Readable(name);
        Assert.Equal(first, again);
        Assert.Equal(NameNormalizer.Searchable(name), NameNormalizer.Searchable(name));
        Assert.StartsWith("Alex Adderall Moth Принцесса 翔宇 sienna Alex", first, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSearchableFormIsStable()
    {
        foreach (var (name, _, _, searchable) in Fixture())
        {
            Assert.Equal(searchable, NameNormalizer.Searchable(searchable));
            Assert.Equal(searchable, NameNormalizer.Searchable(NameNormalizer.Readable(name)));
        }
    }

    // ── the group's own names ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheRealNamesComeOutAsReviewed()
    {
        var rows = Fixture();
        Assert.True(rows.Count > 150, "the fixture should carry the curated names");

        var wrong = new List<string>();
        foreach (var (name, readable, withEmoji, searchable) in rows)
        {
            var r = NameNormalizer.Readable(name);
            var e = NameNormalizer.Readable(name, keepEmoji: true);
            var s = NameNormalizer.Searchable(name);
            if (r != readable || e != withEmoji || s != searchable)
                wrong.Add($"{name}: readable '{r}' (want '{readable}'), with emoji '{e}' (want '{withEmoji}'), searchable '{s}' (want '{searchable}')");
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    private static List<(string Name, string Readable, string WithEmoji, string Searchable)> Fixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "display-names.tsv");
        var rows = new List<(string, string, string, string)>();
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            var parts = line.Split('\t');
            Assert.Equal(4, parts.Length);
            rows.Add((parts[0], parts[1], parts[2], parts[3]));
        }

        return rows;
    }
}
