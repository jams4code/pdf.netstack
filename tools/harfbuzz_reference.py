"""Records HarfBuzz shaping of sample texts in local fonts, for comparison with PdfNetStack's shaper.

    pip install uharfbuzz
    python tools/harfbuzz_reference.py out.jsonl font1.ttf [font2.ttc#1 font3.ttf@wght=700 ...]
    set PDFNETSTACK_HB_REFERENCE=out.jsonl and run the tests (HarfBuzzComparisonTests)

Dev only: fonts and HarfBuzz output stay on the machine. A font in a collection is named with its
index after '#', an instance of a variable font with its axis values after '@'
(font.ttf@wght=700,wdth=75). Each line holds the font, the text, direction, script and HarfBuzz's glyphs in
logical order: glyph id, cluster (code point index), x advance, x offset, y offset, in font units.
A sample is recorded for a font when the font covers its text: each character, its canonical
decomposition, or (for spaces) the space.
"""

import json
import sys
import unicodedata

import uharfbuzz as hb

SAMPLES = [
    ("ltr", "latn", "office efficient fjord flight"),
    ("ltr", "latn", "AVAWAY To Ta Te Vo Yo LT. P, F."),
    ("ltr", "latn", "Zürich naïve café — “quoted” résumé"),
    ("ltr", "latn", "Ελληνικά Кириллица ŁÓDŹ Ǆ"),
    # Normalization: decomposed text composes, precomposed text the font lacks decomposes, marks
    # are put in canonical order, and spaces and the non-breaking hyphen fall back.
    ("ltr", "latn", "café naïve Å Å Ω ệ ệ ớ"),
    ("ltr", "latn", "ệ ớ Ǖ ḉ ǡ q̣̇ x́"),
    ("ltr", "latn", "a b c d e f g h i　j k‑l"),
    ("rtl", "hebr", "שָׁלוֹם בְּרֵאשִׁית"),
    ("rtl", "hebr", "אַ אָ בּ וֹ יִ שּׁ שּׂ פֿ"),
    ("rtl", "arab", "مرحبا بالعالم"),
    ("rtl", "arab", "السلام عليكم"),
    ("rtl", "arab", "لا إله إلا الله"),
    ("rtl", "arab", "بِسْمِ اللَّهِ الرَّحْمَنِ الرَّحِيمِ"),
    ("rtl", "arab", "پنجره گچ ژاله"),
    ("rtl", "arab", "ك‌ل‍م"),
    ("rtl", "arab", "١٢٣ (نص) [قوس]"),
    ("rtl", "arab", "بَّ بِّ أَ اٰ آ إِ"),
    # Indic: reph, half forms, conjuncts, below and post-base forms, pre-base matras, split
    # matras, nuktas, joiners and broken clusters.
    ("ltr", "deva", "हिन्दी क्षत्रिय कार्य प्रेम द्विज श्री"),
    ("ltr", "deva", "ज़्यादा अर्द्ध स्त्री क्‍ष क्‌ष र्‍य हृदय दृ द्ध ङ्क ह्म ज्ञ"),
    ("ltr", "deva", "ि र् कि ि ॐ १२३ क्ष्म्य रु रू र्कि र्के क्रि त्र्य"),
    ("ltr", "deva", "नमस्ते दुनिया, फ़िल्म क़िला ड़ ढ़ कर्ता धर्म पूर्व"),
    ("ltr", "beng", "বাংলা কোন কৌ র্ক ক্র ক্য ক্ষ সন্ত্রাস"),
    ("ltr", "beng", "রু ড় য় ৎ জ্ঞান স্বাধীনতা কি র্কি ক্ত্র"),
    ("ltr", "guru", "ਪੰਜਾਬੀ ਕ੍ਰ ਸ੍ਵ ਖ਼ ਗੁਰਮੁਖੀ ਧਿਆਨ ਪ੍ਰੀਤ ਕਿ ੴ"),
    ("ltr", "gujr", "ગુજરાતી ક્ષ ર્ક પ્ર દ્વ શ્રી કિ ત્ર ર્કિ"),
    ("ltr", "orya", "ଓଡ଼ିଆ କ୍ଷ ର୍କ କୋ କୌ ପ୍ର ଶ୍ରୀ କ୍ୟ କି"),
    ("ltr", "taml", "தமிழ் கொ கோ கௌ க்ஷ ஸ்ரீ கி கு டி நூ ஔ"),
    ("ltr", "telu", "తెలుగు క్ష ర్క ర్‍క క్ర కై స్త్రీ కి కృ న్యూ"),
    ("ltr", "knda", "ಕನ್ನಡ ಕ್ಷ ರ್ಕ ಕೊ ಕೋ ಕೈ ಕ್ರ ಸ್ತ್ರೀ ರ್‍ಕ ಕಿ"),
    ("ltr", "mlym", "മലയാളം ക്ഷ ക്ര കൊ കൌ ൎക ന്റ ക്ക ണ്‍ ൺ"),
    ("ltr", "mlym", "സ്ത്രീ ര്‍ ക്യ ക്വ കി പ്ര ക്രൊ"),
    ("ltr", "deva", "्क ाक किि क्‍ि क॒॑ क़्ष हँस कँ ऍ ॲ क्‍ ॐ भूर्भुवः स्वः तत्सवितुर्वरेण्यं"),
    # Article 1 of the Universal Declaration of Human Rights.
    ("ltr", "deva", "सभी मनुष्यों को गौरव और अधिकारों के मामले में जन्मजात स्वतन्त्रता और समानता प्राप्त है। उन्हें बुद्धि और अन्तरात्मा की देन प्राप्त है और परस्पर उन्हें भाईचारे के भाव से बर्ताव करना चाहिये।"),
    ("ltr", "beng", "সমস্ত মানুষ স্বাধীনভাবে সমান মর্যাদা এবং অধিকার নিয়ে জন্মগ্রহণ করে। তাঁদের বিবেক এবং বুদ্ধি আছে; সুতরাং সকলেরই একে অপরের প্রতি ভ্রাতৃত্বসুলভ মনোভাব নিয়ে আচরণ করা উচিত।"),
    ("ltr", "guru", "ਸਾਰਾ ਮਨੁੱਖੀ ਪਰਿਵਾਰ ਆਪਣੀ ਮਹਿਮਾ, ਸ਼ਾਨ ਅਤੇ ਹੱਕਾਂ ਦੇ ਪੱਖੋਂ ਜਨਮ ਤੋਂ ਹੀ ਆਜ਼ਾਦ ਹੈ ਅਤੇ ਸੁਤੇ ਸਿੱਧ ਸਾਰੇ ਲੋਕ ਬਰਾਬਰ ਹਨ।"),
    ("ltr", "gujr", "પ્રતિષ્ઠા અને અધિકારોની દૃષ્ટિએ સર્વ માનવો જન્મથી સ્વતંત્ર અને સમાન હોય છે. તેમનામાં વિચારશક્તિ અને અંતઃકરણ હોય છે અને તેમણે પરસ્પર બંધુત્વની ભાવનાથી વર્તવું જોઇએ."),
    ("ltr", "orya", "ସବୁ ମଣିଷ ଜନ୍ମକାଳରୁ ସ୍ୱାଧୀନ ଏବଂ ମର୍ଯ୍ୟାଦା ଓ ଅଧିକାରରେ ସମାନ। ସେମାନଙ୍କଠାରେ ବୁଦ୍ଧି ଓ ବିବେକ ନିହିତ ଅଛି।"),
    ("ltr", "taml", "மனிதப் பிறவியினர் சகலரும் சுதந்திரமாகவே பிறக்கின்றனர்; அவர்கள் மதிப்பிலும், உரிமைகளிலும் சமமானவர்கள், அவர்கள் நியாயத்தையும் மனச்சாட்சியையும் இயற்பண்பாகப் பெற்றவர்கள்."),
    ("ltr", "telu", "ప్రతిపత్తిస్వత్వముల విషయమున మానవులెల్లరును జన్మతః స్వతంత్రులును సమానులును నగుదురు. వారు వివేచన-అంతఃకరణ సంపన్నులగుటచే పరస్పరము భ్రాతృభావముతో వర్తింపవలయును."),
    ("ltr", "knda", "ಎಲ್ಲಾ ಮಾನವರೂ ಸ್ವತಂತ್ರರಾಗಿಯೇ ಜನಿಸಿದ್ದಾರೆ. ಹಾಗೂ ಘನತೆ ಮತ್ತು ಹಕ್ಕುಗಳಲ್ಲಿ ಸಮಾನರಾಗಿದ್ದಾರೆ. ವಿವೇಕ ಮತ್ತು ಅಂತಃಕರಣಗಳನ್ನು ಪಡೆದವರಾದ್ದರಿಂದ ಅವರು ಪರಸ್ಪರ ಸಹೋದರ ಭಾವದಿಂದ ವರ್ತಿಸಬೇಕು."),
    ("ltr", "mlym", "മനുഷ്യരെല്ലാവരും തുല്യാവകാശങ്ങളോടും അന്തസ്സോടും സ്വാതന്ത്ര്യത്തോടുംകൂടി ജനിച്ചിട്ടുള്ളവരാണ്‌. അന്യോന്യം ഭ്രാതൃഭാവത്തോടെ പെരുമാറുവാനാണ്‌ മനുഷ്യന് വിവേകബുദ്ധിയും മനസാക്ഷിയും സിദ്ധമായിരിക്കുന്നത്‌."),
    ("rtl", "hebr", "כָּל בְּנֵי הָאָדָם נוֹלְדוּ בְּנֵי חוֹרִין וְשָׁוִים בְּעֶרְכָּם וּבִזְכֻיּוֹתֵיהֶם"),
    # Thai and Lao: sara am after tone marks, marks over tall consonants and under descenders.
    ("ltr", "thai", "ภาษาไทย น้ำ กำ ทำ ป่ำ ปั่น ปิ่น ฟ้า ฝั่ง ญู ฐุ ฎุ ปุ่ ก็ ฬ์ ป้ ป๋ ฝื่"),
    ("ltr", "lao ", "ພາສາລາວ ນ້ຳ ກຳ ເຂົ້າ ສະບາຍດີ ກ່ຳ"),
    # Khmer: coeng ro before the base, split vowels, stacked subscripts, signs after vowels.
    ("ltr", "khmr", "ភាសាខ្មែរ ស្រី ក្រុម ព្រះ កៀ កើ កោ កៅ ង្ក្រ ខ្ញុំ សួស្តី ក៏ ប៉ុ"),
    # Myanmar: kinzi, medials, asat, the vowel e before the base, tones.
    ("ltr", "mymr", "မြန်မာ ကြည့် သင်္ချိုင်း ကျွန်ုပ် မင်္ဂလာပါ ရှင်း ကြွ ဂေါ် ကေ့ ပြော"),
    # Hangul: syllables, conjoining jamo that compose, a syllable and trailing jamo, Old Hangul,
    # tone marks after a syllable and alone.
    ("ltr", "hang", "한국어 훈민정음 각 각 ᄒᆞᆫ 한〮 〯 가 ꥠᅡ"),
    # The Universal Shaping Engine.
    ("ltr", "sinh", "සිංහල ශ්‍රී ක්‍ෂ කෙ කො කෞ ප්‍ර ර්‍ක ක්‍ය ක්ව කේ කෝ ලංකා"),
    ("ltr", "tibt", "བོད་ཡིག བསྒྲུབས ཧཱུྃ ཨོཾ་མ་ཎི་པདྨེ་ཧཱུྃ ཀྲ སྐྱེ ཀཱི ཀྵ"),
    ("ltr", "bali", "ᬩᬲᬩᬮᬶ ᬓ᭄ᬭ ᬓᬾ ᬓᭀ ᬓᭁ ᬭ᭄ᬓ ᬓᬃ ᬓ᭄ᬱᬾ ᬓᬸ ᬅᬓ᭄ᬱᬭ"),
    ("ltr", "java", "ꦧꦱꦗꦮ ꦏꦿ ꦏꦾ ꦏꦺ ꦏꦺꦴ ꦫ꧀ꦏ ꦏꦂ ꦲꦤꦕꦫꦏ ꦏ꧀ꦱꦼ ꦏꦶꦁ"),
    ("ltr", "mong", "ᠮᠣᠩᠭᠣᠯ ᠪᠢᠴᠢᠭ ᠠ᠋ᠨ ᠭ᠌ᠠ ᠨᠠ᠋ᠮ"),
    ("rtl", "nko ", "ߒߞߏ ߞߊ߲ ߓߊ߯ ߞߊ߫"),
    ("rtl", "adlm", "𞤀𞤣𞤤𞤢𞤥 𞤆𞤵𞤤𞤢𞤪"),
    ("ltr", "tfng", "ⵜⴰⵎⴰⵣⵉⵖⵜ ⵜ⵿ⵜ"),
    ("ltr", "bugi", "ᨅᨔ ᨕᨘᨁᨗ ᨀᨙ"),
    ("ltr", "mtei", "ꯃꯤꯇꯩ ꯃꯌꯦꯛ ꯀ꯭ꯔ"),
    ("ltr", "sund", "ᮞᮥᮔ᮪ᮓ ᮊᮦ ᮊᮢ ᮊᮁ"),
    ("ltr", "lana", "ᨲᩫ᩠ᩅᨾᩮᩬᩥᩬᨦ"),
    ("ltr", "lepc", "ᰛᰩᰵ ᰀᰦ"),
    ("ltr", "tglg", "ᜆᜄᜎᜓᜄ᜔"),
    ("ltr", "limb", "ᤕᤠᤰᤌᤢ"),
    # Broken clusters, joiners and long stacks.
    ("ltr", "thai", "ำ ่ำ กํ่ ปิํ ปั้ำ ญฺ่"),
    ("ltr", "khmr", "ា ្ក ក្ក្រេះ ក‌៉ ង្រ្កុំ ឥ្រ កំា"),
    ("ltr", "mymr", "ေ ် င်္ကြော့ ကေ︀ ရှော် ကိုင်း ၀ိ ‌ိ"),
    ("ltr", "sinh", "් ෙ ක්‍රෙ ර්‍කෙ ක්‍යො අැ ක‌්"),
    ("ltr", "tibt", "ཱ ཀཱུཾྂ སྒྲོཾ ཀཱྀ ༄༅ ཀུ༹ ༀ"),
    ("ltr", "bali", "ᬾ ᭄ᬓ ᬓ᭄ᬭᬾᬃ ᬭ᭄ᬓ᭄ᬭᬾ ᬓᭀᬁ ᬓ᬴᭄ ᬆ"),
    ("ltr", "java", "ꦺ ꦏ꧀ꦫꦺꦴ ꦫ꧀ꦏꦾꦺ ꦏꦿꦼꦂ ꦏ꦳꧀ ꦄꦴ"),
    ("ltr", "mong", "ᠠ᠋ᠨ ᠭ᠌ᠠ ᠢ ᠨᠡ᠍ᠮ ᠲᠦ᠋ ᠠ‍ ‍ᠠ"),
    ("rtl", "nko ", "ߊ߲߫ ߫ ߓߊ߽߳"),
    ("ltr", "hang", "각〮 ᄀ ᅡ ᆨ 각ᆨ 가ᇃ ᅙᅡᇹ 〮〯"),
    ("ltr", "lao ", "ກົ່ຳ ຳ ກິ້ຳ"),
]

