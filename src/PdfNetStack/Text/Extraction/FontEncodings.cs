namespace PdfNetStack.Text.Extraction;

/// <summary>
/// The predefined simple font encodings (ISO 32000-1, Annex D): code to glyph name. Each table
/// lists the names of codes 32 to 255 in order, a period marking an unused code. WinAnsiEncoding
/// maps its unused codes above 40 octal to bullet, as the annex notes.
/// </summary>
internal static class FontEncodings
{
    private const string Standard =
        "space exclam quotedbl numbersign dollar percent ampersand quoteright parenleft parenright asterisk " +
        "plus comma hyphen period slash zero one two three four five six seven eight nine colon semicolon " +
        "less equal greater question at A B C D E F G H I J K L M N O P Q R S T U V W X Y Z bracketleft " +
        "backslash bracketright asciicircum underscore quoteleft a b c d e f g h i j k l m n o p q r s t u v " +
        "w x y z braceleft bar braceright asciitilde . . . . . . . . . . . . . . . . . . . . . . . . . . . . " +
        ". . . . . . exclamdown cent sterling fraction yen florin section currency quotesingle quotedblleft " +
        "guillemotleft guilsinglleft guilsinglright fi fl . endash dagger daggerdbl periodcentered . " +
        "paragraph bullet quotesinglbase quotedblbase quotedblright guillemotright ellipsis perthousand . " +
        "questiondown . grave acute circumflex tilde macron breve dotaccent dieresis . ring cedilla . " +
        "hungarumlaut ogonek caron emdash . . . . . . . . . . . . . . . . AE . ordfeminine . . . . Lslash " +
        "Oslash OE ordmasculine . . . . . ae . . . dotlessi . . lslash oslash oe germandbls";

    private const string WinAnsi =
        "space exclam quotedbl numbersign dollar percent ampersand quotesingle parenleft parenright asterisk " +
        "plus comma hyphen period slash zero one two three four five six seven eight nine colon semicolon " +
        "less equal greater question at A B C D E F G H I J K L M N O P Q R S T U V W X Y Z bracketleft " +
        "backslash bracketright asciicircum underscore grave a b c d e f g h i j k l m n o p q r s t u v w x " +
        "y z braceleft bar braceright asciitilde bullet Euro bullet quotesinglbase florin quotedblbase " +
        "ellipsis dagger daggerdbl circumflex perthousand Scaron guilsinglleft OE bullet Zcaron bullet bullet " +
        "quoteleft quoteright quotedblleft quotedblright bullet endash emdash tilde trademark scaron " +
        "guilsinglright oe bullet zcaron Ydieresis space exclamdown cent sterling currency yen brokenbar " +
        "section dieresis copyright ordfeminine guillemotleft logicalnot hyphen registered macron degree " +
        "plusminus twosuperior threesuperior acute mu paragraph periodcentered cedilla onesuperior " +
        "ordmasculine guillemotright onequarter onehalf threequarters questiondown Agrave Aacute Acircumflex " +
        "Atilde Adieresis Aring AE Ccedilla Egrave Eacute Ecircumflex Edieresis Igrave Iacute Icircumflex " +
        "Idieresis Eth Ntilde Ograve Oacute Ocircumflex Otilde Odieresis multiply Oslash Ugrave Uacute " +
        "Ucircumflex Udieresis Yacute Thorn germandbls agrave aacute acircumflex atilde adieresis aring ae " +
        "ccedilla egrave eacute ecircumflex edieresis igrave iacute icircumflex idieresis eth ntilde ograve " +
        "oacute ocircumflex otilde odieresis divide oslash ugrave uacute ucircumflex udieresis yacute thorn " +
        "ydieresis";

    private const string MacRoman =
        "space exclam quotedbl numbersign dollar percent ampersand quotesingle parenleft parenright asterisk " +
        "plus comma hyphen period slash zero one two three four five six seven eight nine colon semicolon " +
        "less equal greater question at A B C D E F G H I J K L M N O P Q R S T U V W X Y Z bracketleft " +
        "backslash bracketright asciicircum underscore grave a b c d e f g h i j k l m n o p q r s t u v w x " +
        "y z braceleft bar braceright asciitilde . Adieresis Aring Ccedilla Eacute Ntilde Odieresis Udieresis " +
        "aacute agrave acircumflex adieresis atilde aring ccedilla eacute egrave ecircumflex edieresis iacute " +
        "igrave icircumflex idieresis ntilde oacute ograve ocircumflex odieresis otilde uacute ugrave " +
        "ucircumflex udieresis dagger degree cent sterling section bullet paragraph germandbls registered " +
        "copyright trademark acute dieresis . AE Oslash . plusminus . . yen mu . . . . . ordfeminine " +
        "ordmasculine . ae oslash questiondown exclamdown logicalnot . florin . . guillemotleft " +
        "guillemotright ellipsis space Agrave Atilde Otilde OE oe endash emdash quotedblleft quotedblright " +
        "quoteleft quoteright divide . ydieresis Ydieresis fraction currency guilsinglleft guilsinglright fi " +
        "fl daggerdbl periodcentered quotesinglbase quotedblbase perthousand Acircumflex Ecircumflex Aacute " +
        "Edieresis Egrave Iacute Icircumflex Idieresis Igrave Oacute Ocircumflex . Ograve Uacute Ucircumflex " +
        "Ugrave dotlessi circumflex tilde macron breve dotaccent ring cedilla hungarumlaut ogonek caron";

