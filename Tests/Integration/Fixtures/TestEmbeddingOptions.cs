using Infrastructure.Memory;

namespace Tests.Integration.Fixtures;

public static class TestEmbeddingOptions
{
    public static EmbeddingOptions At(int dimension) => new()
    {
        BaseAddress = "http://embeddings.invalid/v1/",
        Model = "test-embedding-model",
        Dimension = dimension
    };
}