namespace ZDUpdater;

public static class VdfParser
{

    public static List<string> FindLibraryPaths(string vdfText)
    {
        var tokens = Tokenize(vdfText);
        var paths = new List<string>();
        for (int i = 0; i < tokens.Count - 1; i++)
        {
            if (string.Equals(tokens[i], "path", StringComparison.OrdinalIgnoreCase))
                paths.Add(tokens[i + 1]);
        }
        return paths;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        int i = 0;
        int n = text.Length;
        while (i < n)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < n && text[i + 1] == '/')
            {
                while (i < n && text[i] != '\n') i++;
                continue;
            }
            if (c == '{' || c == '}') { i++; continue; }
            if (c == '"')
            {
                i++;
                var sb = new System.Text.StringBuilder();
                while (i < n && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < n)
                    {

                        sb.Append(text[i + 1]);
                        i += 2;
                    }
                    else
                    {
                        sb.Append(text[i]);
                        i++;
                    }
                }
                i++;
                tokens.Add(sb.ToString());
                continue;
            }

            int start = i;
            while (i < n && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}') i++;
            tokens.Add(text[start..i]);
        }
        return tokens;
    }
}