    private const string MacExpert =
        "space exclamsmall Hungarumlautsmall centoldstyle dollaroldstyle dollarsuperior ampersandsmall " +
        "Acutesmall parenleftsuperior parenrightsuperior twodotenleader onedotenleader comma hyphen period " +
        "fraction zerooldstyle oneoldstyle twooldstyle threeoldstyle fouroldstyle fiveoldstyle sixoldstyle " +
        "sevenoldstyle eightoldstyle nineoldstyle colon semicolon . threequartersemdash . questionsmall . . . " +
        ". Ethsmall . . onequarter onehalf threequarters oneeighth threeeighths fiveeighths seveneighths " +
        "onethird twothirds . . . . . . ff fi fl ffi ffl parenleftinferior . parenrightinferior " +
        "Circumflexsmall hypheninferior Gravesmall Asmall Bsmall Csmall Dsmall Esmall Fsmall Gsmall Hsmall " +
        "Ismall Jsmall Ksmall Lsmall Msmall Nsmall Osmall Psmall Qsmall Rsmall Ssmall Tsmall Usmall Vsmall " +
        "Wsmall Xsmall Ysmall Zsmall colonmonetary onefitted rupiah Tildesmall . . asuperior centsuperior . . " +
        ". . Aacutesmall Agravesmall Acircumflexsmall Adieresissmall Atildesmall Aringsmall Ccedillasmall " +
        "Eacutesmall Egravesmall Ecircumflexsmall Edieresissmall Iacutesmall Igravesmall Icircumflexsmall " +
        "Idieresissmall Ntildesmall Oacutesmall Ogravesmall Ocircumflexsmall Odieresissmall Otildesmall " +
        "Uacutesmall Ugravesmall Ucircumflexsmall Udieresissmall . eightsuperior fourinferior threeinferior " +
        "sixinferior eightinferior seveninferior Scaronsmall . centinferior twoinferior . Dieresissmall . " +
        "Caronsmall osuperior fiveinferior . commainferior periodinferior Yacutesmall . dollarinferior . . " +
        "Thornsmall . nineinferior zeroinferior Zcaronsmall AEsmall Oslashsmall questiondownsmall oneinferior " +
        "Lslashsmall . . . . . . Cedillasmall . . . . . OEsmall figuredash hyphensuperior . . . . " +
        "exclamdownsmall . Ydieresissmall . onesuperior twosuperior threesuperior foursuperior fivesuperior " +
        "sixsuperior sevensuperior ninesuperior zerosuperior . esuperior rsuperior tsuperior . . isuperior " +
        "ssuperior dsuperior . . . . . lsuperior Ogoneksmall Brevesmall Macronsmall bsuperior nsuperior " +
        "msuperior commasuperior periodsuperior Dotaccentsmall Ringsmall";

    private static string?[]? s_standard;
    private static string?[]? s_winAnsi;
    private static string?[]? s_macRoman;
    private static string?[]? s_macExpert;

    /// <summary>StandardEncoding: the built-in encoding of most Latin Type 1 fonts.</summary>
    public static string?[] StandardEncoding => LazyInitializer.EnsureInitialized(ref s_standard, static () => Expand(Standard))!;

    public static string?[] WinAnsiEncoding => LazyInitializer.EnsureInitialized(ref s_winAnsi, static () => Expand(WinAnsi))!;

    public static string?[] MacRomanEncoding => LazyInitializer.EnsureInitialized(ref s_macRoman, static () => Expand(MacRoman))!;

    public static string?[] MacExpertEncoding => LazyInitializer.EnsureInitialized(ref s_macExpert, static () => Expand(MacExpert))!;

    /// <summary>The encoding a /Encoding or /BaseEncoding name stands for, or null.</summary>
    public static string?[]? FromName(string? name) => name switch
    {
        "StandardEncoding" => StandardEncoding,
        "WinAnsiEncoding" => WinAnsiEncoding,
        "MacRomanEncoding" => MacRomanEncoding,
        "MacExpertEncoding" => MacExpertEncoding,
        _ => null,
    };

    private static string?[] Expand(string names)
    {
        var table = new string?[256];
        int code = 32;
        foreach (string name in names.Split(' '))
        {
            if (name != ".") table[code] = name;
            code++;
        }

        return table;
    }
}
