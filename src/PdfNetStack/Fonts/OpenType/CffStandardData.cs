namespace PdfNetStack.Fonts.OpenType;

/// <summary>
/// The predefined data of CFF fonts (Adobe Technical Note 5176): the standard strings that string
/// ids below 391 name (Appendix A), the Expert encoding (Appendix B) and the Expert and
/// ExpertSubset charsets (Appendix C). The ISOAdobe charset is the first 229 standard strings.
/// </summary>
internal static class CffStandardData
{
    /// <summary>The number of standard strings; a string id from here on indexes the font's String INDEX.</summary>
    public const int StandardStringCount = 391;

    private const string Standard =
        ".notdef space exclam quotedbl numbersign dollar percent ampersand quoteright parenleft " +
        "parenright asterisk plus comma hyphen period slash zero one two three four five six seven eight " +
        "nine colon semicolon less equal greater question at A B C D E F G H I J K L M N O P Q R S T U V " +
        "W X Y Z bracketleft backslash bracketright asciicircum underscore quoteleft a b c d e f g h i j " +
        "k l m n o p q r s t u v w x y z braceleft bar braceright asciitilde exclamdown cent sterling " +
        "fraction yen florin section currency quotesingle quotedblleft guillemotleft guilsinglleft " +
        "guilsinglright fi fl endash dagger daggerdbl periodcentered paragraph bullet quotesinglbase " +
        "quotedblbase quotedblright guillemotright ellipsis perthousand questiondown grave acute " +
        "circumflex tilde macron breve dotaccent dieresis ring cedilla hungarumlaut ogonek caron emdash " +
        "AE ordfeminine Lslash Oslash OE ordmasculine ae dotlessi lslash oslash oe germandbls " +
        "onesuperior logicalnot mu trademark Eth onehalf plusminus Thorn onequarter divide brokenbar " +
        "degree thorn threequarters twosuperior registered minus eth multiply threesuperior copyright " +
        "Aacute Acircumflex Adieresis Agrave Aring Atilde Ccedilla Eacute Ecircumflex Edieresis Egrave " +
        "Iacute Icircumflex Idieresis Igrave Ntilde Oacute Ocircumflex Odieresis Ograve Otilde Scaron " +
        "Uacute Ucircumflex Udieresis Ugrave Yacute Ydieresis Zcaron aacute acircumflex adieresis agrave " +
        "aring atilde ccedilla eacute ecircumflex edieresis egrave iacute icircumflex idieresis igrave " +
        "ntilde oacute ocircumflex odieresis ograve otilde scaron uacute ucircumflex udieresis ugrave " +
        "yacute ydieresis zcaron exclamsmall Hungarumlautsmall dollaroldstyle dollarsuperior " +
        "ampersandsmall Acutesmall parenleftsuperior parenrightsuperior twodotenleader onedotenleader " +
        "zerooldstyle oneoldstyle twooldstyle threeoldstyle fouroldstyle fiveoldstyle sixoldstyle " +
        "sevenoldstyle eightoldstyle nineoldstyle commasuperior threequartersemdash periodsuperior " +
        "questionsmall asuperior bsuperior centsuperior dsuperior esuperior isuperior lsuperior " +
        "msuperior nsuperior osuperior rsuperior ssuperior tsuperior ff ffi ffl parenleftinferior " +
        "parenrightinferior Circumflexsmall hyphensuperior Gravesmall Asmall Bsmall Csmall Dsmall Esmall " +
        "Fsmall Gsmall Hsmall Ismall Jsmall Ksmall Lsmall Msmall Nsmall Osmall Psmall Qsmall Rsmall " +
        "Ssmall Tsmall Usmall Vsmall Wsmall Xsmall Ysmall Zsmall colonmonetary onefitted rupiah " +
        "Tildesmall exclamdownsmall centoldstyle Lslashsmall Scaronsmall Zcaronsmall Dieresissmall " +
        "Brevesmall Caronsmall Dotaccentsmall Macronsmall figuredash hypheninferior Ogoneksmall " +
        "Ringsmall Cedillasmall questiondownsmall oneeighth threeeighths fiveeighths seveneighths " +
        "onethird twothirds zerosuperior foursuperior fivesuperior sixsuperior sevensuperior " +
        "eightsuperior ninesuperior zeroinferior oneinferior twoinferior threeinferior fourinferior " +
        "fiveinferior sixinferior seveninferior eightinferior nineinferior centinferior dollarinferior " +
        "periodinferior commainferior Agravesmall Aacutesmall Acircumflexsmall Atildesmall " +
        "Adieresissmall Aringsmall AEsmall Ccedillasmall Egravesmall Eacutesmall Ecircumflexsmall " +
        "Edieresissmall Igravesmall Iacutesmall Icircumflexsmall Idieresissmall Ethsmall Ntildesmall " +
        "Ogravesmall Oacutesmall Ocircumflexsmall Otildesmall Odieresissmall OEsmall Oslashsmall " +
        "Ugravesmall Uacutesmall Ucircumflexsmall Udieresissmall Yacutesmall Thornsmall Ydieresissmall " +
        "001.000 001.001 001.002 001.003 Black Bold Book Light Medium Regular Roman Semibold";

