using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Domain.DTOs.FileSystem;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.FileSystem;

public class FsSchemaGoldenTests
{
    // Set FS_SCHEMA_UPDATE=1 to (re)write the committed schema files.
    private static readonly string SchemaDir =
        Path.Combine(FindRepoRoot(), "docs", "contracts", "fs");

    // GetJsonSchemaAsNode requires a TypeInfoResolver on the options instance.
    // We derive a schema-only copy from FsResultContract.SerializerOptions so
    // that camelCase naming policy is preserved in the generated schema.
    private static readonly JsonSerializerOptions SchemaOptions =
        new(FsResultContract.SerializerOptions)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };

    public static TheoryData<string, Type> Schemas()
    {
        var data = new TheoryData<string, Type>();
        foreach (var kvp in FsResultContract.ResultTypes)
        {
            data.Add(kvp.Key, kvp.Value);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Schemas))]
    public void CommittedSchema_MatchesGeneratedFromDto(string toolName, Type dtoType)
    {
        var generated = SchemaOptions
            .GetJsonSchemaAsNode(dtoType)
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        var file = Path.Combine(SchemaDir, $"{toolName}.schema.json");

        if (Environment.GetEnvironmentVariable("FS_SCHEMA_UPDATE") == "1")
        {
            Directory.CreateDirectory(SchemaDir);
            File.WriteAllText(file, generated);
            return;
        }

        File.Exists(file).ShouldBeTrue($"Missing schema {file}. Run with FS_SCHEMA_UPDATE=1 to generate.");
        var committed = File.ReadAllText(file);
        Normalize(committed).ShouldBe(Normalize(generated),
            $"Schema for {toolName} drifted from {dtoType.Name}. Run with FS_SCHEMA_UPDATE=1 to regenerate.");
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.GetFiles("*.sln").Any())
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repo root (.sln) not found");
    }
}