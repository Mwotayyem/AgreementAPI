using Oracle.ManagedDataAccess.Client;
using AgreementAPI.Models;
using System.Globalization;

namespace AgreementAPI.Repositories
{
    public class AgreementRepository
    {
        private readonly string _connectionString;

        public AgreementRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("OracleConnection") ?? throw new ArgumentNullException("Connection string not found");
        }

        public async Task<(bool success, string message, List<string> errors)> InsertOrUpdateAgreement(AgreementRequest request)
        {
            var errors = new List<string>();
            var messages = new List<string>();
            
            using var connection = new OracleConnection(_connectionString);
            await connection.OpenAsync();

            using var transaction = connection.BeginTransaction();
            try
            {
                // DEBUG: Check Total Rows in Agreement Table
                string countSql = "SELECT COUNT(*) FROM COMMDIV.MCEPOS_AGREEMENT";
                using var countCmd = new OracleCommand(countSql, connection);
                countCmd.Transaction = transaction;
                var totalRows = await countCmd.ExecuteScalarAsync();
                Console.WriteLine($"DEBUG: Total Rows in MCEPOS_AGREEMENT: {totalRows}");

                // Parse dates
                string[] dateFormats = { "dd/MM/yyyy", "d/M/yyyy", "dd/M/yyyy", "d/MM/yyyy", "dd-MM-yyyy", "d-M-yyyy", "yyyy-MM-dd" };
                DateTime startDate = DateTime.ParseExact(request.AGR_STDATE, dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None);
                DateTime endDate = DateTime.ParseExact(request.AGR_ENDATE, dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None);

                // Get or create AGR_SERAIL (Agreement internal ID)
                int agrSerial = await GetOrCreateAgreementSerial(connection, transaction, request.AGREEMENT_NO);

                int insertedItemsCount = 0;
                int insertedAgreementsCount = 0;

                foreach (var item in request.ITEMS)
                {
                    // 1. Process Item History
                    // Check if item exists and if it has changed
                    var (shouldInsertItem, trnTypeId, lastItemState) = await CheckItemShouldInsert(connection, transaction, item);

                    int trnSerial = 0;

                    if (shouldInsertItem)
                    {
                        // Get Next TRN_SERIAL
                        trnSerial = await GetNextTrnSerial(connection, transaction);

                        // Insert into MCEPOS_ITEMS with History (Old/New values)
                        await InsertItemHistory(connection, transaction, trnSerial, trnTypeId, item, lastItemState);
                        
                        insertedItemsCount++;
                        messages.Add($"✓ تم إدراج سجل حركة للمادة {item.ITEMNO} (نوع: {trnTypeId})");
                    }
                    else
                    {
                        messages.Add($"ℹ️ المادة {item.ITEMNO}: لم يتم اكتشاف أي تغيير في البيانات (موجودة مسبقاً).");
                        
                        // Even if we don't insert a new item record (no change), we might still need a TRN_SERIAL if we are inserting into Agreement?
                        // If Agreement matches EXACTLY, we do nothing.
                        // If Agreement differs, we insert into Agreement. What TRN_SERIAL to use?
                        // If no item change, maybe we need the LATEST TRN_SERIAL of the item?
                        // Or does Agreement get its own Serial? 
                        // Schema: MCEPOS_AGREEMENT has TRN_SERAIL. 
                        // SAFEST: Generate a new TRN_SERIAL for the Agreement Action if we are inserting into Agreement.
                    }

                    // 2. Process Agreement Link
                    // Check if this specific configuration (Agreement + Item + Dates + Etc) exists
                    bool agreementExists = await CheckAgreementMatch(connection, transaction, request, item, startDate, endDate);

                    if (!agreementExists)
                    {
                        // If we didn't generate a serial for item insert, generate one now.
                        if (trnSerial == 0)
                        {
                             trnSerial = await GetNextTrnSerial(connection, transaction);
                        }

                        // Insert into MCEPOS_AGREEMENT
                        await InsertAgreementRow(connection, transaction, trnSerial, agrSerial, request, item, startDate, endDate);
                        
                        insertedAgreementsCount++;
                        messages.Add($"✓ تم ربط المادة {item.ITEMNO} بالاتفاقية {request.AGREEMENT_NO}");
                    }
                    else
                    {
                         messages.Add($"ℹ️ المادة {item.ITEMNO}: موجودة بالفعل في الاتفاقية {request.AGREEMENT_NO} بنفس التفاصيل.");
                    }
                }

                await transaction.CommitAsync();
                
                string finalMessage = $"تمت العملية: {insertedItemsCount} حركات مواد، {insertedAgreementsCount} سجلات اتفاقية";
                if (insertedItemsCount == 0 && insertedAgreementsCount == 0)
                    finalMessage = "لم يتم إجراء أي تغييرات (البيانات مطابقة للموجود)";

                messages.Insert(0, $"✅ {finalMessage}");
                
                return (true, string.Join("\n", messages), errors);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                errors.Add(ex.Message);
                messages.Add($"❌ فشلت العملية: {ex.Message}");
                return (false, string.Join("\n", messages), errors);
            }
        }

