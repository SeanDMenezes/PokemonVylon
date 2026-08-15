using System.Security.Cryptography;
using System.Text;

namespace PatchBuilderGUI;

/// <summary>
/// Stores the GitHub token encrypted with DPAPI under the current Windows user. The file is
/// unreadable by other accounts and never leaves the machine. A token must never be compiled
/// into this executable, which is itself published as a public release asset.
/// </summary>
static class GitHubTokenStore
{
    static string TokenPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PokemonVylonPatchBuilder",
        "github-token.dat");

    public static string? Load()
    {
        string path = TokenPath;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            byte[] plain = ProtectedData.Unprotect(
                File.ReadAllBytes(path),
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);

            string token = Encoding.UTF8.GetString(plain).Trim();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (CryptographicException)
        {
            // Written by a different Windows user, or the profile was rebuilt.
            return null;
        }
    }

    public static void Save(string token)
    {
        string path = TokenPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        byte[] cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(token.Trim()),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);

        File.WriteAllBytes(path, cipher);
    }

    public static void Clear()
    {
        string path = TokenPath;
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
