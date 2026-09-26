using System.Text.Json;
using System.Text.RegularExpressions;

namespace VyronSkill.Tests;

/// <summary>
/// Static checks on the plugin sources: every localization key used in code must exist in both
/// language files, and no language entry may be left unused. A missing key would be printed verbatim
/// to players, so it is worth failing the build over.
/// </summary>
public class LocalizationTests
{
    private static readonly Regex KeyPattern = new("\"(vyron\\.[a-z0-9_.]+)\"", RegexOptions.Compiled);

    [Fact]
    public void EveryKeyUsedInCodeExistsInEveryLanguageFile()
    {
        var usedKeys = CollectKeysUsedInCode();
        Assert.NotEmpty(usedKeys);

        foreach (var (language, keys) in LoadLanguageFiles())
        {
            var missing = usedKeys.Where(key => !keys.ContainsKey(key)).OrderBy(key => key).ToList();
            Assert.True(missing.Count == 0, $"{language} is missing: {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void NoLanguageEntryIsUnused()
    {
        var usedKeys = CollectKeysUsedInCode();

        foreach (var (language, keys) in LoadLanguageFiles())
        {
            var unused = keys.Keys.Where(key => !usedKeys.Contains(key)).OrderBy(key => key).ToList();
            Assert.True(unused.Count == 0, $"{language} has unused entries: {string.Join(", ", unused)}");
        }
    }

    [Fact]
    public void AllLanguageFilesShareTheSameKeys()
    {
        var languages = LoadLanguageFiles();
        Assert.True(languages.Count >= 2, "expected at least an English and a Chinese language file");

        var reference = languages[0];
        foreach (var (language, keys) in languages.Skip(1))
        {
            var missing = reference.Keys.Keys.Except(keys.Keys).OrderBy(key => key).ToList();
            Assert.True(missing.Count == 0, $"{language} is missing keys present in {reference.Language}: {string.Join(", ", missing)}");
        }
    }

    /// <summary>
    /// The plugin must not contain format placeholders that the matching translation forgot, which
    /// would throw inside the game when the message is rendered.
    /// </summary>
    [Fact]
    public void FormatPlaceholdersMatchAcrossLanguages()
    {
        var languages = LoadLanguageFiles().ToDictionary(entry => entry.Language, entry => entry.Keys);
        var reference = languages.Values.First();

        foreach (var (key, value) in reference)
        {
            var expected = Placeholders(value);

            foreach (var (language, keys) in languages)
            {
                if (!keys.TryGetValue(key, out var translated))
                {
                    continue;
                }

                Assert.True(
                    expected.SetEquals(Placeholders(translated)),
                    $"{language}:{key} uses placeholders {{{string.Join(",", Placeholders(translated).OrderBy(x => x))}}} but the reference uses {{{string.Join(",", expected.OrderBy(x => x))}}}");
            }
        }
    }

    private static HashSet<string> Placeholders(string value)
        => Regex.Matches(value, @"\{(\d+)(?::[^}]*)?\}")
            .Select(match => match.Groups[1].Value)
            .ToHashSet();

    private static HashSet<string> CollectKeysUsedInCode()
    {
        var pluginDirectory = Path.Combine(RepositoryRoot(), "src", "VyronSkill");
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.GetFiles(pluginDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(file);

            foreach (Match match in KeyPattern.Matches(content))
            {
                keys.Add(match.Groups[1].Value);
            }
        }

        return keys;
    }

    private static List<(string Language, Dictionary<string, string> Keys)> LoadLanguageFiles()
    {
        var languageDirectory = Path.Combine(RepositoryRoot(), "src", "VyronSkill", "lang");
        var files = Directory.GetFiles(languageDirectory, "*.json").OrderBy(file => file).ToList();

        Assert.True(files.Count > 0, $"no language files found in {languageDirectory}");

        return files.Select(file =>
        {
            var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))
                       ?? throw new InvalidOperationException($"{file} could not be parsed");

            return (Path.GetFileNameWithoutExtension(file), keys);
        }).ToList();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "VyronSkill", "VyronSkill.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("could not locate the repository root from " + AppContext.BaseDirectory);
    }
}
