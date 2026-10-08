namespace CIPAdmin.Backend.Services;

/// <summary>
/// The two employee-scoped rules the whole application agrees on, defined once.
///
/// Before this existed the same literals were copy-pasted across five files, and
/// they had already drifted: the Dashboard still counted "active" from the ACTIVE
/// bit while every other screen had moved on. Anything that lists or counts
/// employees must use these fragments rather than writing its own.
/// </summary>
public static class EmployeeFilters
{
    /// <summary>CONNECTOR role. Present in master data, excluded from the application.</summary>
    public const int ConnectorRoleId = 2010;

    /// <summary>CONNECTOR designation. Excluded too: some connector rows carry the role
    /// and others only the designation, so filtering on one of the two leaks rows.</summary>
    public const int ConnectorDesigId = 1014;

    /// <summary>
    /// "Active" means still working, which is <c>LAST_WORKING_DATE IS NULL</c>.
    /// The ACTIVE bit is NOT the source of truth: it is still set on 3,249 rows that
    /// already carry a last working date, so it over-reports leavers.
    /// The column is varchar(50) rather than a date type, so it is compared for NULL
    /// only - no date parsing, and no reliance on a particular text format.
    /// </summary>
    public const string LastWorkingDateColumn = "LAST_WORKING_DATE";

    /// <summary>
    /// Converts the stored <c>LAST_WORKING_DATE</c> text to a DATE, tolerating the
    /// three formats actually present in the data. Used ONLY for sorting: the stored
    /// values are inconsistent enough (3,317 of 3,335 parse as MM/dd/yyyy, the rest
    /// are 2-digit years or a spelled-out form) that ordering the raw text would put
    /// '01/10/2024' before '02/11/2020'. Anything that must be interpreted rather than
    /// compared should not rely on this.
    /// </summary>
    public static string LastWorkingDateSortKey(string alias = "")
    {
        var col = Qualify(alias, LastWorkingDateColumn);
        return $"COALESCE(" +
               $"TRY_CONVERT(DATE, {col}, 101), " +
               $"TRY_CONVERT(DATE, {col}, 3), " +
               $"TRY_CONVERT(DATE, {col}, 107), " +
               $"TRY_CONVERT(DATE, {col}, 1))";
    }

    /// <summary>SQL predicate: employee is still on roll.</summary>
    public static string Active(string alias = "") =>
        Qualify(alias, LastWorkingDateColumn) + " IS NULL";

    /// <summary>SQL predicate: employee has left (the exact inverse of <see cref="Active"/>).</summary>
    public static string Inactive(string alias = "") =>
        Qualify(alias, LastWorkingDateColumn) + " IS NOT NULL";

    /// <summary>
    /// Remove values that are not active, non-deleted, non-connector employee codes.
    /// The codes are converted to positive integers before being embedded in this
    /// SQL Server 2014-compatible IN list; empty inputs avoid a database round trip.
    /// </summary>
    public static async Task<HashSet<int>> FilterActiveCodesAsync(IDbContext db, IEnumerable<int> employeeCodes)
    {
        var codes = employeeCodes.Where(code => code > 0).Distinct().ToArray();
        if (codes.Length == 0) return new HashSet<int>();

        // Keep each IN list comfortably below SQL Server's expression ceiling; bulk
        // application uploads can contain up to 15,000 assignment codes in one request.
        const int batchSize = 1_000;
        var eligible = new HashSet<int>();
        foreach (var batch in codes.Chunk(batchSize))
        {
            var dt = await db.QueryAsync($@"
                SELECT EMP_CODE
                FROM dbo.TBL_CIP_EMPLOYEE_MASTER e
                WHERE e.EMP_CODE IN ({string.Join(",", batch)})
                  AND {Active("e")} AND {NotDeleted("e")} AND {NotConnector("e")}");
            foreach (System.Data.DataRow row in dt.Rows)
                eligible.Add(Convert.ToInt32(row["EMP_CODE"]));
        }
        return eligible;
    }

    /// <summary>SQL predicate: employee is not a CONNECTOR, by role OR designation.</summary>
    public static string NotConnector(string alias = "") =>
        $"ISNULL({Qualify(alias, "ROLE_ID")}, 0) <> {ConnectorRoleId} " +
        $"AND ISNULL({Qualify(alias, "DESIG_ID")}, 0) <> {ConnectorDesigId}";

    /// <summary>Soft-deleted rows never appear in a listing.</summary>
    public static string NotDeleted(string alias = "") =>
        $"ISNULL({Qualify(alias, "IS_DELETED")}, 0) = 0";

    /// <summary>The common baseline every employee listing starts from: on roll,
    /// not deleted, not a connector.</summary>
    public static string Base(string alias = "") =>
        $" WHERE {Active(alias)} AND {NotDeleted(alias)} AND {NotConnector(alias)}";

    private static string Qualify(string alias, string column) =>
        string.IsNullOrWhiteSpace(alias) ? column : alias + "." + column;
}