    // Codes 32 to 255, a period marking an unused code.
    private const string ExpertEncodingNames =
        "space exclamsmall Hungarumlautsmall . dollaroldstyle dollarsuperior ampersandsmall Acutesmall " +
        "parenleftsuperior parenrightsuperior twodotenleader onedotenleader comma hyphen period fraction " +
        "zerooldstyle oneoldstyle twooldstyle threeoldstyle fouroldstyle fiveoldstyle sixoldstyle " +
        "sevenoldstyle eightoldstyle nineoldstyle colon semicolon commasuperior threequartersemdash " +
        "periodsuperior questionsmall . asuperior bsuperior centsuperior dsuperior esuperior . . . " +
        "isuperior . . lsuperior msuperior nsuperior osuperior . . rsuperior ssuperior tsuperior . ff fi " +
        "fl ffi ffl parenleftinferior . parenrightinferior Circumflexsmall hyphensuperior Gravesmall " +
        "Asmall Bsmall Csmall Dsmall Esmall Fsmall Gsmall Hsmall Ismall Jsmall Ksmall Lsmall Msmall " +
        "Nsmall Osmall Psmall Qsmall Rsmall Ssmall Tsmall Usmall Vsmall Wsmall Xsmall Ysmall Zsmall " +
        "colonmonetary onefitted rupiah Tildesmall . . . . . . . . . . . . . . . . . . . . . . . . . . . " +
        ". . . . . . . exclamdownsmall centoldstyle Lslashsmall . . Scaronsmall Zcaronsmall " +
        "Dieresissmall Brevesmall Caronsmall . Dotaccentsmall . . Macronsmall . . figuredash " +
        "hypheninferior . . Ogoneksmall Ringsmall Cedillasmall . . . onequarter onehalf threequarters " +
        "questiondownsmall oneeighth threeeighths fiveeighths seveneighths onethird twothirds . . " +
        "zerosuperior onesuperior twosuperior threesuperior foursuperior fivesuperior sixsuperior " +
        "sevensuperior eightsuperior ninesuperior zeroinferior oneinferior twoinferior threeinferior " +
        "fourinferior fiveinferior sixinferior seveninferior eightinferior nineinferior centinferior " +
        "dollarinferior periodinferior commainferior Agravesmall Aacutesmall Acircumflexsmall " +
        "Atildesmall Adieresissmall Aringsmall AEsmall Ccedillasmall Egravesmall Eacutesmall " +
        "Ecircumflexsmall Edieresissmall Igravesmall Iacutesmall Icircumflexsmall Idieresissmall " +
        "Ethsmall Ntildesmall Ogravesmall Oacutesmall Ocircumflexsmall Otildesmall Odieresissmall " +
        "OEsmall Oslashsmall Ugravesmall Uacutesmall Ucircumflexsmall Udieresissmall Yacutesmall " +
        "Thornsmall Ydieresissmall";