        // Removed UpdateItems method as requested (Only Inserts allowed)

        private async Task<(bool ShouldInsert, int TrnTypeId, ItemHistoryState? LastState)> CheckItemShouldInsert(
            OracleConnection connection, OracleTransaction transaction, ItemDto newItem)
        {
            // Get Latest Item Record
            string sql = @"
                SELECT TRN_ITEMBARCODE, TRN_ITEMPRICE, TRN_ITEMSHORTNAME, TRN_ITEMSTOP, TRN_SERIAL
                FROM COMMDIV.MCEPOS_ITEMS 
                WHERE TRN_ITEMCODE = :itemNo 
                ORDER BY TRN_SERIAL DESC 
                FETCH FIRST 1 ROW ONLY";

            using var cmd = new OracleCommand(sql, connection);
            cmd.Transaction = transaction;
            cmd.BindByName = true;
            cmd.Parameters.Add(":itemNo", OracleDbType.Varchar2).Value = newItem.ITEMNO;

            using var reader = await cmd.ExecuteReaderAsync();
            
            if (await reader.ReadAsync())
            {
                var trnSerial = reader.IsDBNull(4) ? 0 : reader.GetInt32(4); // Need to fetch TRN_SERIAL to log it
                var lastState = new ItemHistoryState
                {
                    Barcode = reader.IsDBNull(0) ? null : reader.GetString(0),
                    Price = reader.IsDBNull(1) ? 0 : reader.GetDecimal(1),
                    Name = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Stop = reader.IsDBNull(3) ? 0 : reader.GetInt32(3)
                };

                // Add log to message list? Not easily accessible here.
                // We will rely on the return values affecting the main messages.
                // But to help DEBUG, let's include this info in the Check results? 
                // Or just assume the Logic is correct and the user has data. 
                // Let's modify the SQL to fetch TRN_SERIAL to be sure.
                Console.WriteLine($"Found existing item record: Serial {trnSerial}, Price {lastState.Price}");

                // Compare logic
                if (newItem.ITEMSTOP == 1) return (true, 5, lastState); // STOP
                if (lastState.Barcode != newItem.BARCODE) return (true, 2, lastState); // BARCODE CHANGE
                
                // Debugging Price Change
                if (lastState.Price != newItem.ITEMPRICE) 
                {
                     // Found price change
                     return (true, 3, lastState); 
                }
                
                if (lastState.Name != newItem.ITEMSHORTNAME) return (true, 4, lastState); // NAME CHANGE
                
                // If identical, do NOT insert
                return (false, 0, lastState);
            }

            // No record found -> First Insert
            Console.WriteLine("No existing item record found");
            return (true, 1, null);
        }

