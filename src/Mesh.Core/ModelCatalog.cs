namespace Mesh.Core;

using Mesh.Abstractions;

/// <summary>
/// Representative catalog used for planning and the UI. Layer counts are
/// approximations; replace with the live Foundry Local catalog when deployed.
/// Quantized (Q4) variants are the right default for a 16 GB CPU node.
/// </summary>
public static class ModelCatalog
{
    public static readonly IReadOnlyList<ModelDescriptor> Models = new[]
    {
        New("phi-4-mini",      "Phi",       3.8,  4, 32, "phi-4-mini"),
        New("phi-4",           "Phi",      14.0,  4, 40, "phi-4"),
        New("mistral-7b",      "Mistral",   7.0,  4, 32, "mistral-7b", "open-mistral-7b"),
        New("llama-3.1-8b",    "Llama",     8.0,  4, 32, "llama-3.1-8b"),
        New("qwen2.5-7b",      "Qwen",      7.6,  4, 28, "qwen2.5-7b"),
        New("qwen2.5-14b",     "Qwen",     14.8,  4, 48, "qwen2.5-14b"),
        New("qwen2.5-32b",     "Qwen",     32.8,  4, 64, "qwen2.5-32b"),
        New("llama-3.1-70b",   "Llama",    70.0,  4, 80, "llama-3.1-70b"),
    };

    public static ModelDescriptor? Find(string idOrAlias) =>
        Models.FirstOrDefault(m =>
            string.Equals(m.Id, idOrAlias, StringComparison.OrdinalIgnoreCase) ||
            m.Aliases.Any(a => string.Equals(a, idOrAlias, StringComparison.OrdinalIgnoreCase)));

    private static ModelDescriptor New(string id, string family, double paramsB, int quantBits, int layers, params string[] aliases) =>
        new(id, family, paramsB, quantBits, layers, aliases);
}
