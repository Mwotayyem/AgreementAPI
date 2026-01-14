namespace AgreementAPI.Models
{
    /// <summary>
    /// Transaction Type IDs based on MCEPOS system
    /// </summary>
    public enum TransactionType
    {
        INSERT_ITEM = 1,
        UPDATE_BARCODE = 2,
        UPDATE_PRICE = 3,
        UPDATE_ITEM_NAME = 4,
        STOP_ITEM = 5,
        INSERT_OFFER = 6,
        UPDATE_OFFER = 7,
        INSERT_AGREEMENT = 8,
        UPDATE_AGREEMENT = 9
    }
}