    // Glyph 1 onwards; glyph 0 is .notdef.
    private const string ExpertCharsetNames =
        "space exclamsmall Hungarumlautsmall dollaroldstyle dollarsuperior ampersandsmall Acutesmall " +
        "parenleftsuperior parenrightsuperior twodotenleader onedotenleader comma hyphen period fraction " +
        "zerooldstyle oneoldstyle twooldstyle threeoldstyle fouroldstyle fiveoldstyle sixoldstyle " +
        "sevenoldstyle eightoldstyle nineoldstyle colon semicolon commasuperior threequartersemdash " +
        "periodsuperior questionsmall asuperior bsuperior centsuperior dsuperior esuperior isuperior " +
        "lsuperior msuperior nsuperior osuperior rsuperior ssuperior tsuperior ff fi fl ffi ffl " +
        "parenleftinferior parenrightinferior Circumflexsmall hyphensuperior Gravesmall Asmall Bsmall " +
        "Csmall Dsmall Esmall Fsmall Gsmall Hsmall Ismall Jsmall Ksmall Lsmall Msmall Nsmall Osmall " +
        "Psmall Qsmall Rsmall Ssmall Tsmall Usmall Vsmall Wsmall Xsmall Ysmall Zsmall colonmonetary " +
        "onefitted rupiah Tildesmall exclamdownsmall centoldstyle Lslashsmall Scaronsmall Zcaronsmall " +
        "Dieresissmall Brevesmall Caronsmall Dotaccentsmall Macronsmall figuredash hypheninferior " +
        "Ogoneksmall Ringsmall Cedillasmall onequarter onehalf threequarters questiondownsmall oneeighth " +
        "threeeighths fiveeighths seveneighths onethird twothirds zerosuperior onesuperior twosuperior " +
        "threesuperior foursuperior fivesuperior sixsuperior sevensuperior eightsuperior ninesuperior " +
        "zeroinferior oneinferior twoinferior threeinferior fourinferior fiveinferior sixinferior " +
        "seveninferior eightinferior nineinferior centinferior dollarinferior periodinferior " +
        "commainferior Agravesmall Aacutesmall Acircumflexsmall Atildesmall Adieresissmall Aringsmall " +
        "AEsmall Ccedillasmall Egravesmall Eacutesmall Ecircumflexsmall Edieresissmall Igravesmall " +
        "Iacutesmall Icircumflexsmall Idieresissmall Ethsmall Ntildesmall Ogravesmall Oacutesmall " +
        "Ocircumflexsmall Otildesmall Odieresissmall OEsmall Oslashsmall Ugravesmall Uacutesmall " +
        "Ucircumflexsmall Udieresissmall Yacutesmall Thornsmall Ydieresissmall";

    private const string ExpertSubsetCharsetNames =
        "space dollaroldstyle dollarsuperior parenleftsuperior parenrightsuperior twodotenleader " +
        "onedotenleader comma hyphen period fraction zerooldstyle oneoldstyle twooldstyle threeoldstyle " +
        "fouroldstyle fiveoldstyle sixoldstyle sevenoldstyle eightoldstyle nineoldstyle colon semicolon " +
        "commasuperior threequartersemdash periodsuperior asuperior bsuperior centsuperior dsuperior " +
        "esuperior isuperior lsuperior msuperior nsuperior osuperior rsuperior ssuperior tsuperior ff fi " +
        "fl ffi ffl parenleftinferior parenrightinferior hyphensuperior colonmonetary onefitted rupiah " +
        "centoldstyle figuredash hypheninferior onequarter onehalf threequarters oneeighth threeeighths " +
        "fiveeighths seveneighths onethird twothirds zerosuperior onesuperior twosuperior threesuperior " +
        "foursuperior fivesuperior sixsuperior sevensuperior eightsuperior ninesuperior zeroinferior " +
        "oneinferior twoinferior threeinferior fourinferior fiveinferior sixinferior seveninferior " +
        "eightinferior nineinferior centinferior dollarinferior periodinferior commainferior";

    private static string[]? s_standard;
    private static string?[]? s_expertEncoding;
    private static string[]? s_expertCharset;
    private static string[]? s_expertSubsetCharset;

    /// <summary>The standard strings, by string id.</summary>
    public static string[] StandardStrings => LazyInitializer.EnsureInitialized(ref s_standard, static () => Standard.Split(' '))!;

    /// <summary>The Expert encoding, code to glyph name.</summary>
    public static string?[] ExpertEncoding => LazyInitializer.EnsureInitialized(ref s_expertEncoding, static () =>
    {
        var table = new string?[256];
        string[] names = ExpertEncodingNames.Split(' ');
        for (int i = 0; i < names.Length; i++) table[32 + i] = names[i] == "." ? null : names[i];
        return table;
    })!;

    /// <summary>The names of glyphs 1 onwards in the Expert charset.</summary>
    public static string[] ExpertCharset => LazyInitializer.EnsureInitialized(ref s_expertCharset, static () => ExpertCharsetNames.Split(' '))!;

    /// <summary>The names of glyphs 1 onwards in the ExpertSubset charset.</summary>
    public static string[] ExpertSubsetCharset => LazyInitializer.EnsureInitialized(ref s_expertSubsetCharset, static () => ExpertSubsetCharsetNames.Split(' '))!;
}
