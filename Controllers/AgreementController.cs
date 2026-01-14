using Microsoft.AspNetCore.Mvc;
using AgreementAPI.Models;
using AgreementAPI.Repositories;

namespace AgreementAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AgreementController : ControllerBase
    {
        private readonly AgreementRepository _repository;
        private readonly ILogger<AgreementController> _logger;

        public AgreementController(AgreementRepository repository, ILogger<AgreementController> logger)
        {
            _repository = repository;
            _logger = logger;
        }

        /// <summary>
        /// Insert or update agreement with items
        /// </summary>
        /// <param name="request">Agreement data with items</param>
        /// <returns>API response with operation result</returns>
        [HttpPost("insert")]
        public async Task<ActionResult<ApiResponse<object>>> InsertAgreement([FromBody] AgreementRequest request)
        {
            try
            {
                // Validate request
                if (string.IsNullOrWhiteSpace(request.AGREEMENT_NO))
                {
                    return BadRequest(ApiResponse<object>.ErrorResponse("AGREEMENT_NO is required"));
                }

                if (request.ITEMS == null || request.ITEMS.Count == 0)
                {
                    return BadRequest(ApiResponse<object>.ErrorResponse("At least one item is required"));
                }

                if (string.IsNullOrWhiteSpace(request.COMP_CODE))
                {
                    return BadRequest(ApiResponse<object>.ErrorResponse("COMP_CODE is required"));
                }

                if (string.IsNullOrWhiteSpace(request.AGR_STDATE) || string.IsNullOrWhiteSpace(request.AGR_ENDATE))
                {
                    return BadRequest(ApiResponse<object>.ErrorResponse("AGR_STDATE and AGR_ENDATE are required"));
                }

                // Process agreement
                var (success, message, errors) = await _repository.InsertOrUpdateAgreement(request);

                if (success)
                {
                    _logger.LogInformation("Agreement {AgreementNo} processed successfully", request.AGREEMENT_NO);
                    return Ok(ApiResponse<object>.SuccessResponse(new { agreementNo = request.AGREEMENT_NO, itemCount = request.ITEMS.Count }, message));
                }
                else
                {
                    _logger.LogError("Failed to process agreement {AgreementNo}: {Message}", request.AGREEMENT_NO, message);
                    return BadRequest(ApiResponse<object>.ErrorResponse(message, errors));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing agreement");
                return StatusCode(500, ApiResponse<object>.ErrorResponse("Internal server error", new List<string> { ex.Message }));
            }
        }

        /// <summary>
        /// Update item(s) by ITEMNO
        /// Accepts either a single object or a list of objects
        /// </summary>
        /// <param name="requestBody">Item update data (Single object or List)</param>
        /// <returns>API response with operation result</returns>
        [HttpPut("update-item")]
        public async Task<ActionResult<ApiResponse<object>>> UpdateItem([FromBody] object requestBody)
        {
            try
            {
                List<ItemUpdateRequest> requests;

                // Deserialize based on type (List or Single)
                if (requestBody is System.Text.Json.JsonElement jsonElement && jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    requests = System.Text.Json.JsonSerializer.Deserialize<List<ItemUpdateRequest>>(jsonElement.GetRawText(), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                else
                {
                    var singleRequest = System.Text.Json.JsonSerializer.Deserialize<ItemUpdateRequest>(System.Text.Json.JsonSerializer.Serialize(requestBody), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    requests = new List<ItemUpdateRequest> { singleRequest };
                }

                if (requests == null || requests.Count == 0)
                {
                    return BadRequest(ApiResponse<object>.ErrorResponse("Invalid request body"));
                }

                // Validate all requests
                foreach (var req in requests)
                {
                    if (string.IsNullOrWhiteSpace(req.ITEMNO))
                        return BadRequest(ApiResponse<object>.ErrorResponse("ITEMNO is required for all items"));
                    
                    if (string.IsNullOrWhiteSpace(req.BARCODE))
                        return BadRequest(ApiResponse<object>.ErrorResponse($"BARCODE is required for item {req.ITEMNO}"));
                }

                // Update items
                var (success, message, errors) = await _repository.UpdateItems(requests);

                if (success || errors.Count > 0) // Return OK even if partial errors (handled in message/errors list)
                {
                    var responseData = requests.Count == 1 
                        ? (object)new { itemNo = requests[0].ITEMNO } 
                        : new { updatedCount = requests.Count, items = requests.Select(r => r.ITEMNO).ToList() };

                    if (!success && errors.Count > 0) // If complete failure
                    {
                         return BadRequest(ApiResponse<object>.ErrorResponse(message, errors));
                    }

                    return Ok(ApiResponse<object>.SuccessResponse(responseData, message, errors));
                }
                else
                {
                    return BadRequest(ApiResponse<object>.ErrorResponse(message, errors));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating item");
                return StatusCode(500, ApiResponse<object>.ErrorResponse("Internal server error", new List<string> { ex.Message }));
            }
        }
    }
}
