using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.DAL.Entities;
using Life_Admin_Autopilot.DAL.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Life_Admin_Autopilot_Backend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ContentChunksTestController : ControllerBase
    {
        private readonly IContentChunksRepository _contentChunksRepository;
        private readonly IEmbeddingProvider _huggingFace;

        public ContentChunksTestController(IContentChunksRepository contentChunksRepository, IEmbeddingProvider huggingFace)
        {
            _contentChunksRepository = contentChunksRepository;
            _huggingFace = huggingFace;
        }

        [HttpPost("create-chunk")]
        public async Task<IActionResult> CreateChunk([FromBody] ContentChunkCreateDto request)
        {
            if (string.IsNullOrWhiteSpace(request.Text))
            {
                return BadRequest(new { Message = "Text field is required to generate embeddings." });
            }

            // 1. Get the embedding vector from your Hugging Face model endpoint
            float[] vectorArray = await _huggingFace.GenerateEmbeddingAsync(request.Text);

            if (vectorArray.Length == 0)
            {
                return StatusCode(500, new { Message = "Failed to retrieve embedding vector from Hugging Face." });
            }

            // 2. Build the complete row combining metadata + text + the generated vector
            var chunkEntity = new ContentChunks
            {
                UserId = request.UserId,
                SourceId = request.SourceId,
                SourceType = request.SourceType,
                Text = request.Text,
                Embedding = vectorArray
            };

            // 3. Save the full row directly into MongoDB Atlas
            await _contentChunksRepository.CreateAsync(chunkEntity);

            return Ok(new
            {
                Message = "Content chunk successfully embedded via Hugging Face and stored in MongoDB!",
                ChunkId = chunkEntity.Id
            });
        }
    }

    public class ContentChunkCreateDto
    {
        public string UserId { get; set; }
        public string SourceId { get; set; }
        public ChunkSourceType SourceType { get; set; }
        public string Text { get; set; }
    }
}
