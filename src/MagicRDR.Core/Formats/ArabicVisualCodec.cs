using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MagicRDR.Core.Formats;

/// <summary>
/// Converts Arabic between the two forms it takes in Red Dead Redemption string tables.
/// <para>
/// The game draws every string strictly left to right with no Arabic shaping. Arabic text therefore has to be stored
/// in <b>visual</b> form: letters already replaced by their Unicode <i>Presentation Forms</i> (U+FE70–U+FEFF, U+FB50–U+FDFF)
/// and the characters laid out in reverse. People (and Android's text engine) work with <b>logical</b> Arabic:
/// base letters (U+0600–U+06FF) in reading order, which the platform shapes and reorders itself.
/// </para>
/// <list type="bullet">
/// <item><see cref="Decode"/>: visual to logical, for showing and exporting stored text.</item>
/// <item><see cref="Encode"/>: logical to visual, for storing what the user typed or imported.</item>
/// </list>
/// Left-to-right material inside a line (Latin words, numbers, <c>&lt;red&gt;</c>-style tags, <c>~r~</c> colour codes,
/// <c>%s</c>-style placeholders, <c>{0}</c>, literal <c>\n</c>) is kept as one unit, so it is never reversed or scrambled.
/// Mirrored brackets are swapped. Line breaks (<c>\r</c>, <c>\n</c>) stay where they are and every line is handled on its own.
/// Strings without Arabic letters are returned unchanged.
/// </summary>
public static class ArabicVisualCodec
{
    private enum Kind : byte
    {
        Rtl,       // one Arabic letter (with its diacritics), or other Arabic-script character
        Ltr,       // an unbreakable left-to-right unit: word, number, tag, placeholder
        Neutral,   // space, punctuation
        Bracket,   // ( ) [ ] { } < > « » ‹ ›  - swapped when the direction is reversed
    }

    private readonly record struct Token(string Text, Kind Kind);

    private readonly record struct ShapeForms(char Isolated, char Final, char Initial, char Medial)
    {
        /// <summary>Joins to both neighbours (has initial and medial forms).</summary>
        public bool IsDual => Initial != '\0';

        /// <summary>Can connect to the letter before it.</summary>
        public bool CanJoinBack => Final != '\0' || Initial != '\0';
    }

    // Tags (<red>, </red>), colour codes (~r~), printf placeholders (%s, %1$d), {0}-style placeholders and literal \n \r \t.
    private static readonly Regex Atomic = new(
        @"\G(?:<\/?[A-Za-z][^<>\r\n]{0,63}>|~[A-Za-z0-9_]{1,16}~|%(?:\d+\$)?\d*(?:\.\d+)?[sdifuxXc]|\{[A-Za-z0-9_]{1,16}\}|\\[rnt])",
        RegexOptions.CultureInvariant);

    private static readonly Dictionary<char, (char Isolated, char Final)> LamAlef = new()
    {
        ['\u0622'] = ('\uFEF5', '\uFEF6'), // lam + alef with madda above
        ['\u0623'] = ('\uFEF7', '\uFEF8'), // lam + alef with hamza above
        ['\u0625'] = ('\uFEF9', '\uFEFA'), // lam + alef with hamza below
        ['\u0627'] = ('\uFEFB', '\uFEFC'), // lam + alef
    };

