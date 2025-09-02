using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace User.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DebugController : ControllerBase
    {
        private readonly ILogger<DebugController> _logger;

        public DebugController(ILogger<DebugController> logger)
        {
            _logger = logger;
        }

        [HttpGet("auth-test")]
        [Authorize]
        public IActionResult TestAuth()
        {
            var claims = new List<object>();
            
            _logger.LogInformation("🔍 DEBUG: Testing REST API authentication");
            _logger.LogInformation($"🔍 User.Identity.IsAuthenticated: {User?.Identity?.IsAuthenticated}");
            _logger.LogInformation($"🔍 User.Identity.Name: {User?.Identity?.Name}");
            _logger.LogInformation($"🔍 User.Identity.AuthenticationType: {User?.Identity?.AuthenticationType}");
            
            if (User?.Claims != null)
            {
                foreach (var claim in User.Claims)
                {
                    _logger.LogInformation($"🔍 Claim: {claim.Type} = {claim.Value}");
                    claims.Add(new { Type = claim.Type, Value = claim.Value });
                }
            }

            // Try to get the user ID from the NameIdentifier claim
            var userIdClaim = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            _logger.LogInformation($"🔍 NameIdentifier claim: {userIdClaim}");

            return Ok(new
            {
                IsAuthenticated = User?.Identity?.IsAuthenticated,
                Name = User?.Identity?.Name,
                AuthenticationType = User?.Identity?.AuthenticationType,
                UserIdFromClaim = userIdClaim,
                Claims = claims
            });
        }

        [HttpGet("public-test")]
        public IActionResult TestPublic()
        {
            return Ok(new { Message = "Public endpoint working" });
        }
    }
}
