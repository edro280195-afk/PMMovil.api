using System.Net;
using System.Text.Json.Serialization;

namespace PMMovil.Api.Models;

public class ApiResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("statusCode")]
    public int StatusCode { get; set; }

    public ApiResponse() { }

    public ApiResponse(bool success, string? message = null, int statusCode = 200)
    {
        Success = success;
        Message = message;
        StatusCode = statusCode;
    }

    public static ApiResponse Ok(string? message = null) =>
   new(true, message, 200);

    public static ApiResponse Error(string message, int statusCode = 400) =>
   new(false, message, statusCode);

    public static ApiResponse NotFound(string message = "Recurso no encontrado") =>
   new(false, message, 404);

    public static ApiResponse ServerError(string? message = null) =>
   new(false, message ?? "Error interno del servidor", 500);
}

public class ApiResponse<T> : ApiResponse
{
    [JsonPropertyName("data")]
    public T? Data { get; set; }

    public ApiResponse() { }

    public ApiResponse(bool success, T? data, string? message = null, int statusCode = 200)
   : base(success, message, statusCode)
    {
        Data = data;
    }

    public static ApiResponse<T> Ok(T data, string? message = null) =>
   new(true, data, message, 200);

    public new static ApiResponse<T> Error(string message, int statusCode = 400) =>
   new(false, default, message, statusCode);

    public new static ApiResponse<T> NotFound(string message = "Recurso no encontrado") =>
   new(false, default, message, 404);
}
