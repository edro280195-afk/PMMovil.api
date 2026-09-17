using System.Data;

using Microsoft.Extensions.Configuration;

using MySqlConnector;

namespace PMMovil.Api.Data;

public class BaseRepository
{
    private readonly IConfiguration _configuration;

    private readonly string _reportesConn;

    private readonly string _pmmtrafConn;

    private readonly string _pmmtoolsConn;

    private readonly string _pmmutileriaConn;

    public BaseRepository(IConfiguration configuration)
    {
        _configuration = configuration;
        _reportesConn = _configuration.GetConnectionString("ReportesDb") ?? "";
        _pmmtrafConn = _configuration.GetConnectionString("PmmtrafDb") ?? "";
        _pmmtoolsConn = _configuration.GetConnectionString("PmmtoolsDb") ?? _pmmtrafConn;
        _pmmutileriaConn = _configuration.GetConnectionString("PmmutileriaDb")
            ?? GetDatabaseConnection(_pmmtrafConn, "pmmutileria");
    }

    public IDbConnection CreateReportesConnection()
    {
        return new MySqlConnection(_reportesConn);
    }

    public IDbConnection CreateConnection()
    {
        return new MySqlConnection(_pmmtrafConn);
    }

    public IDbConnection CreatePmmtoolsConnection()
    {
        return new MySqlConnection(_pmmtoolsConn);
    }

    public IDbConnection CreatePmmutileriaConnection()
    {
        return new MySqlConnection(_pmmutileriaConn);
    }

    private static string GetDatabaseConnection(string connectionString, string database)
    {
        MySqlConnectionStringBuilder builder = new(connectionString)
        {
            Database = database
        };
        return builder.ConnectionString;
    }
}
