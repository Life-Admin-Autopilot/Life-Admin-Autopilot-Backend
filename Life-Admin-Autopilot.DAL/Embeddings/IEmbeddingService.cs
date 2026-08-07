using Life_Admin_Autopilot.DAL.Common;

namespace Life_Admin_Autopilot.DAL.Embeddings
{
    // Turns text into the vector Copilot Chat searches by. The only thing in the codebase
    // that talks to an embedding provider.
    public interface IEmbeddingService
    {
        Task<Result<float[]>> EmbedAsync(string text, CancellationToken cancellationToken = default);

        // The model that produced the vectors, recorded alongside them. Vectors from two
        // models are not comparable, so a later change has to be detectable.
        string ModelId { get; }
    }
}
