using System.Data;
using MySqlConnector;
using Microsoft.Extensions.Configuration;

namespace PMMovil.Api.Data
{
    /// <summary>
    /// Clase base que proporciona la conexión a la base de datos MySQL para usar con Dapper.
    /// Diseñada para ser inyectada en los controladores o servicios.
    /// 
    /// Porque se opto usar Dapper? Por su mayor rapidez en las consultas, su practica es sencilla
    /// la teoria puede ser un poco complicada de entender pero, practicandola, es muy amigable
    /// </summary>
    public class BaseRepository
    {
        private readonly IConfiguration _configuration;
        private readonly string _reportesConn;
        private readonly string _pmmtrafConn;

        public BaseRepository(IConfiguration configuration)
        {
            _configuration = configuration;
            _reportesConn = _configuration.GetConnectionString("ReportesDb") ?? "";
            _pmmtrafConn = _configuration.GetConnectionString("PmmtrafDb") ?? "";
        }

        /// <summary>
        /// Crea una conexión a la base de datos de administración/usuarios.
        /// </summary>
        public IDbConnection CreateReportesConnection()
        {
            return new MySqlConnection(_reportesConn);
        }

        /// <summary>
        /// Crea una conexión a la base de datos de tráfico/operación.
        /// (Dapper se encarga de que sea rápida y eficiente).
        /// </summary>
        public IDbConnection CreateConnection()
        {
            return new MySqlConnection(_pmmtrafConn);
        }
    }
}