# OpenType script tags whose ISO 15924 code (what HarfBuzz takes) differs.
ISO_SCRIPTS = {"lao ": "Laoo", "nko ": "Nkoo", "vai ": "Vaii", "yi  ": "Yiii"}

# Brahmic scripts of the Universal Shaping Engine, sampled from their character names: vowel
# signs, conjuncts, reph and below forms, signs and broken clusters.
BRAHMIC = {
    "brah": "BRAHMI", "cakm": "CHAKMA", "shrd": "SHARADA", "takr": "TAKRI", "gran": "GRANTHA", "khoj": "KHOJKI",
    "sind": "KHUDAWADI", "mahj": "MAHAJANI", "modi": "MODI", "sidd": "SIDDHAM", "tirh": "TIRHUTA", "ahom": "AHOM",
    "mult": "MULTANI", "bhks": "BHAIKSUKI", "marc": "MARCHEN", "newa": "NEWA", "gonm": "MASARAM GONDI", "soyo": "SOYOMBO",
    "zanb": "ZANABAZAR SQUARE", "dogr": "DOGRA", "gong": "GUNJALA GONDI", "maka": "MAKASAR", "nand": "NANDINAGARI",
    "kthi": "KAITHI", "sylo": "SYLOTI NAGRI", "saur": "SAURASHTRA", "cham": "CHAM", "batk": "BATAK", "kali": "KAYAH LI",
    "rjng": "REJANG", "hano": "HANUNOO", "buhd": "BUHID", "tagb": "TAGBANWA", "khar": "KHAROSHTHI", "tale": "TAI LE",
    "lepc": "LEPCHA", "tavt": "TAI VIET", "diak": "DIVES AKURU", "kawi": "KAWI", "tutg": "TULU-TIGALARI",
}