    // presentation form -> base letter(s), e.g. FEFB -> 0644 0627
    private const string UnshapeTable =
        "FB50=0671;FB51=0671;FB52=067B;FB53=067B;FB54=067B;FB55=067B;FB56=067E;FB57=067E;FB58=067E;" +
        "FB59=067E;FB5A=0680;FB5B=0680;FB5C=0680;FB5D=0680;FB5E=067A;FB5F=067A;FB60=067A;FB61=067A;" +
        "FB62=067F;FB63=067F;FB64=067F;FB65=067F;FB66=0679;FB67=0679;FB68=0679;FB69=0679;FB6A=06A4;" +
        "FB6B=06A4;FB6C=06A4;FB6D=06A4;FB6E=06A6;FB6F=06A6;FB70=06A6;FB71=06A6;FB72=0684;FB73=0684;" +
        "FB74=0684;FB75=0684;FB76=0683;FB77=0683;FB78=0683;FB79=0683;FB7A=0686;FB7B=0686;FB7C=0686;" +
        "FB7D=0686;FB7E=0687;FB7F=0687;FB80=0687;FB81=0687;FB82=068D;FB83=068D;FB84=068C;FB85=068C;" +
        "FB86=068E;FB87=068E;FB88=0688;FB89=0688;FB8A=0698;FB8B=0698;FB8C=0691;FB8D=0691;FB8E=06A9;" +
        "FB8F=06A9;FB90=06A9;FB91=06A9;FB92=06AF;FB93=06AF;FB94=06AF;FB95=06AF;FB96=06B3;FB97=06B3;" +
        "FB98=06B3;FB99=06B3;FB9A=06B1;FB9B=06B1;FB9C=06B1;FB9D=06B1;FB9E=06BA;FB9F=06BA;FBA0=06BB;" +
        "FBA1=06BB;FBA2=06BB;FBA3=06BB;FBA4=06C0;FBA5=06C0;FBA6=06C1;FBA7=06C1;FBA8=06C1;FBA9=06C1;" +
        "FBAA=06BE;FBAB=06BE;FBAC=06BE;FBAD=06BE;FBAE=06D2;FBAF=06D2;FBB0=06D3;FBB1=06D3;FBD3=06AD;" +
        "FBD4=06AD;FBD5=06AD;FBD6=06AD;FBD7=06C7;FBD8=06C7;FBD9=06C6;FBDA=06C6;FBDB=06C8;FBDC=06C8;" +
        "FBDD=0677;FBDE=06CB;FBDF=06CB;FBE0=06C5;FBE1=06C5;FBE2=06C9;FBE3=06C9;FBE4=06D0;FBE5=06D0;" +
        "FBE6=06D0;FBE7=06D0;FBE8=0649;FBE9=0649;FBEA=06260627;FBEB=06260627;FBEC=062606D5;FBED=062606D5;" +
        "FBEE=06260648;FBEF=06260648;FBF0=062606C7;FBF1=062606C7;FBF2=062606C6;FBF3=062606C6;" +
        "FBF4=062606C8;FBF5=062606C8;FBF6=062606D0;FBF7=062606D0;FBF8=062606D0;FBF9=06260649;" +
        "FBFA=06260649;FBFB=06260649;FBFC=06CC;FBFD=06CC;FBFE=06CC;FBFF=06CC;FC00=0626062C;FC01=0626062D;" +
        "FC02=06260645;FC03=06260649;FC04=0626064A;FC05=0628062C;FC06=0628062D;FC07=0628062E;" +
        "FC08=06280645;FC09=06280649;FC0A=0628064A;FC0B=062A062C;FC0C=062A062D;FC0D=062A062E;" +
        "FC0E=062A0645;FC0F=062A0649;FC10=062A064A;FC11=062B062C;FC12=062B0645;FC13=062B0649;" +
        "FC14=062B064A;FC15=062C062D;FC16=062C0645;FC17=062D062C;FC18=062D0645;FC19=062E062C;" +
        "FC1A=062E062D;FC1B=062E0645;FC1C=0633062C;FC1D=0633062D;FC1E=0633062E;FC1F=06330645;" +
        "FC20=0635062D;FC21=06350645;FC22=0636062C;FC23=0636062D;FC24=0636062E;FC25=06360645;" +
        "FC26=0637062D;FC27=06370645;FC28=06380645;FC29=0639062C;FC2A=06390645;FC2B=063A062C;" +
        "FC2C=063A0645;FC2D=0641062C;FC2E=0641062D;FC2F=0641062E;FC30=06410645;FC31=06410649;" +
        "FC32=0641064A;FC33=0642062D;FC34=06420645;FC35=06420649;FC36=0642064A;FC37=06430627;" +
        "FC38=0643062C;FC39=0643062D;FC3A=0643062E;FC3B=06430644;FC3C=06430645;FC3D=06430649;" +
        "FC3E=0643064A;FC3F=0644062C;FC40=0644062D;FC41=0644062E;FC42=06440645;FC43=06440649;" +
        "FC44=0644064A;FC45=0645062C;FC46=0645062D;FC47=0645062E;FC48=06450645;FC49=06450649;" +
        "FC4A=0645064A;FC4B=0646062C;FC4C=0646062D;FC4D=0646062E;FC4E=06460645;FC4F=06460649;" +
        "FC50=0646064A;FC51=0647062C;FC52=06470645;FC53=06470649;FC54=0647064A;FC55=064A062C;" +
        "FC56=064A062D;FC57=064A062E;FC58=064A0645;FC59=064A0649;FC5A=064A064A;FC5B=06300670;" +
        "FC5C=06310670;FC5D=06490670;FC64=06260631;FC65=06260632;FC66=06260645;FC67=06260646;" +
        "FC68=06260649;FC69=0626064A;FC6A=06280631;FC6B=06280632;FC6C=06280645;FC6D=06280646;" +
        "FC6E=06280649;FC6F=0628064A;FC70=062A0631;FC71=062A0632;FC72=062A0645;FC73=062A0646;" +
        "FC74=062A0649;FC75=062A064A;FC76=062B0631;FC77=062B0632;FC78=062B0645;FC79=062B0646;" +
        "FC7A=062B0649;FC7B=062B064A;FC7C=06410649;FC7D=0641064A;FC7E=06420649;FC7F=0642064A;" +
        "FC80=06430627;FC81=06430644;FC82=06430645;FC83=06430649;FC84=0643064A;FC85=06440645;" +
        "FC86=06440649;FC87=0644064A;FC88=06450627;FC89=06450645;FC8A=06460631;FC8B=06460632;" +
        "FC8C=06460645;FC8D=06460646;FC8E=06460649;FC8F=0646064A;FC90=06490670;FC91=064A0631;" +
        "FC92=064A0632;FC93=064A0645;FC94=064A0646;FC95=064A0649;FC96=064A064A;FC97=0626062C;" +
        "FC98=0626062D;FC99=0626062E;FC9A=06260645;FC9B=06260647;FC9C=0628062C;FC9D=0628062D;" +
        "FC9E=0628062E;FC9F=06280645;FCA0=06280647;FCA1=062A062C;FCA2=062A062D;FCA3=062A062E;" +
        "FCA4=062A0645;FCA5=062A0647;FCA6=062B0645;FCA7=062C062D;FCA8=062C0645;FCA9=062D062C;" +
        "FCAA=062D0645;FCAB=062E062C;FCAC=062E0645;FCAD=0633062C;FCAE=0633062D;FCAF=0633062E;" +
        "FCB0=06330645;FCB1=0635062D;FCB2=0635062E;FCB3=06350645;FCB4=0636062C;FCB5=0636062D;" +
        "FCB6=0636062E;FCB7=06360645;FCB8=0637062D;FCB9=06380645;FCBA=0639062C;FCBB=06390645;" +
        "FCBC=063A062C;FCBD=063A0645;FCBE=0641062C;FCBF=0641062D;FCC0=0641062E;FCC1=06410645;" +
        "FCC2=0642062D;FCC3=06420645;FCC4=0643062C;FCC5=0643062D;FCC6=0643062E;FCC7=06430644;" +
        "FCC8=06430645;FCC9=0644062C;FCCA=0644062D;FCCB=0644062E;FCCC=06440645;FCCD=06440647;" +
        "FCCE=0645062C;FCCF=0645062D;FCD0=0645062E;FCD1=06450645;FCD2=0646062C;FCD3=0646062D;" +
        "FCD4=0646062E;FCD5=06460645;FCD6=06460647;FCD7=0647062C;FCD8=06470645;FCD9=06470670;" +
        "FCDA=064A062C;FCDB=064A062D;FCDC=064A062E;FCDD=064A0645;FCDE=064A0647;FCDF=06260645;" +
        "FCE0=06260647;FCE1=06280645;FCE2=06280647;FCE3=062A0645;FCE4=062A0647;FCE5=062B0645;" +
        "FCE6=062B0647;FCE7=06330645;FCE8=06330647;FCE9=06340645;FCEA=06340647;FCEB=06430644;" +
        "FCEC=06430645;FCED=06440645;FCEE=06460645;FCEF=06460647;FCF0=064A0645;FCF1=064A0647;" +
        "FCF5=06370649;FCF6=0637064A;FCF7=06390649;FCF8=0639064A;FCF9=063A0649;FCFA=063A064A;" +
        "FCFB=06330649;FCFC=0633064A;FCFD=06340649;FCFE=0634064A;FCFF=062D0649;FD00=062D064A;" +
        "FD01=062C0649;FD02=062C064A;FD03=062E0649;FD04=062E064A;FD05=06350649;FD06=0635064A;" +
        "FD07=06360649;FD08=0636064A;FD09=0634062C;FD0A=0634062D;FD0B=0634062E;FD0C=06340645;" +
        "FD0D=06340631;FD0E=06330631;FD0F=06350631;FD10=06360631;FD11=06370649;FD12=0637064A;" +
        "FD13=06390649;FD14=0639064A;FD15=063A0649;FD16=063A064A;FD17=06330649;FD18=0633064A;" +
        "FD19=06340649;FD1A=0634064A;FD1B=062D0649;FD1C=062D064A;FD1D=062C0649;FD1E=062C064A;" +
        "FD1F=062E0649;FD20=062E064A;FD21=06350649;FD22=0635064A;FD23=06360649;FD24=0636064A;" +
        "FD25=0634062C;FD26=0634062D;FD27=0634062E;FD28=06340645;FD29=06340631;FD2A=06330631;" +
        "FD2B=06350631;FD2C=06360631;FD2D=0634062C;FD2E=0634062D;FD2F=0634062E;FD30=06340645;" +
        "FD31=06330647;FD32=06340647;FD33=06370645;FD34=0633062C;FD35=0633062D;FD36=0633062E;" +
        "FD37=0634062C;FD38=0634062D;FD39=0634062E;FD3A=06370645;FD3B=06380645;FD3C=0627064B;" +
        "FD3D=0627064B;FE70=0020064B;FE71=0640064B;FE72=0020064C;FE74=0020064D;FE76=0020064E;" +
        "FE77=0640064E;FE78=0020064F;FE79=0640064F;FE7A=00200650;FE7B=06400650;FE7C=00200651;" +
        "FE7D=06400651;FE7E=00200652;FE7F=06400652;FE80=0621;FE81=0622;FE82=0622;FE83=0623;FE84=0623;" +
        "FE85=0624;FE86=0624;FE87=0625;FE88=0625;FE89=0626;FE8A=0626;FE8B=0626;FE8C=0626;FE8D=0627;" +
        "FE8E=0627;FE8F=0628;FE90=0628;FE91=0628;FE92=0628;FE93=0629;FE94=0629;FE95=062A;FE96=062A;" +
        "FE97=062A;FE98=062A;FE99=062B;FE9A=062B;FE9B=062B;FE9C=062B;FE9D=062C;FE9E=062C;FE9F=062C;" +
        "FEA0=062C;FEA1=062D;FEA2=062D;FEA3=062D;FEA4=062D;FEA5=062E;FEA6=062E;FEA7=062E;FEA8=062E;" +
        "FEA9=062F;FEAA=062F;FEAB=0630;FEAC=0630;FEAD=0631;FEAE=0631;FEAF=0632;FEB0=0632;FEB1=0633;" +
        "FEB2=0633;FEB3=0633;FEB4=0633;FEB5=0634;FEB6=0634;FEB7=0634;FEB8=0634;FEB9=0635;FEBA=0635;" +
        "FEBB=0635;FEBC=0635;FEBD=0636;FEBE=0636;FEBF=0636;FEC0=0636;FEC1=0637;FEC2=0637;FEC3=0637;" +
        "FEC4=0637;FEC5=0638;FEC6=0638;FEC7=0638;FEC8=0638;FEC9=0639;FECA=0639;FECB=0639;FECC=0639;" +
        "FECD=063A;FECE=063A;FECF=063A;FED0=063A;FED1=0641;FED2=0641;FED3=0641;FED4=0641;FED5=0642;" +
        "FED6=0642;FED7=0642;FED8=0642;FED9=0643;FEDA=0643;FEDB=0643;FEDC=0643;FEDD=0644;FEDE=0644;" +
        "FEDF=0644;FEE0=0644;FEE1=0645;FEE2=0645;FEE3=0645;FEE4=0645;FEE5=0646;FEE6=0646;FEE7=0646;" +
        "FEE8=0646;FEE9=0647;FEEA=0647;FEEB=0647;FEEC=0647;FEED=0648;FEEE=0648;FEEF=0649;FEF0=0649;" +
        "FEF1=064A;FEF2=064A;FEF3=064A;FEF4=064A;FEF5=06440622;FEF6=06440622;FEF7=06440623;FEF8=06440623;" +
        "FEF9=06440625;FEFA=06440625;FEFB=06440627;FEFC=06440627;";

