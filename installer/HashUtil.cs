using System.Security.Cryptography;

namespace ZDUpdater;

public static class HashUtil
{
    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Md5File(string path)
    {
        using var stream = File.OpenRead(path);
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool Sha256Matches(string path, string expectedHex) =>
        File.Exists(path) &&
        string.Equals(Sha256File(path), expectedHex, StringComparison.OrdinalIgnoreCase);

    public static void AtomicPlace(string tempSourcePath, string destPath)
    {
        var destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);
        File.Move(tempSourcePath, destPath, overwrite: true);
    }
}
