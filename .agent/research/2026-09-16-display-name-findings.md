# Display names — what people actually put in them

- **Source:** the live Modbot at the group's own deployment, read on 2026-09-16 through
  `GET /api/members?status=all` (4,750 VRChat members, current and past) and
  `GET /api/discord/members?state=all` (800 Discord members). Read-only.
- **What was read:** 7,175 name values -- 4,750 VRChat display names; 800 Discord usernames, 800
  Discord display names, 746 global names and 79 server nicknames.
- **Fixture:** `fixtures/display-names-2026-09-16.txt`, the distinctive real names, one per line.
  The same names with the forms the normalizer must produce for them are the test fixture
  `tests/Modbot.Core.Tests/Fixtures/display-names.tsv`. They are the group's own members' names:
  they stay in this folder and in the test, and go nowhere else.
- **What it fed:** `NameNormalizer` in `src/Modbot.Shared/Names/` and the hand tables in
  `tools/name-folding/generate-folding-tables.py`.

---

## 1. Headline numbers

| | Count |
|---|---|
| Name values read | 7,175 |
| Distinct names | 6,207 |
| Names with any character outside ASCII | 787 (11%) |
| Distinct names the normalizer changes | 695 |
| VRChat names ending in a space and four hex characters (`Boba․tea ad9d`) | 687 of 4,750 (14%) -- VRChat's own suffix, left alone |
| Names that are only decoration and fold to nothing (`༝༚༝༚༝༚`, ten combining marks) | 3 |

The remaining 89% are plain ASCII and pass through the normalizer unchanged apart from case in
the searchable form.

## 2. What the non-ASCII names are made of

Counted per name (a name is counted once per category it uses) over all 7,175 values, then the
characters themselves.

