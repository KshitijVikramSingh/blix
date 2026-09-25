using Blix.Cooked;

namespace Blix.Test.ProjectRecipes;

/// <summary>A foreign recipe used to prove discovery across an assembly boundary.</summary>
public static class FixtureRecipe
{
    [Recipe("tst1",
        Produces = ".blixfixture",
        Consumes = ".fixture",
        Summary = "the external source-consumer recipe fixture")]
    public static CookOutcome Cook(CookRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parent = Path.GetDirectoryName(request.OutputPath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        File.WriteAllText(request.OutputPath, "cooked:" + File.ReadAllText(request.SourcePath));
        return CookOutcome.Written("fixture");
    }
}