def brahmic_samples(tag, script):
    def ch(*names):
        for n in names:
            try:
                return unicodedata.lookup(f"{script} {n}")
            except KeyError:
                pass
        return ""

    ka, ga, ta, na, ma, ya, ra, va, sa = (ch(f"LETTER {n}") for n in ("KA", "GA", "TA", "NA", "MA", "YA", "RA", "VA", "SA"))
    a = ch("LETTER A")
    h = ch("SIGN VIRAMA", "VIRAMA", "SIGN HALANTA", "SIGN PATEN", "SIGN KILLER", "SIGN PAMUDPOD", "PANGOLAT")
    vowels = [ch(f"VOWEL SIGN {v}") for v in ("AA", "I", "II", "U", "UU", "E", "AI", "O", "AU", "VOCALIC R")]
    signs = [ch(f"SIGN {s}") for s in ("ANUSVARA", "CANDRABINDU", "VISARGA", "NUKTA")]
    if not ka:
        return []
    words = [ka + v for v in vowels if v] + [ka + s for s in signs if s] + [a + v for v in vowels[:2] if a and v]
    if h:
        words += [ka + h + ssa for ssa in (sa, ta) if ssa] + [ra + h + ka, ka + h + ra, ka + h + ya, ka + h + va, ka + h,
                                                            sa + h + ta + h + ra + vowels[1], ra + h + ka + vowels[1],
                                                            na + h + ga + vowels[5], ma + h + ma + vowels[7], h + ka]
    words += [v for v in vowels[:2] if v]
    words = [w for w in words if w]
    return [("rtl" if tag == "khar" else "ltr", tag, " ".join(words[i:i + 8])) for i in range(0, len(words), 8)]


