namespace MagicRDR.Core;

public static class RpfFileTypes
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".tr", ".txt", ".csv", ".sc.xml", ".refgroup", ".fxlist", ".modellist", ".emitlist",
        ".fullfxlist", ".shaderlist", ".texlist", ".ptxlist", ".raw", ".expl", ".textune", ".hud",
        ".envclothmanager", ".charclothmanager", ".traffic", ".arm", ".mtl", ".weap", ".cfg", ".fx",
        ".todlight", ".ppp", ".dat", ".tune",
    };

    /// <summary>
    /// Extensions tried when a bare word is found inside a text file (e.g. <c>strings</c> -> <c>strings.txt</c>).
    /// Text formats come from the original tool; the resource extensions are best guesses and only cost a few hash
    /// computations when wrong, since a candidate is accepted only if its hash matches an unnamed entry.
    /// </summary>
    public static readonly string[] CandidateExtensions =
    {
        ".rpf", ".xml", ".txt", ".csv", ".tr", ".dat", ".cfg", ".fx", ".fxc", ".nvn", ".hud", ".tune",
        ".refgroup", ".fxlist", ".modellist", ".emitlist", ".fullfxlist", ".shaderlist", ".texlist", ".ptxlist",
        ".raw", ".expl", ".textune", ".envclothmanager", ".charclothmanager", ".traffic", ".arm", ".mtl",
        ".weap", ".todlight", ".ppp",
        ".wsc", ".xsc", ".csc", ".sc", ".wft", ".xft", ".cft", ".wfd", ".xfd", ".wtd", ".xtd", ".wvd", ".xvd",
        ".wbd", ".xbd", ".wsi", ".xsi", ".wst", ".xst", ".wsf", ".xsf", ".awc", ".strtbl",
    };

    /// <summary>
    /// Files the original tool always stored compressed when imported (plain-text style formats).
    /// Ported from <c>ResourceUtils.IsTextFile</c>.
    /// </summary>
    public static bool IsTextFile(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        int dot = fileName.LastIndexOf('.');
        return dot >= 0 && TextExtensions.Contains(fileName.Substring(dot));
    }
}