    private const string FormsTable =
        "0621=FE80,0000,0000,0000;0622=FE81,FE82,0000,0000;0623=FE83,FE84,0000,0000;" +
        "0624=FE85,FE86,0000,0000;0625=FE87,FE88,0000,0000;0626=FE89,FE8A,FE8B,FE8C;" +
        "0627=FE8D,FE8E,0000,0000;0628=FE8F,FE90,FE91,FE92;0629=FE93,FE94,0000,0000;" +
        "062A=FE95,FE96,FE97,FE98;062B=FE99,FE9A,FE9B,FE9C;062C=FE9D,FE9E,FE9F,FEA0;" +
        "062D=FEA1,FEA2,FEA3,FEA4;062E=FEA5,FEA6,FEA7,FEA8;062F=FEA9,FEAA,0000,0000;" +
        "0630=FEAB,FEAC,0000,0000;0631=FEAD,FEAE,0000,0000;0632=FEAF,FEB0,0000,0000;" +
        "0633=FEB1,FEB2,FEB3,FEB4;0634=FEB5,FEB6,FEB7,FEB8;0635=FEB9,FEBA,FEBB,FEBC;" +
        "0636=FEBD,FEBE,FEBF,FEC0;0637=FEC1,FEC2,FEC3,FEC4;0638=FEC5,FEC6,FEC7,FEC8;" +
        "0639=FEC9,FECA,FECB,FECC;063A=FECD,FECE,FECF,FED0;0640=0640,0640,0640,0640;" +
        "0641=FED1,FED2,FED3,FED4;0642=FED5,FED6,FED7,FED8;0643=FED9,FEDA,FEDB,FEDC;" +
        "0644=FEDD,FEDE,FEDF,FEE0;0645=FEE1,FEE2,FEE3,FEE4;0646=FEE5,FEE6,FEE7,FEE8;" +
        "0647=FEE9,FEEA,FEEB,FEEC;0648=FEED,FEEE,0000,0000;0649=FEEF,FEF0,0000,0000;" +
        "064A=FEF1,FEF2,FEF3,FEF4;0671=FB50,FB51,0000,0000;0677=FBDD,0000,0000,0000;" +
        "0679=FB66,FB67,FB68,FB69;067A=FB5E,FB5F,FB60,FB61;067B=FB52,FB53,FB54,FB55;" +
        "067E=FB56,FB57,FB58,FB59;067F=FB62,FB63,FB64,FB65;0680=FB5A,FB5B,FB5C,FB5D;" +
        "0683=FB76,FB77,FB78,FB79;0684=FB72,FB73,FB74,FB75;0686=FB7A,FB7B,FB7C,FB7D;" +
        "0687=FB7E,FB7F,FB80,FB81;0688=FB88,FB89,0000,0000;068C=FB84,FB85,0000,0000;" +
        "068D=FB82,FB83,0000,0000;068E=FB86,FB87,0000,0000;0691=FB8C,FB8D,0000,0000;" +
        "0698=FB8A,FB8B,0000,0000;06A4=FB6A,FB6B,FB6C,FB6D;06A6=FB6E,FB6F,FB70,FB71;" +
        "06A9=FB8E,FB8F,FB90,FB91;06AD=FBD3,FBD4,FBD5,FBD6;06AF=FB92,FB93,FB94,FB95;" +
        "06B1=FB9A,FB9B,FB9C,FB9D;06B3=FB96,FB97,FB98,FB99;06BA=FB9E,FB9F,0000,0000;" +
        "06BB=FBA0,FBA1,FBA2,FBA3;06BE=FBAA,FBAB,FBAC,FBAD;06C0=FBA4,FBA5,0000,0000;" +
        "06C1=FBA6,FBA7,FBA8,FBA9;06C5=FBE0,FBE1,0000,0000;06C6=FBD9,FBDA,0000,0000;" +
        "06C7=FBD7,FBD8,0000,0000;06C8=FBDB,FBDC,0000,0000;06C9=FBE2,FBE3,0000,0000;" +
        "06CB=FBDE,FBDF,0000,0000;06CC=FBFC,FBFD,FBFE,FBFF;06D0=FBE4,FBE5,FBE6,FBE7;" +
        "06D2=FBAE,FBAF,0000,0000;06D3=FBB0,FBB1,0000,0000;";

