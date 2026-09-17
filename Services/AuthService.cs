using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

using MySqlConnector;

using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public class AuthService : IAuthService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthService> _logger;

    private readonly string _connectionString;

    public AuthService(IConfiguration configuration, ILogger<AuthService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _connectionString = _configuration.GetConnectionString("ReportesDb") ?? throw new InvalidOperationException("Connection string 'ReportesDb' not found.");
    }

    public async Task<LoginResponse> AuthenticateAsync(LoginRequest request)
    {

        try
        {
            UserData user;
            if (request.IsQR)
            {
                user = await VerifyLoginAsync(request.Username, request.Password, isQr: true);
            }
            else
            {
                string hashedPassword = CalculateCrc(request.Password).ToString();
                user = await VerifyLoginAsync(request.Username, hashedPassword, isQr: false);
                if (user == null)
                {
                    user = await VerifyWebAccesoAsync(request.Username, request.Password);
                }
            }
            if (user == null)
            {
                return new LoginResponse
                {
                    Success = false,
                    Message = "Usuario o contraseña incorrectos."
                };
            }
            string token = GenerateJwtToken(user);
            return new LoginResponse
            {
                Success = true,
                Message = "Autenticación exitosa.",
                Token = token,
                User = user
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error interno al autenticar. Username={Username}, UsernameLength={UsernameLength}, IsQR={IsQR}",
                request.Username,
                request.Username?.Length ?? 0,
                request.IsQR);

            return new LoginResponse
            {
                Success = false,
                Message = "Error interno: " + ex.Message
            };
        }
    }

    public async Task<LoginResponse> AuthenticateByUsernameAsync(string claveusuario)
    {
        try
        {
            UserData? user = await GetUsuarioActivoAsync(claveusuario);
            if (user == null)
            {
                return new LoginResponse
                {
                    Success = false,
                    Message = "Usuario o contraseña incorrectos."
                };
            }

            return new LoginResponse
            {
                Success = true,
                Message = "Autenticación exitosa.",
                Token = GenerateJwtToken(user),
                User = user
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al emitir token por usuario. Username={Username}", claveusuario);
            return new LoginResponse
            {
                Success = false,
                Message = "Error interno: " + ex.Message
            };
        }
    }

    /// <summary>
    /// Igual que VerifyLoginAsync pero sin verificar contraseña: la identidad
    /// ya viene verificada de afuera. Privado a propósito.
    /// </summary>
    private async Task<UserData?> GetUsuarioActivoAsync(string claveusuario)
    {
        using MySqlConnection connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        string sql = "SELECT usuarioid, claveusuario, CONCAT(TRIM(nombre), ' ', TRIM(apaterno)) as nombre, area, iddepto, lugar, planta, redireccionamiento FROM usuarios WHERE claveusuario = @username AND inactivo = 0";
        using MySqlCommand command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@username", claveusuario);
        using MySqlDataReader reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            string area = reader["area"]?.ToString() ?? "";
            return new UserData
            {
                IdUsuario = (reader["usuarioid"]?.ToString() ?? "0"),
                Username = (reader["claveusuario"]?.ToString() ?? ""),
                Nombre = (reader["nombre"]?.ToString() ?? ""),
                Area = area,
                Lugar = (reader["lugar"]?.ToString() ?? ""),
                Planta = (reader["planta"]?.ToString() ?? ""),
                Redireccionamiento = (reader["redireccionamiento"]?.ToString() ?? ""),
                IsCliente = false,
                EsSistemas = EsUsuarioSistemas(area, reader["iddepto"])
            };
        }
        return null;
    }

    private async Task<UserData?> VerifyLoginAsync(string username, string password, bool isQr)
    {
        using MySqlConnection connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        string column = (isQr ? "pass_qr" : "password");
        string sql = "SELECT usuarioid, claveusuario, CONCAT(TRIM(nombre), ' ', TRIM(apaterno)) as nombre, area, iddepto, lugar, planta, redireccionamiento FROM usuarios WHERE claveusuario = @username AND " + column + " = @password AND inactivo = 0";
        using MySqlCommand command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@username", username);
        command.Parameters.AddWithValue("@password", password);
        using MySqlDataReader reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            string area = reader["area"]?.ToString() ?? "";
            return new UserData
            {
                IdUsuario = (reader["usuarioid"]?.ToString() ?? "0"),
                Username = (reader["claveusuario"]?.ToString() ?? ""),
                Nombre = (reader["nombre"]?.ToString() ?? ""),
                Area = area,
                Lugar = (reader["lugar"]?.ToString() ?? ""),
                Planta = (reader["planta"]?.ToString() ?? ""),
                Redireccionamiento = (reader["redireccionamiento"]?.ToString() ?? ""),
                IsCliente = false,
                EsSistemas = EsUsuarioSistemas(area, reader["iddepto"])
            };
        }
        return null;
    }

    /// <summary>
    /// Mismo criterio que <see cref="ModulePermissionService"/>: acceso total
    /// por Area (HARDWARE/SOFTWARE, como ya lo usaba PMMovil) O por
    /// Departamento (1 = SISTEMAS, como ya lo usa PMMAdmin). Es una unión
    /// deliberada -- solo amplía quién cuenta como Sistemas, nunca se lo
    /// quita a quien ya lo tenía por el criterio anterior.
    /// </summary>
    internal static bool EsUsuarioSistemas(string? area, object? iddepto)
    {
        string normalizedArea = (area ?? string.Empty).Trim().ToUpperInvariant();
        bool esPorArea = normalizedArea is "HARDWARE" or "SOFTWARE";

        bool esPorDepartamento = iddepto is not null
            && iddepto != DBNull.Value
            && Convert.ToInt32(iddepto) == ModulePermissionService.SystemsDepartmentId;

        return esPorArea || esPorDepartamento;
    }

    public async Task<string?> GetGafetePayloadAsync(string username)
    {
        using MySqlConnection connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        string sql = "SELECT pass_qr FROM usuarios WHERE claveusuario = @username AND inactivo = 0";
        using MySqlCommand command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@username", username);
        object? result = await command.ExecuteScalarAsync();
        string? passQr = result?.ToString();
        if (string.IsNullOrEmpty(passQr))
        {
            return null;
        }
        return $"{username};{passQr}";
    }

    private async Task<UserData?> VerifyWebAccesoAsync(string username, string password)
    {
        using MySqlConnection connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        string sql = "SELECT usuarioweb, cliente, restringircta FROM webacceso WHERE usuarioweb = @username AND passweb = @password AND inactivo = 0";
        using MySqlCommand command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@username", username);
        command.Parameters.AddWithValue("@password", password);
        using MySqlDataReader reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new UserData
            {
                Username = (reader["usuarioweb"]?.ToString() ?? ""),
                Nombre = "Cliente: " + reader["cliente"],
                IsCliente = true,
                ClienteId = (reader["cliente"]?.ToString() ?? ""),
                Redireccionamiento = "DashboardRevisadores"
            };
        }
        return null;
    }

    private string GenerateJwtToken(UserData user)
    {
        IConfigurationSection jwtSettings = _configuration.GetSection("Jwt");
        SymmetricSecurityKey key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings["Secret"]));
        SigningCredentials creds = new SigningCredentials(key, "HS256");
        List<Claim> claims = new List<Claim>
        {
            new Claim("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name", user.Username),
            new Claim("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier", user.IdUsuario),
            new Claim("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname", user.Nombre),
            new Claim("Area", user.Area),
            new Claim("Lugar", user.Lugar),
            new Claim("Planta", user.Planta),
            new Claim("IsCliente", user.IsCliente.ToString()),
            new Claim("ClienteId", user.ClienteId),
            new Claim("jti", Guid.NewGuid().ToString())
        };
        string? issuer = jwtSettings["Issuer"];
        string? audience = jwtSettings["Audience"];
        DateTime? expires = DateTime.Now.AddMinutes(double.Parse(jwtSettings["ExpiryInMinutes"]));
        SigningCredentials signingCredentials = creds;
        JwtSecurityToken token = new JwtSecurityToken(issuer, audience, claims, null, expires, signingCredentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
    private uint CalculateCrc(string password)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(password);
        byte[] laPower = new byte[8] { 1, 2, 4, 8, 16, 32, 64, 128 };
        uint lnBitLen = 4129u;
        uint lnCrc = uint.MaxValue;
        byte[] array = bytes;
        foreach (byte byteVal in array)
        {
            for (int inLoop = 7; inLoop >= 0; inLoop--)
            {
                bool testBit = ((lnCrc & 0x8000) == 32768 && (byteVal & laPower[inLoop]) != laPower[inLoop]) || ((lnCrc & 0x8000) != 32768 && (byteVal & laPower[inLoop]) == laPower[inLoop]);
                lnCrc = (lnCrc & 0x7FFF) * 2;
                if (testBit)
                {
                    lnCrc ^= lnBitLen;
                }
            }
        }
        return lnCrc;
    }
}