| Category | Names | Characters | Most common | Handled by |
|---|---|---|---|---|
| Accented and stroked Latin letters (`Łęmøń Çãkę`, `ŞũķũńãMẽrçÿŘøşë`, `Ř€B€€ČǍ`) | 187 | 400 (130 distinct) | ° ×40 (decoration), ø ×24, ı ×14, Ø ×12, ï ×10, ƈ ×9 | decomposition, plain-spelling table (ø ł đ æ ß), hooked-letter table (ƈ Ƭ Ɩ ƙ) |
| One dot leader U+2024 `․` in place of a full stop (`Boba․tea`, `Freddie․․․․․`) | 115 | 130 | VRChat appears to substitute it for `.` | decomposition (→ `.`) |
| Emoji and pictographic symbols (`Brie 💙`, `♡Berrius♡`, `Nova ★`) | 102 | 187 (55 distinct) | ♡ ×16, ✨ ×8, 🐾 ×8, ❤ ×7, ☆ ×6 | dropped; kept with `keepEmoji` |
| Small capitals and phonetic letters (`ꜱɪᴇɴɴᴀ`, `Vᴇɴᴜs ᴅᴇ Gʀᴀᴀɴ`, `Kᴇɴᴢᴏ`) | 91 | 423 (42 distinct) | ᴀ ×49, ɪ ×40, ɴ ×39, ᴇ ×33, ʀ ×27 | small-capitals table |
| CJK: kana, kanji, hangul, bopomofo (`Meloz メロズ - 멜로즈`, `翔宇`, `Asherツ`) | 73 | 199 (117 distinct) | ツ ×9 (a smiley), 『』 ×5 (brackets) | kept when it is the word; a lone kana beside Latin letters is decoration |
| IPA and modifier letters used as a font (`GɾιȥȥʅყDιρρҽɾ69`, `ɱყʂɬıƈąƖզųɛɛŋ`) and spacing modifiers as decoration (`˚` `˶` `˸`) | 73 | 176 (46 distinct) | ˚ ×22, ʚ ×11, ɞ ×11, ɨ ×8, ɛ ×8 | font tables; modifier letters dropped |
| Cyrillic (`Addеrаll`, `BӨПG`, `PΛPIƬӨ-ЯIӨ`, `Принцесса Кира`) | 72 | 185 (48 distinct) | т ×12, є ×11, а ×10, у ×10, ѕ ×10, П ×9 | confusables + font tables; a word written in Cyrillic stays |
| Greek (`BLΛNK`, `Lυigi`, `~ 「 κηασs 」 ~`, `Mαȥαƙιҽɳ`) | 72 | 143 (33 distinct) | α ×27, ι ×20, Λ ×10, Σ ×8, υ ×8 | confusables + font tables |
| Tibetan marks used as ornament (`༒ Blink ༒`, `Vesper༄`, `Mocha༊`) | 70 | 138 | ༒ ×105, ༄ ×7, ꧂ ×6 | dropped (punctuation) |
| Mathematical alphanumerics (`𝕬𝖑𝖊𝖝`, `𝒟𝒾𝓋𝒶`, `☾ 𝑽𝒆𝒏𝒖𝒔 𝒅𝒆 𝑮𝒓𝒂𝒂𝒏 ☽`) | 68 | 452 (131 distinct) | script and fraktur lower case | decomposition |
| Georgian and Armenian letters as a font (`ღXylaaღ`, `Ֆɨʟɛռȶʟɨɮʀǟ`, `ֆքօօӄʏ`) | 48 | 77 | ღ ×50 (a heart), ռ ×6, ყ ×6, ֆ ×5 | font tables; ღ is dropped |
| Full-width letters and punctuation (`Ｋｅｌｌｙ Ｂｒｏｗｎ`, `＄EvilTwin＄`, `（Air）`) | 48 | 245 (63 distinct) | ｏ ×19, ｅ ×18, ｉ ×16 | decomposition |
| Combining marks: strike-through and zalgo (`M̷o̷t̷h̷M̷a̷n̷`, `F̸̐̾R̸̄̾O̶̓̓S̸̈̍T̴̉ͅ`, `N̲u̲c̲l̲e̲a̲r̲`) | 43 | 191 (40 distinct) | U+0337 ×38, U+0336 ×25, U+0310 ×12, U+0331 ×11, U+FE0F ×10 | dropped; a script's own marks stay on its own letters |
| Letters from scripts used only as ornament: cuneiform 𒈞, hieroglyphs 𓆩𓆪, Yi ꌗꀍꀤ, syllabics ᐟ, Bamum 𖤐 | 28 | 55 | | dropped as decoration |
| Tibetan brackets `༺ ༻` | 18 | 36 | | dropped |
| Arrows and math operators as brackets (`≻Black≺`, `∗Tate∗`, `⋆`) | 18 | 27 | ≺ ×8, ∗ ×8, ⋆ ×6 | folded to `<`, `>`, `*` |
| Letterlike symbols (`Steak™`, `♛𝓂𝓊𝒾𝒸𝒽𝓊𝓇ℴ♛`, `❀ мαя¢ι ℓ`) | 14 | 18 | ™ ×4, ℴ ×4, ℙ, ℐ, ℓ, ℯ, ℎ | decomposition; ™ dropped rather than becoming `TM` |
| Superscripts and subscripts (`°.•,ᵤₛₑₗₑₛₛ,•.°`, `Foolsgoldᵀᴹ`, `bin¹`) | 12 | 24 | ₊ ×7, ₛ ×6 | decomposition |
| Currency signs as letters (`J₳₵₭`, `₵ØØ₭łɆ₭ł₦₲`, `90s_BADDI€`) | 11 | 23 | € ×5, ₦ ×5, ₭ ×4 | currency-font table |
| Other-script digits as bows (`୨୧Miyu୨୧`) and Thai/Lao digits as letters (`๓ყth໓໐ll`) | 9 | 20 | ୨ ×7, ୧ ×7 | dropped beside Latin letters; Thai/Lao font table |
| Thai letters as a font (`ღรђค๔๏ฬ ฬ๏ɭŦღ`, `๓i๓iŞຖ໐`) | 9 | 19 | | Thai-style font table |
| Box-drawing and geometric shapes (`°•□Liara■^`, `Heartless◀|3`) | 7 | 11 | | dropped |
| Arabic, Hebrew (`-_- נιмму -_-` has a Hebrew נ for j) | 4 | 5 | | Hebrew letters of the Thai-style alphabet folded; Arabic kept |
| Invisible and format characters | **1** name (`Momo⠀⠀`, two braille blanks) | 2 | | treated as spaces |
| Zero-width joiners, spaces, marks, direction marks, tags | **0** | 0 | VRChat and Discord strip them | dropped anyway |
| Variation selector 16 (emoji presentation) | 6 names | 10 | | dropped unless emoji are kept |