    private static readonly Dictionary<char, string> Unshape = BuildUnshape();

    private static readonly Dictionary<char, ShapeForms> Forms = BuildForms();

    private static Dictionary<char, string> BuildUnshape()
    {
        var map = new Dictionary<char, string>(700);
        foreach (string entry in UnshapeTable.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = entry.IndexOf('=');
            char key = (char)int.Parse(entry.AsSpan(0, equals), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            ReadOnlySpan<char> value = entry.AsSpan(equals + 1);
            var text = new StringBuilder(2);
            for (int i = 0; i + 4 <= value.Length; i += 4)
            {
                text.Append((char)int.Parse(value.Slice(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }

            map[key] = text.ToString();
        }

        return map;
    }

    private static Dictionary<char, ShapeForms> BuildForms()
    {
        var map = new Dictionary<char, ShapeForms>(100);
        foreach (string entry in FormsTable.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = entry.IndexOf('=');
            char letter = (char)int.Parse(entry.AsSpan(0, equals), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            string[] parts = entry.Substring(equals + 1).Split(',');
            char Form(int index) => (char)int.Parse(parts[index], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            map[letter] = new ShapeForms(Form(0), Form(1), Form(2), Form(3));
        }

        return map;
    }

    // ------------------------------------------------------------------ public API

    /// <summary>
    /// <c>true</c> if the text contains Arabic Presentation Forms, which is how stored (visual) Arabic is recognised.
    /// </summary>
    public static bool HasPresentationForms(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (char c in text)
        {
            if (c >= '\uFB50' && Unshape.ContainsKey(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>true</c> if the text contains any Arabic letter, in either form.</summary>
    public static bool ContainsArabic(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (char c in text)
        {
            if (IsArabicBlock(c) && !IsArabicNeutral(c) && !char.IsDigit(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Stored (visual) text to natural, logical Arabic. Text without Arabic Presentation Forms is returned as it is,
    /// so Latin strings and Arabic that is already logical are never touched.
    /// </summary>
    public static string Decode(string? visual)
    {
        if (string.IsNullOrEmpty(visual) || !HasPresentationForms(visual))
        {
            return visual ?? string.Empty;
        }

        return ConvertText(visual, encode: false);
    }

    /// <summary>
    /// Natural, logical Arabic to the form the game stores: shaped into Presentation Forms and laid out visually.
    /// Text without Arabic letters is returned as it is. Text that already contains Presentation Forms is taken to be
    /// stored text and is returned as it is, so a string is never reversed twice.
    /// </summary>
    public static string Encode(string? logical)
    {
        if (string.IsNullOrEmpty(logical) || HasPresentationForms(logical) || !ContainsArabic(logical))
        {
            return logical ?? string.Empty;
        }

        return ConvertText(logical, encode: true);
    }

    // ------------------------------------------------------------------ conversion

    private static string ConvertText(string text, bool encode)
    {
        var result = new StringBuilder(text.Length);
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            bool endOfLine = i == text.Length || text[i] == '\r' || text[i] == '\n';
            if (!endOfLine)
            {
                continue;
            }

            if (i > start)
            {
                result.Append(ConvertLine(text.Substring(start, i - start), encode));
            }

            if (i < text.Length)
            {
                result.Append(text[i]);
            }

            start = i + 1;
        }

        return result.ToString();
    }

    private static string ConvertLine(string line, bool encode)
    {
        List<Token> tokens = Tokenize(line);
        bool hasArabic = false;
        foreach (Token token in tokens)
        {
            if (token.Kind == Kind.Rtl)
            {
                hasArabic = true;
                break;
            }
        }

        if (!hasArabic)
        {
            return line;
        }

        if (encode)
        {
            tokens = Shape(tokens);
        }

        tokens = Reorder(tokens);

        var text = new StringBuilder(line.Length);
        foreach (Token token in tokens)
        {
            if (!encode && token.Kind == Kind.Rtl)
            {
                foreach (char c in token.Text)
                {
                    text.Append(Unshape.TryGetValue(c, out string? baseLetters) ? baseLetters : c.ToString());
                }
            }
            else
            {
                text.Append(token.Text);
            }
        }

        return text.ToString();
    }

    // ------------------------------------------------------------------ tokens

    private static bool IsArabicBlock(char c) =>
        (c >= '\u0600' && c <= '\u06FF')
        || (c >= '\u0750' && c <= '\u077F')
        || (c >= '\u08A0' && c <= '\u08FF')
        || (c >= '\uFB50' && c <= '\uFDFF')
        || (c >= '\uFE70' && c <= '\uFEFE');

    /// <summary>Arabic comma and the Arabic percent / decimal / thousands signs behave like ordinary punctuation.</summary>
    private static bool IsArabicNeutral(char c) => c == '\u060C' || (c >= '\u066A' && c <= '\u066D');

    private static bool IsBaseMark(char c) =>
        (c >= '\u064B' && c <= '\u065F')
        || c == '\u0670'
        || (c >= '\u06D6' && c <= '\u06DC')
        || (c >= '\u06DF' && c <= '\u06E4')
        || c == '\u06E7'
        || c == '\u06E8'
        || (c >= '\u06EA' && c <= '\u06ED');

    private static bool IsMark(char c)
    {
        if (IsBaseMark(c))
        {
            return true;
        }

        return Unshape.TryGetValue(c, out string? baseLetters) && baseLetters.Length == 1 && IsBaseMark(baseLetters[0]);
    }

    private static bool IsLtrLetter(char c) => char.IsLetter(c) && !IsArabicBlock(c);

    private static bool IsBracket(char c) => c is '(' or ')' or '[' or ']' or '{' or '}' or '<' or '>' or '\u00AB' or '\u00BB' or '\u2039' or '\u203A';

    private static char Mirror(char c) => c switch
    {
        '(' => ')',
        ')' => '(',
        '[' => ']',
        ']' => '[',
        '{' => '}',
        '}' => '{',
        '<' => '>',
        '>' => '<',
        '\u00AB' => '\u00BB',
        '\u00BB' => '\u00AB',
        '\u2039' => '\u203A',
        '\u203A' => '\u2039',
        _ => c,
    };

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (c is '<' or '~' or '%' or '{' or '\\')
            {
                Match match = Atomic.Match(text, i);
                if (match.Success && match.Length > 0)
                {
                    tokens.Add(new Token(match.Value, Kind.Ltr));
                    i += match.Length;
                    continue;
                }
            }

            if (char.IsDigit(c) || IsLtrLetter(c))
            {
                int j = i;
                while (j < text.Length)
                {
                    char ch = text[j];
                    if (char.IsDigit(ch) || IsLtrLetter(ch))
                    {
                        j++;
                    }
                    else if ((ch == '.' || ch == ',' || ch == ':' || ch == '/') && j > i && char.IsDigit(text[j - 1]) && j + 1 < text.Length && char.IsDigit(text[j + 1]))
                    {
                        j++;   // 1,000  3.5  12:30
                    }
                    else if (ch == '%' && j > i && char.IsDigit(text[j - 1]))
                    {
                        j++;   // 50%
                    }
                    else if ((ch == '\'' || ch == '\u2019') && j > i && IsLtrLetter(text[j - 1]) && j + 1 < text.Length && IsLtrLetter(text[j + 1]))
                    {
                        j++;   // don't
                    }
                    else
                    {
                        break;
                    }
                }

                tokens.Add(new Token(text.Substring(i, j - i), Kind.Ltr));
                i = j;
                continue;
            }

            if (IsArabicBlock(c) && !IsArabicNeutral(c))
            {
                int j = i + 1;
                while (j < text.Length && IsArabicBlock(text[j]) && IsMark(text[j]))
                {
                    j++;
                }

                tokens.Add(new Token(text.Substring(i, j - i), Kind.Rtl));
                i = j;
                continue;
            }

            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                tokens.Add(new Token(text.Substring(i, 2), Kind.Neutral));
                i += 2;
                continue;
            }

            tokens.Add(new Token(c.ToString(), IsBracket(c) ? Kind.Bracket : Kind.Neutral));
            i++;
        }

        return tokens;
    }

    /// <summary>
    /// Joins left-to-right units that are only separated by spaces or punctuation ("Hello, World", "abc &lt;red&gt;")
    /// into one unit, which is how a bidirectional layout keeps such a phrase in reading order.
    /// </summary>
    private static List<Token> MergeLtr(List<Token> tokens)
    {
        var result = new List<Token>(tokens.Count);
        int i = 0;
        while (i < tokens.Count)
        {
            if (tokens[i].Kind != Kind.Ltr)
            {
                result.Add(tokens[i]);
                i++;
                continue;
            }

            var text = new StringBuilder(tokens[i].Text);
            int end = i + 1;
            while (true)
            {
                int k = end;
                while (k < tokens.Count && (tokens[k].Kind == Kind.Neutral || tokens[k].Kind == Kind.Bracket))
                {
                    k++;
                }

                if (k < tokens.Count && tokens[k].Kind == Kind.Ltr)
                {
                    for (int x = end; x <= k; x++)
                    {
                        text.Append(tokens[x].Text);
                    }

                    end = k + 1;
                }
                else
                {
                    break;
                }
            }

            result.Add(new Token(text.ToString(), Kind.Ltr));
            i = end;
        }

        return result;
    }

    /// <summary>
    /// The one step both directions share: reverse the order of the units and swap mirrored brackets.
    /// Reversing twice gives back the original, which is why decoding and encoding are exact inverses of each other.
    /// </summary>
    private static List<Token> Reorder(List<Token> tokens)
    {
        List<Token> merged = MergeLtr(tokens);
        merged.Reverse();
        for (int i = 0; i < merged.Count; i++)
        {
            if (merged[i].Kind == Kind.Bracket)
            {
                merged[i] = new Token(Mirror(merged[i].Text[0]).ToString(), Kind.Bracket);
            }
        }

        return merged;
    }

    // ------------------------------------------------------------------ shaping

    /// <summary>Replaces base letters with the Initial / Medial / Final / Isolated Presentation Form that fits their neighbours.</summary>
    private static List<Token> Shape(List<Token> tokens)
    {
        var result = new List<Token>(tokens.Count);
        bool previousJoinsForward = false;
        int i = 0;
        while (i < tokens.Count)
        {
            Token token = tokens[i];
            if (token.Kind != Kind.Rtl)
            {
                result.Add(token);
                previousJoinsForward = false;
                i++;
                continue;
            }

            string text = token.Text;
            char letter = text[0];
            if (!Forms.TryGetValue(letter, out ShapeForms forms))
            {
                result.Add(token);   // not a letter that has forms: digits, ؟ ؛, diacritics on their own...
                previousJoinsForward = false;
                i++;
                continue;
            }

            bool hasNext = i + 1 < tokens.Count && tokens[i + 1].Kind == Kind.Rtl;
            string nextText = hasNext ? tokens[i + 1].Text : string.Empty;

            // Lam followed directly by an alef is one ligature glyph.
            if (letter == '\u0644' && text.Length == 1 && nextText.Length == 1 && LamAlef.TryGetValue(nextText[0], out (char Isolated, char Final) ligature))
            {
                result.Add(new Token((previousJoinsForward ? ligature.Final : ligature.Isolated).ToString(), Kind.Rtl));
                previousJoinsForward = false;
                i += 2;
                continue;
            }

            bool nextCanJoinBack = hasNext
                && !IsMark(nextText[0])
                && Forms.TryGetValue(nextText[0], out ShapeForms nextForms)
                && nextForms.CanJoinBack;

            bool connectPrevious = previousJoinsForward && forms.CanJoinBack;
            bool connectNext = forms.IsDual && nextCanJoinBack;

            char form;
            if (forms.IsDual)
            {
                form = connectPrevious && connectNext ? forms.Medial
                    : connectPrevious ? forms.Final
                    : connectNext ? forms.Initial
                    : forms.Isolated;
            }
            else
            {
                form = connectPrevious ? forms.Final : forms.Isolated;
            }

            if (form == '\0')
            {
                form = forms.Isolated != '\0' ? forms.Isolated : letter;
            }

            result.Add(new Token(form.ToString() + text.Substring(1), Kind.Rtl));
            previousJoinsForward = forms.IsDual;
            i++;
        }

        return result;
    }
}