for _tag, _script in BRAHMIC.items():
    SAMPLES.extend(brahmic_samples(_tag, _script))


def covered(text, cmap):
    for c in text:
        cp = ord(c)
        if cp in cmap or cp in (0x200C, 0x200D):
            continue
        if unicodedata.category(c) == "Zs" and 0x20 in cmap:
            continue
        if cp == 0x2011 and 0x2010 in cmap:
            continue
        decomposed = unicodedata.normalize("NFD", c)
        if decomposed != c and all(ord(d) in cmap for d in decomposed):
            continue
        return False
    return True


def shape(face, text, direction, script, variations):
    font = hb.Font(face)
    if variations:
        font.set_variations(variations)
    buf = hb.Buffer()
    buf.add_codepoints([ord(c) for c in text])
    buf.direction = direction
    buf.script = ISO_SCRIPTS.get(script, script.strip())
    hb.shape(font, buf, {})
    infos, positions = buf.glyph_infos, buf.glyph_positions
    glyphs = [[i.codepoint, i.cluster, p.x_advance, p.x_offset, p.y_offset] for i, p in zip(infos, positions)]
    if direction == "rtl":
        glyphs.reverse()
    return glyphs


def main(out, fonts):
    with open(out, "w", encoding="utf-8") as f:
        for name in fonts:
            located, _, axes = name.partition("@")
            variations = {a.split("=")[0]: float(a.split("=")[1]) for a in axes.split(",")} if axes else {}
            path, _, index = located.partition("#")
            face = hb.Face(hb.Blob.from_file_path(path), int(index or 0))
            cmap = face.unicodes
            for direction, script, text in SAMPLES:
                if not covered(text, cmap):
                    continue
                record = {"font": name, "text": text, "direction": direction, "script": script,
                          "glyphs": shape(face, text, direction, script, variations)}
                f.write(json.dumps(record, ensure_ascii=False) + "\n")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2:])