Two things stand out. VRChat and Discord already strip the invisible characters, so the
"zero-width" case the design worried about does not arrive in stored names at all -- the zalgo
combining marks do. And the most common non-ASCII character in VRChat names is not a font
letter but U+2024 ONE DOT LEADER: 130 occurrences across 115 names, always where a full stop would
be (`Bruh_SFX․mp4`, `Mr․ Goose`). VRChat evidently substitutes it, since a display name cannot
contain a full stop. The searchable form treats it as `.`, so `boba.tea` finds `Boba․tea`.

## 3. Leet

291 of the 7,175 values have a digit inside a word (`pr3ttyfacee`, `C0zyS0cks`, `SKEL3TOR`,
`M0NEYPOWERFAME`), and `$` and `@` for s and a turn up (`＄EvilTwin＄`, `CyD1＠N`). The
normalizer does **not** fold leet, in either form: a digit is a digit in `Dragon135_Racer`,
`danktips100` and `Kuna_2.0`, which are far more common than leet, and `1` is `i` in one name
and `l` in the next. The moderation engine's own term matcher (`NormalisedText` in Modbot.AI)
folds leet for word filtering, where a false positive is a review rather than a wrong name.

## 4. The font alphabets, decoded

The names use a small number of generator alphabets, each a fixed substitution. These are the
hand tables in the generator; every letter in each was checked against a real name here.

| Alphabet | Example (real) | Reads as |
|---|---|---|
| Small capitals `ᴀʙᴄᴅᴇғɢʜɪᴊᴋʟᴍɴᴏᴘǫʀꜱᴛᴜᴠᴡxʏᴢ` | `༻sᴜɢᴀʀʙᴜɴɴɪᴇ༺` | sugarbunnie |
| Cute lower case `αвc∂єfgнιנкℓмησρqяѕтυνωχуz` (Greek, Cyrillic, one Hebrew) | `-_- נιмму -_-` | jimmy |
| Bold capitals from other scripts `ΛBᄃDΣFGHIJKLMПӨPQЯƧƬЦVЩXYZ` | `ᄃYЯЦƧЯӨZΣ`, `PӨᄃKΣƬ_JΛMMY` | CYRUSROZE, POCKET_JAMMY |
| Hooked and stroked capitals `ƛƁƇƊƐƑƓӇƖʆƘԼMƝƠƤQƦƧƬƲƔƜXƳȤ` | `ƛƇƦƳԼƖƇ`, `ƜЄƧƬƓӇƠƧƬ` | ACRYLIC, WESTGHOST |
| Thai-looking `ค๒ς๔єŦgђเןкl๓ภ๏קợгรtยשฬאץչ` | `ღรђค๔๏ฬ ฬ๏ɭŦღ` | shadow wolf (Ŧ is read as T: see §6) |
| Rune-looking `ǟɮƈɖɛʄɢɦɨʝӄʟʍռօքզʀֆȶʊʋաӼʏʐ` (Armenian, Cyrillic, IPA) | `Ֆɨʟɛռȶʟɨɮʀǟ`, `ֆքօօӄʏ ǟʍօʀ` | Silentlibra, spooky amor |
| Curly `ąɓƈɖɛʄɠɧıʝƙƖɱŋơ℘զཞʂɬųʋῳҳყʑ` | `ɱყʂɬıƈąƖզųɛɛŋ`, `۷ųƖƈąཞơʂ` | mysticalqueen, vulcaros |
| Greek-looking `αႦƈԃҽϝɠԋιʝƙʅɱɳσρϙɾʂƚυʋɯxყȥ` | `Mαȥαƙιҽɳ`, `GɾιȥȥʅყDιρρҽɾ69`, `Cσȥყ_Qυιɳɳ` | Mazakien, GrizzlyDipper69, Cozy_Quinn |
| Currency `₳฿₵ĐɆ₣₲ĦłJ₭Ł₥₦Ø₱Q₹₴₮ɄV₩ӾɎƵ` | `₵ØØ₭łɆ₭ł₦₲`, `VɆ₦ØM_₲HØ₴₮x` | COOKIEKING, VENOM_GHOSTx |
| Chinese-stroke `卂乃匚刀乇下厶卄工丁长乚从几口卩㔿尺丂丅凵リ山乂丫乙` | `丅卄尺乇卂丅`, `卂 丂 卄 ㄒ ㄖ 几` | THREAT, ASHTON -- **not folded**, see §6 |

