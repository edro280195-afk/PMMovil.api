using MySqlConnector;
using Microsoft.IdentityModel.Tokens;
using PMMovil.Api.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace PMMovil.Api.Services
{
    public interface IAuthService
    {
        Task<LoginResponse> AuthenticateAsync(LoginRequest request);
    }

    public class AuthService : IAuthService
    {
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public AuthService(IConfiguration configuration)
        {
            _configuration = configuration;
            _connectionString = _configuration.GetConnectionString("ReportesDb") ?? throw new InvalidOperationException("Connection string 'ReportesDb' not found.");
        }

        public async Task<LoginResponse> AuthenticateAsync(LoginRequest request)
        {
            try
            {
                UserData? user = null;

                if (request.IsQR)
                {
                    // Lógica para Login QR (pass_qr directo sin hash)
                    user = await VerifyLoginAsync(request.Username, request.Password, true);
                }
                else
                {
                    // Lógica para Login Estándar (hashing CRC)
                    string hashedPassword = CalculateCrc(request.Password).ToString();
                    user = await VerifyLoginAsync(request.Username, hashedPassword, false);
                    
                    // Si no entra por usuarios, intentar por webacceso (clientes)
                    if (user == null)
                    {
                        user = await VerifyWebAccesoAsync(request.Username, request.Password);
                    }
                }

                if (user == null)
                {
                    return new LoginResponse { Success = false, Message = "Usuario o contraseña incorrectos." };
                }

                var token = GenerateJwtToken(user);

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
                return new LoginResponse { Success = false, Message = $"Error interno: {ex.Message}" };
            }
        }

        private async Task<UserData?> VerifyLoginAsync(string username, string password, bool isQr)
        {
            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            string column = isQr ? "pass_qr" : "password";
            string sql = $@"SELECT id, claveusuario, 
                                   TRIM(CONCAT_WS(' ', TRIM(apaterno), TRIM(amaterno), TRIM(nombre))) as nombre, 
                                   area, lugar, redireccionamiento 
                            FROM usuarios 
                            WHERE claveusuario = @username AND {column} = @password AND inactivo = 0";

            using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@username", username);
            command.Parameters.AddWithValue("@password", password);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new UserData
                {
                    IdUsuario = reader["id"]?.ToString() ?? "0",
                    Username = reader["claveusuario"]?.ToString() ?? "",
                    Nombre = reader["nombre"]?.ToString() ?? "",
                    Area = reader["area"]?.ToString() ?? "",
                    Lugar = reader["lugar"]?.ToString() ?? "",
                    Redireccionamiento = reader["redireccionamiento"]?.ToString() ?? "",
                    IsCliente = false
                };
            }

            return null;
        }

        private async Task<UserData?> VerifyWebAccesoAsync(string username, string password)
        {
            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            string sql = @"SELECT usuarioweb, cliente, restringircta
                           FROM webacceso 
                           WHERE usuarioweb = @username AND passweb = @password AND inactivo = 0";

            using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@username", username);
            command.Parameters.AddWithValue("@password", password);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new UserData
                {
                    Username = reader["usuarioweb"]?.ToString() ?? "",
                    Nombre = "Cliente: " + reader["cliente"]?.ToString(),
                    IsCliente = true,
                    ClienteId = reader["cliente"]?.ToString() ?? "",
                    Redireccionamiento = "DashboardRevisadores" // Default para clientes
                };
            }

            return null;
        }

        private string GenerateJwtToken(UserData user)
        {
            var jwtSettings = _configuration.GetSection("Jwt");
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings["Secret"]!));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.NameIdentifier, user.IdUsuario),
                new Claim(ClaimTypes.GivenName, user.Nombre),
                new Claim("Area", user.Area),
                new Claim("Lugar", user.Lugar),
                new Claim("IsCliente", user.IsCliente.ToString()),
                new Claim("ClienteId", user.ClienteId),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var token = new JwtSecurityToken(
                issuer: jwtSettings["Issuer"],
                audience: jwtSettings["Audience"],
                claims: claims,
                expires: DateTime.Now.AddMinutes(double.Parse(jwtSettings["ExpiryInMinutes"]!)),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        // Ported CRC logic from legacy Usuario.cs
        private uint CalculateCrc(string password)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(password);
            var laPower = new byte[] { 0x1, 0x2, 0x4, 0x8, 0x10, 0x20, 0x40, 0x80 };
            uint lnBitLen = 0x1021; // 16bit seed format used in legacy
            uint lnCrc = 0xffffffff;
            
            foreach (var byteVal in bytes)
            {
                for (var inLoop = 7; inLoop >= 0; inLoop--)
                {
                    var testBit = ((lnCrc & 0x8000) == 0x8000 && (byteVal & laPower[inLoop]) != laPower[inLoop])
                                  || ((lnCrc & 0x8000) != 0x8000 && (byteVal & laPower[inLoop]) == laPower[inLoop]);
                    lnCrc = (lnCrc & 0x7fff) * 2;
                    if (testBit)
                        lnCrc = (uint)(lnCrc ^ lnBitLen);
                }
            }
            return lnCrc;
        }
    }
}
