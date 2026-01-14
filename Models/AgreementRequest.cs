

namespace AgreementAPI.Models
{
    public class AgreementRequest
    {
        public string AGREEMENT_NO { get; set; } = string.Empty;
        public List<ItemDto> ITEMS { get; set; } = new List<ItemDto>();
        public string COMP_CODE { get; set; } = string.Empty;
        public string AGR_STDATE { get; set; } = string.Empty; // Format: dd/MM/yyyy
        public string AGR_ENDATE { get; set; } = string.Empty; // Format: dd/MM/yyyy
    }
}
 