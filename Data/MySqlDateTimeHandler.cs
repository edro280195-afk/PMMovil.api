using System.Data;

using Dapper;

using MySqlConnector;

namespace PMMovil.Api.Data;

public class MySqlDateTimeHandler : SqlMapper.TypeHandler<DateTime?>
{
    public override void SetValue(IDbDataParameter parameter, DateTime? value)
    {
        parameter.Value = value.HasValue ? (object)value.Value : DBNull.Value;
    }

    public override DateTime? Parse(object value)
    {
        if (value is null or DBNull) return null;

        if (value is DateTime dt)
            return dt == DateTime.MinValue ? null : dt;

        if (value is MySqlDateTime mdt)
            return mdt.IsValidDateTime ? mdt.GetDateTime() : null;

        if (DateTime.TryParse(value.ToString(), out var parsed))
            return parsed == DateTime.MinValue ? null : parsed;

        return null;
    }
}