        private async Task InsertItemHistory(OracleConnection connection, OracleTransaction transaction, 
            int trnSerial, int trnTypeId, ItemDto item, ItemHistoryState? lastState)
        {
            string sql = @"
                INSERT INTO COMMDIV.MCEPOS_ITEMS 
                (TRN_SERIAL, TRN_TYPEID, TRN_ITEMCODE, TRN_ITEMBARCODE, TRN_ITEMNAME, TRN_ITEMSHORTNAME, 
                 TRN_ITEMTAX, TRN_TYPE_PRICE, TRN_ITEMPRICE, TRN_ITEMSTOP,
                 TRN_OLDBARCODE, TRN_NEWBARCODE, 
                 TRN_OLDPRICE, TRN_NEWPRICE, 
                 TRN_OLDITEMNAME, TRN_NEWITEMNAME)
                VALUES 
                (:trnSerial, :trnTypeId, :itemNo, :barcode, :shortName, :shortName, 
                 :tax, :typePrice, :price, :stop,
                 :oldBarcode, :newBarcode,
                 :oldPrice, :newPrice,
                 :oldName, :newName)";

            using var cmd = new OracleCommand(sql, connection);
            cmd.BindByName = true;
            cmd.Transaction = transaction;
            
            cmd.Parameters.Add(":trnSerial", OracleDbType.Int32).Value = trnSerial;
            cmd.Parameters.Add(":trnTypeId", OracleDbType.Int32).Value = trnTypeId;
            cmd.Parameters.Add(":itemNo", OracleDbType.Varchar2).Value = item.ITEMNO;
            cmd.Parameters.Add(":barcode", OracleDbType.Varchar2).Value = item.BARCODE;
            cmd.Parameters.Add(":shortName", OracleDbType.Varchar2).Value = item.ITEMSHORTNAME;
            cmd.Parameters.Add(":tax", OracleDbType.Decimal).Value = item.ITEMTAX;
            cmd.Parameters.Add(":typePrice", OracleDbType.Int32).Value = item.TRN_TYPE_PRICE;
            cmd.Parameters.Add(":price", OracleDbType.Decimal).Value = item.ITEMPRICE;
            cmd.Parameters.Add(":stop", OracleDbType.Int32).Value = item.ITEMSTOP;

            // History Columns
            // Explicitly force NULL for Type 1 (New Item)
            bool isNewItem = trnTypeId == 1;
            bool isUpdate = lastState != null && !isNewItem;

            // Barcode Change
            if (isUpdate && lastState?.Barcode != item.BARCODE)
            {
                cmd.Parameters.Add(":oldBarcode", OracleDbType.Varchar2).Value = lastState?.Barcode;
                cmd.Parameters.Add(":newBarcode", OracleDbType.Varchar2).Value = item.BARCODE;
            }
            else
            {
                cmd.Parameters.Add(":oldBarcode", OracleDbType.Varchar2).Value = DBNull.Value;
                cmd.Parameters.Add(":newBarcode", OracleDbType.Varchar2).Value = DBNull.Value;
            }

            // Price Change
            if (isUpdate && lastState?.Price != item.ITEMPRICE)
            {
                cmd.Parameters.Add(":oldPrice", OracleDbType.Decimal).Value = lastState?.Price;
                cmd.Parameters.Add(":newPrice", OracleDbType.Decimal).Value = item.ITEMPRICE;
            }
            else
            {
                cmd.Parameters.Add(":oldPrice", OracleDbType.Decimal).Value = DBNull.Value;
                cmd.Parameters.Add(":newPrice", OracleDbType.Decimal).Value = DBNull.Value;
            }

            // Name Change
            if (isUpdate && lastState?.Name != item.ITEMSHORTNAME)
            {
                 cmd.Parameters.Add(":oldName", OracleDbType.Varchar2).Value = lastState?.Name;
                 cmd.Parameters.Add(":newName", OracleDbType.Varchar2).Value = item.ITEMSHORTNAME;
            }
            else
            {
                 cmd.Parameters.Add(":oldName", OracleDbType.Varchar2).Value = DBNull.Value;
                 cmd.Parameters.Add(":newName", OracleDbType.Varchar2).Value = DBNull.Value;
            }

            await cmd.ExecuteNonQueryAsync();
        }

        private async Task<bool> CheckAgreementMatch(OracleConnection connection, OracleTransaction transaction, 
            AgreementRequest request, ItemDto item, DateTime startDate, DateTime endDate)
        {
            // Check if there is an existing row with EXACT matching data for this item in this agreement
            string sql = @"
                SELECT COUNT(*) 
                FROM COMMDIV.MCEPOS_AGREEMENT 
                WHERE AGR_AGREEMENT_NO = :agreementNo 
                AND AGR_ITEMNO = :itemNo
                AND AGR_BARCODE = :barcode
                AND AGR_COMP_CODE = :compCode
                AND AGR_STDATE = :startDate
                AND AGR_ENDATE = :endDate
                AND STATUS = 1"; // Assuming status 1 is active/valid

            using var cmd = new OracleCommand(sql, connection);
            cmd.BindByName = true;
            cmd.Transaction = transaction;
            
            cmd.Parameters.Add(":agreementNo", OracleDbType.Varchar2).Value = request.AGREEMENT_NO;
            cmd.Parameters.Add(":itemNo", OracleDbType.Varchar2).Value = item.ITEMNO;
            cmd.Parameters.Add(":barcode", OracleDbType.Varchar2).Value = item.BARCODE;
            cmd.Parameters.Add(":compCode", OracleDbType.Varchar2).Value = request.COMP_CODE;
            cmd.Parameters.Add(":startDate", OracleDbType.Date).Value = startDate;
            cmd.Parameters.Add(":endDate", OracleDbType.Date).Value = endDate;

            var result = await cmd.ExecuteScalarAsync();
            var count = Convert.ToInt32(result);
            Console.WriteLine($"DEBUG: CheckAgreementMatch Agreement={request.AGREEMENT_NO} Item={item.ITEMNO} Count={count}");
            
            return count > 0;
        }

