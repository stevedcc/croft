using Croft.Web.Tests.Fixtures;
using AwesomeAssertions;

namespace Croft.Web.Tests;

/// <summary>
/// A test class that needs a container runtime (it takes the shared Postgres Testcontainer
/// through <see cref="PostgresDatabaseFixture"/>) carries <c>[Trait("Category", "Container")]</c>,
/// so a contributor with no Docker or Podman can run the rest with
/// <c>--filter "Category!=Container"</c> (README, "Running the other tests"). The default run
/// still includes them: the project has no default filter.
/// </summary>
public class ContainerTestsAreLabelledTests
{
    [Fact]
    public void Classes_using_the_postgres_fixture_carry_the_container_category()
    {
        var unlabelled = typeof(ContainerTestsAreLabelledTests).Assembly.GetTypes()
            .Where(UsesThePostgresFixture)
            .Where(type => !HasContainerCategory(type))
            .Select(type => type.Name)
            .Order()
            .ToList();

        unlabelled.Should().BeEmpty(
            "every class that needs the Postgres container must carry [Trait(\"Category\", \"Container\")]");
    }

    // SharedPostgresContainer is a static class: it cannot be a fixture type or a constructor
    // parameter, so a class can take the container directly (SharedPostgresContainer.AcquireAsync())
    // without any fixture reflection can see. Any test source file outside Fixtures/ that names it
    // therefore has every class declared in it checked for the label.
    [Fact]
    public void Classes_in_files_using_the_shared_container_directly_carry_the_container_category()
    {
        var testsDirectory = Path.Combine(RepoRoot.Find(), "tests", "Croft.Web.Tests");
        var typesByName = typeof(ContainerTestsAreLabelledTests).Assembly.GetTypes()
            .Where(type => type.DeclaringType is null)
            .ToLookup(type => type.Name);
        var unlabelled = new List<string>();

        foreach (var path in Directory.EnumerateFiles(testsDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(testsDirectory, path);
            if (relative.StartsWith("Fixtures" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")
                || Path.GetFileName(path) == nameof(ContainerTestsAreLabelledTests) + ".cs")
            {
                continue;
            }

            var source = File.ReadAllText(path);
            if (!source.Contains(nameof(SharedPostgresContainer), StringComparison.Ordinal))
            {
                continue;
            }

            foreach (System.Text.RegularExpressions.Match match in
                System.Text.RegularExpressions.Regex.Matches(source, @"^(?:public |internal |sealed |static |abstract )*class (\w+)", System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                foreach (var type in typesByName[match.Groups[1].Value].Where(type => !HasContainerCategory(type)))
                {
                    unlabelled.Add(type.Name);
                }
            }
        }

        unlabelled.Order().Should().BeEmpty(
            "every class in a file that takes SharedPostgresContainer must carry [Trait(\"Category\", \"Container\")]");
    }

    [Fact]
    public void The_postgres_fixture_is_in_use_so_the_check_above_is_not_vacuous()
    {
        typeof(ContainerTestsAreLabelledTests).Assembly.GetTypes()
            .Count(type => type.GetInterfaces().Contains(typeof(IClassFixture<PostgresDatabaseFixture>)))
            .Should().BeGreaterThan(5);
    }

    private static bool UsesThePostgresFixture(Type type) =>
        type.GetInterfaces().Any(i =>
            i.IsGenericType
            && i.GetGenericTypeDefinition() is var definition
            && (definition == typeof(IClassFixture<>) || definition == typeof(ICollectionFixture<>))
            && i.GetGenericArguments()[0] == typeof(PostgresDatabaseFixture))
        || type.GetConstructors().Any(constructor =>
            constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(PostgresDatabaseFixture)));

    private static bool HasContainerCategory(Type type) =>
        type.GetCustomAttributesData().Any(data =>
            data.AttributeType == typeof(TraitAttribute)
            && data.ConstructorArguments.Count == 2
            && data.ConstructorArguments[0].Value as string == "Category"
            && data.ConstructorArguments[1].Value as string == "Container");
}
