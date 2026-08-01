using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Life_Admin_Autopilot_Backend.Extensions
{
    // Declaring the scheme in components (see BearerSecuritySchemeTransformer) is only half
    // the job: Swagger UI attaches the token to an operation solely when that operation
    // declares it needs one. Without this the Authorize box accepts a token and then
    // silently sends nothing, so every secured endpoint answers 401.
    public class BearerSecurityRequirementTransformer : IOpenApiOperationTransformer
    {
        public Task TransformAsync(
            OpenApiOperation operation,
            OpenApiOperationTransformerContext context,
            CancellationToken cancellationToken)
        {
            var metadata = context.Description.ActionDescriptor.EndpointMetadata;

            // [AllowAnonymous] wins over [Authorize], so the auth endpoints are not marked
            // as requiring the very token they exist to issue.
            var requiresToken = metadata.OfType<IAuthorizeData>().Any()
                && !metadata.OfType<IAllowAnonymous>().Any();

            if (!requiresToken)
            {
                return Task.CompletedTask;
            }

            operation.Security ??= new List<OpenApiSecurityRequirement>();
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                // The host document has to be passed, otherwise the reference cannot
                // resolve the scheme and the requirement serialises as an empty object -
                // which Swagger UI reads as "no token needed" and the 401 comes back.
                [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = new List<string>()
            });

            return Task.CompletedTask;
        }
    }
}