        private async Task InsertAgreementRow(OracleConnection connection, OracleTransaction transaction, 
            int trnSerial, int agrSerial, AgreementRequest request, ItemDto item, DateTime startDate, DateTime endDate)
        {
            string sql = @"
                INSERT INTO COMMDIV.MCEPOS_AGREEMENT 
                (TRN_SERAIL, TRN_TYPE, AGR_SERAIL, AGR_AGREEMENT_NO, AGR_ITEMNO, AGR_BARCODE, 
                 AGR_COMP_CODE, AGR_TYPE_CODE, AGR_STDATE, AGR_ENDATE, STATUS)
                VALUES 
                (:trnSerial, :trnType, :agrSerial, :agreementNo, :itemNo, :barcode, 
                 :compCode, :typeCode, :startDate, :endDate, :status)";

            using var cmd = new OracleCommand(sql, connection);
            cmd.BindByName = true;
            cmd.Transaction = transaction;
            
            cmd.Parameters.Add(":trnSerial", OracleDbType.Int32).Value = trnSerial;
            cmd.Parameters.Add(":trnType", OracleDbType.Int32).Value = 8; // 8 = INSERT AGREEMENT333
            cmd.Parameters.Add(":agrSerial", OracleDbType.Int32).Value = agrSerial;
            cmd.Parameters.Add(":agreementNo", OracleDbType.Varchar2).Value = request.AGREEMENT_NO;
            cmd.Parameters.Add(":itemNo", OracleDbType.Varchar2).Value = item.ITEMNO;
            cmd.Parameters.Add(":barcode", OracleDbType.Varchar2).Value = item.BARCODE;
            cmd.Parameters.Add(":compCode", OracleDbType.Varchar2).Value = request.COMP_CODE;
            cmd.Parameters.Add(":typeCode", OracleDbType.Int32).Value = 5; 
            cmd.Parameters.Add(":startDate", OracleDbType.Date).Value = startDate;
            cmd.Parameters.Add(":endDate", OracleDbType.Date).Value = endDate;
            cmd.Parameters.Add(":status", OracleDbType.Int32).Value = 1; 

            await cmd.ExecuteNonQueryAsync();
        }

        private async Task<int> GetNextTrnSerial(OracleConnection connection, OracleTransaction transaction)
        {
            string sql = "SELECT NVL(MAX(TRN_SERIAL), 0) + 1 FROM COMMDIV.MCEPOS_ITEMS";
            using var cmd = new OracleCommand(sql, connection);
            cmd.Transaction = transaction;
            
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }

        private async Task<int> GetOrCreateAgreementSerial(OracleConnection connection, OracleTransaction transaction, string agreementNo)
        {
            string checkSql = "SELECT AGR_SERAIL FROM COMMDIV.MCEPOS_AGREEMENT WHERE AGR_AGREEMENT_NO = :agreementNo AND ROWNUM = 1";
            using var checkCmd = new OracleCommand(checkSql, connection);
            checkCmd.BindByName = true;
            checkCmd.Transaction = transaction;
            checkCmd.Parameters.Add(":agreementNo", OracleDbType.Varchar2).Value = agreementNo;

            var result = await checkCmd.ExecuteScalarAsync();
            if (result != null && result != DBNull.Value) return Convert.ToInt32(result);

            string maxSql = "SELECT NVL(MAX(AGR_SERAIL), 0) + 1 FROM COMMDIV.MCEPOS_AGREEMENT";
            using var maxCmd = new OracleCommand(maxSql, connection);
            maxCmd.Transaction = transaction;
            return Convert.ToInt32(await maxCmd.ExecuteScalarAsync());
        }

        public async Task<(bool success, string message, List<string> errors)> UpdateItems(List<ItemUpdateRequest> requests)
        {
             // This existing method is kept for Controller compatibility but throws error or redirects to logical flow?
             // Since User said "No Updates", we should practically disable this or make it use the Insert Logic.
             // For now, I'll return an error or basic success message without action to prevent misuse if called.
             return await Task.FromResult((false, "Updates are disabled. Please use Insert endpoint for all history changes.", new List<string>()));
        }

        private class ItemHistoryState
        {
            public string? Barcode { get; set; }
            public decimal Price { get; set; }
            public string? Name { get; set; }
            public int Stop { get; set; }
        }
    }
}