## 5. Real words in other scripts, which must not fold

The rule that saves them: a word whose letters are all from one script other than Latin, two or
more of them, is written in that script and is left alone. Everything below is kept as written.

`|~Принцесса Кира~|`, `отъебитесь`, `ПУСТО`, `翔宇`, `美しい蝶`, `意思意思`, `团长你就是个姬`,
`黑胖子自杀吧`, `バハちょん【Baha】` (the kana word stays, the brackets go), `Meloz メロズ - 멜로즈`,
`ケモケモケー N R S`, `Mr John 茶`, `Subaru 水`, `SkyQing青蛙`, `黒Kuro-Neko猫`, `Detective ඩා`,
`ཌDanteད`. `MOHG ﻕﻮﻣ` keeps its Arabic, written with the ordinary letters (`قوم`) rather than the
presentation forms the person pasted.

And the ones a per-word rule gets wrong on purpose: `ԁеаԁ ԁаrling` is two words; the second has a
Latin r and folds to `darling`, the first is four Cyrillic look-alikes and stays `ԁеаԁ`. A name-wide
rule would fold it -- and would also fold the Russian word in a bilingual `Ivan Иван`, which is the
worse mistake.

## 6. Known limits, with the names that show them

- **Ŧ** is T in the plain-letter table, so the Thai-style alphabet's F reads as T:
  `୨୧ Ŧɭєςђค ୨୧` comes out `Tlecha`, not `Flesha`. Two names use it; `Ŧ` as a real T-with-stroke
  (Sami, or the `Ẅøłfɓłŧê` style of the same group) is the safer reading.
- **ł** could be l or I: `Łukasz` and `₵ØØ₭łɆ₭ł₦₲` both occur. It is decided by the letters
  around it, like Ɩ and Ι -- capitals around it make it I. `Michał` is still `Michal`.
- **Chinese-stroke alphabets** (`丅卄尺乇卂丅` = THREAT) are left as written. 丁, 口, 山, 工, 下 are
  everyday Chinese characters, and a table folding them would rewrite `山田` to `WA`.
- **A single kana as a smiley** (`Asherツ`, `Kelzツ`) goes when it sits in a Latin word, but
  `Iglo ツ` and `Ru ツ` keep theirs: alone in its own word, ツ is a Japanese word as far as any
  rule can tell.
- **A decoration that is a real letter of a living script** (`ఌ` Telugu, `ৎ` Bengali, `ᆺ`
  Hangul jamo) goes only when it is the lone letter of its script in a Latin word, or the same
  letter repeated (`亞Rebel亞`). Two different such letters are kept as a word.
- **Ambiguous symbols**: `¥` and `£` fold to Y and L (the currency alphabet), so `¥ Noise ¥`
  reads `Y Noise Y`; `§` folds to S (`Çµr§êÐ` → `CurSeD`).
- **Dakuten in the searchable form**: `ビッグマネー` is searched as `ヒックマネー`, because the
  searchable form drops every mark. Both sides of a search fold the same way, so it still finds
  itself; a moderator typing the name with dakuten finds it too.

## 7. Collisions

220 groups of distinct originals share a searchable form. Almost all are the same person across
Discord's username, display name and global name differing only in case (`RedpandaMel` /
`Redpandamel`), plus a few pairs that differ only in font (`HamiHam` / `𝙃𝙖𝙢𝙞𝙃𝙖𝙢`,
`Dragon135_racer` / `𝕯𝖗𝖆𝖌𝖔𝖓135_𝕽𝖆𝖈𝖊𝖗`). A plain name is a way to read and find somebody, not to
identify them; the id is (moderation actions design §3.1).

## 8. What was not seen, and is handled anyway

No stored name carried a zero-width space or joiner, a directional mark, a soft hyphen, a tag
character or a private-use character: the platforms strip them. The normalizer drops them all
the same, because the term lists, case-file text and pasted search terms are not so clean, and
because the search term a moderator pastes from a Discord message may be.
