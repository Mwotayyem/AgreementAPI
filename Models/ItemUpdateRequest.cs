

namespace AgreementAPI.Models
{
    public class ItemUpdateRequest
    {
        public string ITEMNO { get; set; } = string.Empty;
        public string BARCODE { get; set; } = string.Empty;
        public string ITEMSHORTNAME { get; set; } = string.Empty;
        public decimal ITEMTAX { get; set; }
        public decimal ITEMPRICE { get; set; }
        public int ITEMSTOP { get; set; }
        public int TRN_TYPE_PRICE { get; set; }
    }
}
