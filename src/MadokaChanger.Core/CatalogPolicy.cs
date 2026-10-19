namespace MadokaChanger.Core;

public static class CatalogPolicy
{
    public static bool IsClassicChampion(string alias) => alias.StartsWith("Jade_",StringComparison.OrdinalIgnoreCase);
    public static bool IsClassicRepositoryPath(string path) => path.Replace('\\','/').Split('/')[0].Equals("classic",StringComparison.OrdinalIgnoreCase);
    public static Catalog WithoutClassic(Catalog catalog) => catalog with { Champions=catalog.Champions.Where(c=>!IsClassicChampion(c.Alias)).ToList() };
    public static RepositoryRevision WithoutClassic(RepositoryRevision revision) => revision with { Files=revision.Files.Where(f=>!IsClassicRepositoryPath(f.Path)).ToList() };
    public static LibraryManifest WithoutClassic(LibraryManifest manifest) => manifest with
    {
        Packages=manifest.Packages.Where(p=>!IsClassicRepositoryPath(p.Path)).ToList(),
        Files=manifest.Files.Where(f=>!IsClassicRepositoryPath(f.Path)).ToList()
    };
}
